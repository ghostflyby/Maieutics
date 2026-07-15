using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client.Protocol;

public sealed class JupyterProtocolSession : IJupyterProtocolSession
{
    private readonly IJupyterTransport transport;
    private readonly JupyterSessionIdentity session;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JupyterMessage>> pendingRequests = new();
    private readonly ConcurrentDictionary<string, ExecutionState> executions = new();
    private readonly Channel<KernelEvent> events = Channel.CreateUnbounded<KernelEvent>();
    private readonly CancellationTokenSource disposal = new();
    private readonly Task routerLoop;
    private bool disposed;

    public JupyterProtocolSession(IJupyterTransport transport, JupyterSessionIdentity? session = null)
    {
        this.transport = transport;
        this.session = session ?? JupyterSessionIdentity.Create();
        events.Writer.TryWrite(new Connected());
        routerLoop = Task.Run(RouteIncomingMessagesAsync);
    }

    public IAsyncEnumerable<KernelEvent> Events => events.Reader.ReadAllAsync();

    public async Task<KernelInfoReply> GetKernelInfoAsync(CancellationToken cancellationToken = default)
    {
        var request = JupyterMessage.Create("kernel_info_request", new JsonObject(), session);
        var reply = await SendRequestAsync(request, cancellationToken);

        return new KernelInfoReply(
            reply.Content["implementation"]?.GetValue<string>() ?? string.Empty,
            reply.Content["implementation_version"]?.GetValue<string>() ?? string.Empty,
            reply.Content["language_info"]?["name"]?.GetValue<string>() ?? string.Empty,
            reply);
    }

    public async Task<IJupyterExecution> StartExecutionAsync(
        ExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var message = JupyterMessage.Create("execute_request", request.ToContent(), session);
        var outputChannel = Channel.CreateUnbounded<KernelOutput>();
        var completion = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ExecutionState(message.Header, outputChannel, completion);

        if (!executions.TryAdd(message.Header.MessageId, state))
        {
            throw new InvalidOperationException($"Execution '{message.Header.MessageId}' was already registered.");
        }

        await transport.SendAsync(JupyterTransportChannel.Shell, message, cancellationToken);

        return new JupyterExecution(
            message.Header,
            outputChannel,
            completion,
            ReplyInputAsync,
            _ => Task.CompletedTask);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await disposal.CancelAsync();
        FailPending(new ObjectDisposedException(nameof(JupyterProtocolSession)));
        CompleteExecutions(new ObjectDisposedException(nameof(JupyterProtocolSession)));
        events.Writer.TryWrite(new Disconnected(null));
        events.Writer.TryComplete();

        try
        {
            await routerLoop;
        }
        catch (OperationCanceledException)
        {
        }

        await transport.DisposeAsync();
        disposal.Dispose();
    }

    private async Task<JupyterMessage> SendRequestAsync(
        JupyterMessage request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var completion = new TaskCompletionSource<JupyterMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingRequests.TryAdd(request.Header.MessageId, completion))
        {
            throw new InvalidOperationException($"Request '{request.Header.MessageId}' was already registered.");
        }

