using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client.Protocol;

internal sealed class JupyterProtocolSession : IJupyterProtocolSession
{
    private const int ExecutionOutputCapacity = 512;
    private const int CompletedExecutionHistory = 256;
    private readonly IJupyterTransport transport;
    private readonly JupyterSessionIdentity session;
    private readonly ConcurrentDictionary<JupyterMessageId, PendingRequest> pendingRequests = new();
    private readonly ConcurrentDictionary<JupyterMessageId, TaskCompletionSource<bool>> readinessProbes = new();
    private readonly ConcurrentDictionary<JupyterMessageId, ExecutionState> executions = new();
    private readonly AsyncEventHub<JupyterClientEvent> events = new(256);
    private readonly CancellationTokenSource disposal = new();
    private readonly Task routerLoop;
    private readonly object completedGate = new();
    private readonly HashSet<JupyterMessageId> completedExecutions = [];
    private readonly Queue<JupyterMessageId> completedOrder = [];
    private int disposeState;

    public JupyterProtocolSession(IJupyterTransport transport, JupyterSessionIdentity? session = null)
    {
        this.transport = transport;
        this.session = session ?? JupyterSessionIdentity.Create();
        routerLoop = RouteIncomingMessagesAsync();
    }

    public async IAsyncEnumerable<JupyterClientEvent> WatchEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscription = events.SubscribeAsync(cancellationToken);
        yield return new JupyterClientConnected();
        await foreach (var item in subscription.ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public async Task<JupyterKernelInfo> GetKernelInfoAsync(CancellationToken cancellationToken = default)
    {
        var request = CreateKernelInfoRequest();
        var reply = await SendRequestAsync(
            JupyterTransportChannel.Shell,
            request,
            "kernel_info_reply",
            cancellationToken).ConfigureAwait(false);
        return ParseKernelInfo(reply);
    }

    public async Task<JupyterKernelInfo> WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeIds = new List<JupyterMessageId>();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = CreateKernelInfoRequest();
                if (!readinessProbes.TryAdd(request.Header.MessageId, ready))
                {
                    throw new InvalidOperationException(
                        $"Readiness probe '{request.Header.MessageId}' was already registered.");
                }

                probeIds.Add(request.Header.MessageId);
                var reply = await SendRequestAsync(
                    JupyterTransportChannel.Shell,
                    request,
                    "kernel_info_reply",
                    cancellationToken).ConfigureAwait(false);
                var kernelInfo = ParseKernelInfo(reply);
                if (ready.Task.IsCompletedSuccessfully)
                {
                    return kernelInfo;
                }

                await transport.PingAsync(cancellationToken).ConfigureAwait(false);
                if (ready.Task.IsCompletedSuccessfully)
                {
                    return kernelInfo;
                }
            }
        }
        finally
        {
            foreach (var probeId in probeIds)
            {
                readinessProbes.TryRemove(probeId, out _);
            }
        }
    }

    private JupyterMessage CreateKernelInfoRequest() => JupyterMessage.Create(
        "kernel_info_request",
        new JupyterEmptyContent(),
        JupyterJsonContext.Default.JupyterEmptyContent,
        session);

    private static JupyterKernelInfo ParseKernelInfo(JupyterMessage reply)
    {
        var kernelInfo = reply.GetContent(JupyterJsonContext.Default.JupyterKernelInfo);
        if (!string.Equals(kernelInfo.Status, "ok", StringComparison.Ordinal))
        {
            throw new JupyterProtocolException(
                $"Jupyter kernel_info_reply contained invalid status '{kernelInfo.Status}'.");
        }

        return kernelInfo;
    }

    public async Task<IJupyterExecution> StartExecutionAsync(
        JupyterExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var message = JupyterMessage.Create(
            "execute_request",
            request,
            JupyterJsonContext.Default.JupyterExecuteRequest,
            session);
        var outputs = Channel.CreateBounded<JupyterOutput>(new BoundedChannelOptions(ExecutionOutputCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var completion =
            new TaskCompletionSource<JupyterExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ExecutionState(message.Header, outputs, completion);

        if (!executions.TryAdd(message.Header.MessageId, state))
        {
            throw new InvalidOperationException($"Execution '{message.Header.MessageId}' was already registered.");
        }

        try
        {
            await transport.SendAsync(JupyterTransportChannel.Shell, message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            executions.TryRemove(message.Header.MessageId, out _);
            outputs.Writer.TryComplete();
            throw;
        }

        return new JupyterExecution(message.Header.MessageId, outputs.Reader, completion.Task, ReplyInputAsync);
    }

    public async Task<JupyterCompleteReply> CompleteAsync(
        JupyterCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCursorPosition(request.Code, request.CursorPosition);
        var replyMessage = await SendShellRequestAsync(
            "complete_request",
            request,
            JupyterJsonContext.Default.JupyterCompleteRequest,
            "complete_reply",
            cancellationToken).ConfigureAwait(false);
        var reply = replyMessage.GetContent(JupyterJsonContext.Default.JupyterCompleteReply);
        ThrowIfRequestFailed(reply.Status, reply.ErrorName, reply.ErrorValue, reply.Traceback, replyMessage);
        ValidateReplyCursorRange(request.Code, reply.CursorStart, reply.CursorEnd);
        return reply;
    }

    public async Task<JupyterInspectReply> InspectAsync(
        JupyterInspectRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCursorPosition(request.Code, request.CursorPosition);
        if (request.DetailLevel is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(request.DetailLevel),
                "Jupyter inspect detail level must be 0 or 1.");
        }

        var replyMessage = await SendShellRequestAsync(
            "inspect_request",
            request,
            JupyterJsonContext.Default.JupyterInspectRequest,
            "inspect_reply",
            cancellationToken).ConfigureAwait(false);
        var reply = replyMessage.GetContent(JupyterJsonContext.Default.JupyterInspectReply);
        ThrowIfRequestFailed(reply.Status, reply.ErrorName, reply.ErrorValue, reply.Traceback, replyMessage);
        return reply;
    }

    public async Task<JupyterIsCompleteReply> IsCompleteAsync(
        JupyterIsCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var replyMessage = await SendShellRequestAsync(
            "is_complete_request",
            request,
            JupyterJsonContext.Default.JupyterIsCompleteRequest,
            "is_complete_reply",
            cancellationToken).ConfigureAwait(false);
        var reply = replyMessage.GetContent(JupyterJsonContext.Default.JupyterIsCompleteReply);
        if (reply.Status is not ("complete" or "incomplete" or "invalid" or "unknown"))
        {
            throw new JupyterProtocolException($"Jupyter is_complete_reply contained invalid status '{reply.Status}'.");
        }

        return reply;
    }

    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default) =>
        transport.PingAsync(cancellationToken);

    public async Task<JupyterInterruptReply> InterruptAsync(CancellationToken cancellationToken = default)
    {
        var request = JupyterMessage.Create(
            "interrupt_request",
            new JupyterEmptyContent(),
            JupyterJsonContext.Default.JupyterEmptyContent,
            session);
        var reply = await SendRequestAsync(
            JupyterTransportChannel.Control,
            request,
            "interrupt_reply",
            cancellationToken).ConfigureAwait(false);
        return reply.GetContent(JupyterJsonContext.Default.JupyterInterruptReply);
    }

    public async Task<JupyterShutdownReply> ShutdownAsync(
        bool restart,
        CancellationToken cancellationToken = default)
    {
        var request = JupyterMessage.Create(
            "shutdown_request",
            new JupyterShutdownRequest(restart),
            JupyterJsonContext.Default.JupyterShutdownRequest,
            session);
        var reply = await SendRequestAsync(
            JupyterTransportChannel.Control,
            request,
            "shutdown_reply",
            cancellationToken).ConfigureAwait(false);
        var shutdownReply = reply.GetContent(JupyterJsonContext.Default.JupyterShutdownReply);
        if (!string.Equals(shutdownReply.Status, "ok", StringComparison.Ordinal))
        {
            throw new JupyterProtocolException(
                $"Jupyter shutdown_reply contained invalid status '{shutdownReply.Status}'.");
        }

        return shutdownReply;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        await disposal.CancelAsync().ConfigureAwait(false);
        await transport.DisposeAsync().ConfigureAwait(false);
        try
        {
            await routerLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }

        var exception = new ObjectDisposedException(nameof(JupyterProtocolSession));
        FailPending(exception);
        FailExecutions(exception);
        events.Publish(new JupyterClientDisconnected(null));
        events.Complete();
        disposal.Dispose();
    }

    private async Task<JupyterMessage> SendRequestAsync(
        JupyterTransportChannel channel,
        JupyterMessage request,
        string expectedReplyType,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var completion = new TaskCompletionSource<JupyterMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(channel, expectedReplyType, completion);
        if (!pendingRequests.TryAdd(request.Header.MessageId, pending))
        {
            throw new InvalidOperationException($"Request '{request.Header.MessageId}' was already registered.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            if (pendingRequests.TryRemove(request.Header.MessageId, out var removed))
            {
                removed.Completion.TrySetCanceled(cancellationToken);
            }
        });

        try
        {
            await transport.SendAsync(channel, request, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pendingRequests.TryRemove(request.Header.MessageId, out _);
            throw;
        }
    }

    private Task<JupyterMessage> SendShellRequestAsync<TRequest>(
        string requestType,
        TRequest content,
        JsonTypeInfo<TRequest> contentType,
        string replyType,
        CancellationToken cancellationToken)
    {
        var request = JupyterMessage.Create(requestType, content, contentType, session);
        return SendRequestAsync(JupyterTransportChannel.Shell, request, replyType, cancellationToken);
    }

    private static void ValidateCursorPosition(string code, int cursorPosition)
    {
        ArgumentNullException.ThrowIfNull(code);
        JupyterCursorPosition.ToUtf16Index(code, cursorPosition);
    }

    private static void ValidateReplyCursorRange(string code, int cursorStart, int cursorEnd)
    {
        if (cursorStart > cursorEnd)
        {
            throw new JupyterProtocolException("Jupyter complete_reply cursor_start exceeded cursor_end.");
        }

        try
        {
            JupyterCursorPosition.ToUtf16Index(code, cursorStart);
            JupyterCursorPosition.ToUtf16Index(code, cursorEnd);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new JupyterProtocolException("Jupyter complete_reply contained an invalid cursor range.", exception);
        }
    }

    private static void ThrowIfRequestFailed(
        string status,
        string? errorName,
        string? errorValue,
        IReadOnlyList<string>? traceback,
        JupyterMessage rawReply)
    {
        if (string.Equals(status, "ok", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(status, "error", StringComparison.Ordinal))
        {
            throw new JupyterRequestException(
                errorName ?? "JupyterError",
                errorValue ?? "The Jupyter kernel rejected the request.",
                traceback ?? [],
                rawReply);
        }

        throw new JupyterProtocolException(
            $"Jupyter reply '{rawReply.MessageType}' contained invalid status '{status}'.");
    }

    private async Task ReplyInputAsync(
        JupyterInputRequest request,
        string value,
        CancellationToken cancellationToken)
    {
        if (request.Header is null)
        {
            throw new ArgumentException("The input request did not originate from this client session.",
                nameof(request));
        }

        var reply = JupyterMessage.Create(
            "input_reply",
            new JupyterInputReply(value),
            JupyterJsonContext.Default.JupyterInputReply,
            session,
            request.Header);
        await transport.SendAsync(JupyterTransportChannel.Stdin, reply, cancellationToken).ConfigureAwait(false);
    }

    private async Task RouteIncomingMessagesAsync()
    {
        try
        {
            await foreach (var transportMessage in transport.IncomingMessages.WithCancellation(disposal.Token)
                               .ConfigureAwait(false))
            {
                RouteMessage(transportMessage);
            }
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPending(exception);
            FailExecutions(exception);
            events.Publish(new JupyterClientDisconnected(exception));
            events.Complete(exception);
        }
    }

    private void RouteMessage(JupyterTransportMessage transportMessage)
    {
        var message = transportMessage.Message;
        if (transportMessage.Channel == JupyterTransportChannel.Iopub &&
            message.MessageType == "status" &&
            message.ParentHeader is { } readinessParent &&
            readinessProbes.TryGetValue(readinessParent.MessageId, out var readiness) &&
            message.GetContent(JupyterJsonContext.Default.JupyterStatus).ExecutionState == "idle")
        {
            readiness.TrySetResult(true);
        }

        if (message.ParentHeader is { } parent &&
            pendingRequests.TryGetValue(parent.MessageId, out var pending))
        {
            var replyMatched = pending.Channel == transportMessage.Channel &&
                               string.Equals(pending.ExpectedReplyType, message.MessageType,
                                   StringComparison.Ordinal);
            if (replyMatched)
            {
                if (pendingRequests.TryRemove(parent.MessageId, out _))
                {
                    pending.Completion.TrySetResult(message);
                }

                return;
            }
        }

        if (message.ParentHeader is { } executionParent &&
            executions.TryGetValue(executionParent.MessageId, out var execution))
        {
            RouteExecutionMessage(execution, transportMessage);
            return;
        }

        if (transportMessage.Channel == JupyterTransportChannel.Iopub &&
            message.ParentHeader is { } lateParent &&
            IsCompletedExecution(lateParent.MessageId))
        {
            var lateOutput = new JupyterLateOutput(lateParent.MessageId, message);
            if (TryCreateOutput(lateParent.MessageId, message, out var output))
            {
                lateOutput = lateOutput with { Output = output };
            }

            events.Publish(lateOutput);
            return;
        }

        RouteGlobalMessage(transportMessage);
    }

    private void RouteExecutionMessage(ExecutionState execution, JupyterTransportMessage transportMessage)
    {
        var message = transportMessage.Message;
        if (transportMessage.Channel == JupyterTransportChannel.Shell && message.MessageType == "execute_reply")
        {
            execution.ReplyMessage = message;
            execution.Reply = message.GetContent(JupyterJsonContext.Default.JupyterExecuteReply);
            CompleteExecutionIfReady(execution);
            return;
        }

        if (TryCreateOutput(execution.RequestHeader.MessageId, message, out var output) &&
            !execution.Outputs.Writer.TryWrite(output))
        {
            FailExecution(execution, new JupyterBackpressureException("The execution output queue is full."));
            return;
        }

        if (message.MessageType == "status" &&
            message.GetContent(JupyterJsonContext.Default.JupyterStatus).ExecutionState == "idle")
        {
            execution.IdleSeen = true;
            CompleteExecutionIfReady(execution);
        }
    }

    private void CompleteExecutionIfReady(ExecutionState execution)
    {
        if (!execution.IdleSeen || execution.Reply is null || execution.ReplyMessage is null)
        {
            return;
        }

        if (!executions.TryRemove(execution.RequestHeader.MessageId, out _))
        {
            return;
        }

        execution.Outputs.Writer.TryComplete();
        execution.Completion.TrySetResult(new JupyterExecutionResult(execution.Reply, execution.ReplyMessage));
        RememberCompletedExecution(execution.RequestHeader.MessageId);
    }


    private static bool TryCreateOutput(JupyterMessageId requestId, JupyterMessage message,
        [NotNullWhen(true)] out JupyterOutput? output)
    {
        output = message.MessageType switch
        {
            "stream" => CreateStreamOutput(requestId, message),
            "execute_input" => CreateExecuteInputOutput(requestId, message),
            "display_data" => CreateDisplayOutput(requestId, message),
            "update_display_data" => CreateDisplayUpdateOutput(requestId, message),
            "clear_output" => CreateClearOutput(requestId, message),
            "execute_result" => CreateExecuteResultOutput(requestId, message),
            "error" => CreateErrorOutput(requestId, message),
            "input_request" => CreateInputRequest(requestId, message),
            "status" => new JupyterExecutionStatusChanged(
                requestId,
                ParseKernelState(message.GetContent(JupyterJsonContext.Default.JupyterStatus).ExecutionState)),
            _ => null
        };
        return output is not null;
    }

    private static JupyterOutput CreateStreamOutput(JupyterMessageId requestId, JupyterMessage message)
    {
        var stream = message.GetContent(JupyterJsonContext.Default.JupyterStream);
        return stream.Name == "stderr"
            ? new JupyterStderr(requestId, stream.Text)
            : new JupyterStdout(requestId, stream.Text);
    }

    private static JupyterOutput CreateDisplayOutput(JupyterMessageId requestId, JupyterMessage message)
    {
        var display = message.GetContent(JupyterJsonContext.Default.JupyterDisplayData);
        return new JupyterDisplayOutput(requestId, new MimeBundle(display.Data), display.Metadata)
        {
            Transient = display.Transient,
            DisplayId = JupyterDisplayTransient.GetDisplayId(display.Transient)
        };
    }

    private static JupyterOutput CreateDisplayUpdateOutput(JupyterMessageId requestId, JupyterMessage message)
    {
        var update = message.GetContent(JupyterJsonContext.Default.JupyterUpdateDisplayData);
        var displayId = JupyterDisplayTransient.GetDisplayId(update.Transient)
                        ?? throw new JupyterProtocolException(
                            "Jupyter update_display_data must contain transient.display_id.");
        return new JupyterDisplayUpdateOutput(
            requestId,
            new MimeBundle(update.Data),
            update.Metadata,
            update.Transient,
            displayId);
    }

    private static JupyterOutput CreateClearOutput(JupyterMessageId requestId, JupyterMessage message)
    {
        var clear = message.GetContent(JupyterJsonContext.Default.JupyterClearOutputContent);
        return new JupyterClearOutput(requestId, clear.Wait);
    }

    private static JupyterOutput CreateExecuteInputOutput(JupyterMessageId requestId, JupyterMessage message)
    {
        var input = message.GetContent(JupyterJsonContext.Default.JupyterExecuteInput);
        return new JupyterExecuteInputOutput(requestId, input.Code, input.ExecutionCount);
    }

    private static JupyterOutput CreateExecuteResultOutput(JupyterMessageId requestId, JupyterMessage message)
    {
        var result = message.GetContent(JupyterJsonContext.Default.JupyterExecuteResultData);
        return new JupyterExecuteResultOutput(requestId, new MimeBundle(result.Data), result.Metadata,
            result.ExecutionCount);
    }

    private static JupyterOutput CreateErrorOutput(JupyterMessageId requestId, JupyterMessage message)
    {
        var error = message.GetContent(JupyterJsonContext.Default.JupyterError);
        return new JupyterExecutionError(requestId, error.Name, error.Value, error.Traceback);
    }

    private static JupyterOutput CreateInputRequest(JupyterMessageId requestId, JupyterMessage message)
    {
        var input = message.GetContent(JupyterJsonContext.Default.JupyterInputRequestContent);
        return new JupyterInputRequest(requestId, message.Header.MessageId, input.Prompt, input.Password)
        {
            Header = message.Header
        };
    }

    private void RouteGlobalMessage(JupyterTransportMessage transportMessage)
    {
        var message = transportMessage.Message;
        if (message.MessageType == "status")
        {
            var status = message.GetContent(JupyterJsonContext.Default.JupyterStatus);
            events.Publish(new JupyterKernelStatusChanged(ParseKernelState(status.ExecutionState)));
            return;
        }

        if (message.MessageType == "iopub_welcome")
        {
            return;
        }

        events.Publish(new JupyterUnhandledMessage(transportMessage.Channel, message));
    }

    private static JupyterKernelState ParseKernelState(string value) => value switch
    {
        "starting" => JupyterKernelState.Starting,
        "idle" => JupyterKernelState.Idle,
        "busy" => JupyterKernelState.Busy,
        _ => JupyterKernelState.Unknown
    };

    private void FailPending(Exception exception)
    {
        foreach (var pair in pendingRequests)
        {
            if (pendingRequests.TryRemove(pair.Key, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private void FailExecutions(Exception exception)
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

    private void FailExecution(ExecutionState execution, Exception exception)
    {
        executions.TryRemove(execution.RequestHeader.MessageId, out _);
        execution.Outputs.Writer.TryComplete(exception);
        execution.Completion.TrySetException(exception);
    }

    private void RememberCompletedExecution(JupyterMessageId requestId)
    {
        lock (completedGate)
        {
            completedExecutions.Add(requestId);
            completedOrder.Enqueue(requestId);
            while (completedOrder.Count > CompletedExecutionHistory)
            {
                completedExecutions.Remove(completedOrder.Dequeue());
            }
        }
    }

    private bool IsCompletedExecution(JupyterMessageId requestId)
    {
        lock (completedGate)
        {
            return completedExecutions.Contains(requestId);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);

    private sealed class PendingRequest(
        JupyterTransportChannel channel,
        string expectedReplyType,
        TaskCompletionSource<JupyterMessage> completion)
    {
        public JupyterTransportChannel Channel { get; } = channel;

        public string ExpectedReplyType { get; } = expectedReplyType;

        public TaskCompletionSource<JupyterMessage> Completion { get; } = completion;
    }

    private sealed class ExecutionState(
        JupyterMessageHeader requestHeader,
        Channel<JupyterOutput> outputs,
        TaskCompletionSource<JupyterExecutionResult> completion)
    {
        public JupyterMessageHeader RequestHeader { get; } = requestHeader;

        public Channel<JupyterOutput> Outputs { get; } = outputs;

        public TaskCompletionSource<JupyterExecutionResult> Completion { get; } = completion;

        public JupyterExecuteReply? Reply { get; set; }

        public JupyterMessage? ReplyMessage { get; set; }

        public bool IdleSeen { get; set; }
    }
}