        await using var registration = cancellationToken.Register(() =>
        {
            if (pendingRequests.TryRemove(request.Header.MessageId, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        await transport.SendAsync(JupyterTransportChannel.Shell, request, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task ReplyInputAsync(
        InputRequestId requestId,
        string value,
        CancellationToken cancellationToken = default)
    {
        var content = new JsonObject
        {
            ["value"] = value
        };
        var message = JupyterMessage.Create("input_reply", content, session);
        await transport.SendAsync(JupyterTransportChannel.Stdin, message, cancellationToken);
    }

    private async Task RouteIncomingMessagesAsync()
    {
        try
        {
            await foreach (var transportMessage in transport.IncomingMessages.WithCancellation(disposal.Token))
            {
                RouteMessage(transportMessage);
            }
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FailPending(ex);
            CompleteExecutions(ex);
            events.Writer.TryWrite(new Disconnected(ex));
            events.Writer.TryComplete(ex);
        }
    }

    private void RouteMessage(JupyterTransportMessage transportMessage)
    {
        var message = transportMessage.Message;
        if (IsReply(message) &&
            message.ParentHeader is { } parentHeader &&
            pendingRequests.TryRemove(parentHeader.MessageId, out var pendingRequest))
        {
            pendingRequest.TrySetResult(message);
            return;
        }

        if (message.ParentHeader is { } executionParent &&
            executions.TryGetValue(executionParent.MessageId, out var execution))
        {
            RouteExecutionMessage(execution, transportMessage);
            return;
        }

        RouteGlobalMessage(message);
    }

    private void RouteExecutionMessage(ExecutionState execution, JupyterTransportMessage transportMessage)
    {
        var message = transportMessage.Message;

        if (message.MessageType == "execute_reply")
        {
            execution.Reply = message;
            CompleteExecutionResult(execution);
            _ = CompleteExecutionOutputsAfterDelayAsync(execution, TimeSpan.FromMilliseconds(250));
            return;
        }

        if (TryCreateOutput(execution.RequestHeader.MessageId, transportMessage, out var output))
        {
            execution.Outputs.Writer.TryWrite(output);
        }

        if (IsIdleStatus(message))
        {
            execution.IdleSeen = true;
            CompleteExecutionOutputsIfReady(execution);
        }
    }

    private void CompleteExecutionResult(ExecutionState execution)
    {
        if (execution.Reply is null)
        {
            return;
        }

        var reply = execution.Reply;
        execution.Completion.TrySetResult(new ExecutionResult(
            reply.Content["status"]?.GetValue<string>() ?? "unknown",
            reply.Content["execution_count"]?.GetValue<int?>(),
            reply));
    }

    private async Task CompleteExecutionOutputsAfterDelayAsync(
        ExecutionState execution,
        TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, disposal.Token);
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
            return;
        }

        CompleteExecutionOutputs(execution);
    }

    private void CompleteExecutionOutputs(ExecutionState execution)
    {
        if (!executions.TryRemove(execution.RequestHeader.MessageId, out _))
        {
            return;
        }

        execution.Outputs.Writer.TryComplete();
    }

    private void CompleteExecutionOutputsIfReady(ExecutionState execution)
    {
        if (execution.Reply is null || !execution.IdleSeen)
        {
            return;
        }

        CompleteExecutionOutputs(execution);
    }

    private void RouteGlobalMessage(JupyterMessage message)
    {
        if (message.MessageType == "status")
        {
            var state = ParseKernelState(message.Content["execution_state"]?.GetValue<string>());
            events.Writer.TryWrite(new KernelStatusChanged(state));
            if (state == KernelState.Idle)
            {
                MarkActiveExecutionsIdle();
            }

            return;
        }

        events.Writer.TryWrite(new UnhandledMessage(message));
    }

    private bool TryCreateOutput(
        string executionId,
        JupyterTransportMessage transportMessage,
        out KernelOutput output)
    {
        var message = transportMessage.Message;
        output = message.MessageType switch
        {
            "stream" when message.Content["name"]?.GetValue<string>() == "stdout" =>
                new Stdout(executionId, message.Content["text"]?.GetValue<string>() ?? string.Empty),
            "stream" when message.Content["name"]?.GetValue<string>() == "stderr" =>
                new Stderr(executionId, message.Content["text"]?.GetValue<string>() ?? string.Empty),
            "display_data" => new DisplayData(
                executionId,
                MimeBundle.FromJsonObject(message.Content["data"]?.AsObject()),
                ToDictionary(message.Content["metadata"]?.AsObject())),
            "execute_result" => new ExecuteResultOutput(
                executionId,
                MimeBundle.FromJsonObject(message.Content["data"]?.AsObject()),
                message.Content["execution_count"]?.GetValue<int?>()),
            "error" => new ExecutionError(
                executionId,
                message.Content["ename"]?.GetValue<string>() ?? string.Empty,
                message.Content["evalue"]?.GetValue<string>() ?? string.Empty,
                message.Content["traceback"]?.AsArray()
                    .Select(item => item?.GetValue<string>() ?? string.Empty)
                    .ToArray() ?? []),
            "input_request" => new InputRequest(
                executionId,
                new InputRequestId(message.Header.MessageId),
                message.Content["prompt"]?.GetValue<string>() ?? string.Empty,
                message.Content["password"]?.GetValue<bool?>() ?? false),
            "status" => new ExecutionStatusChanged(
                executionId,
                ParseKernelState(message.Content["execution_state"]?.GetValue<string>())),
            _ => null!
        };

        return output is not null;
    }

    private static bool IsReply(JupyterMessage message)
    {
        return message.MessageType.EndsWith("_reply", StringComparison.Ordinal);
    }

    private static bool IsIdleStatus(JupyterMessage message)
    {
        return message.MessageType == "status" &&
               message.Content["execution_state"]?.GetValue<string>() == "idle";
    }

    private static KernelState ParseKernelState(string? value)
    {
        return value switch
        {
            "starting" => KernelState.Starting,
            "idle" => KernelState.Idle,
            "busy" => KernelState.Busy,
            _ => KernelState.Unknown
        };
    }

    private static IReadOnlyDictionary<string, JsonNode?> ToDictionary(JsonObject? obj)
    {
        if (obj is null)
        {
            return new Dictionary<string, JsonNode?>();
        }

        return obj.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone());
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in pendingRequests)
        {
            if (pendingRequests.TryRemove(pair.Key, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private void CompleteExecutions(Exception exception)
    {
        foreach (var pair in executions)
        {
            if (executions.TryRemove(pair.Key, out var execution))
            {
                execution.Outputs.Writer.TryComplete(exception);
                execution.Completion.TrySetException(exception);
            }
        }
    }

    private void MarkActiveExecutionsIdle()
    {
        foreach (var execution in executions.Values)
        {
            execution.IdleSeen = true;
            CompleteExecutionOutputsIfReady(execution);
        }
    }

    private sealed class ExecutionState(
        JupyterMessageHeader requestHeader,
        Channel<KernelOutput> outputs,
        TaskCompletionSource<ExecutionResult> completion)
    {
        public JupyterMessageHeader RequestHeader { get; } = requestHeader;

        public Channel<KernelOutput> Outputs { get; } = outputs;

        public TaskCompletionSource<ExecutionResult> Completion { get; } = completion;

        public JupyterMessage? Reply { get; set; }

        public bool IdleSeen { get; set; }
    }
}