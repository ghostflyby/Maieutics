using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Maieutics.Jupyter.Kernel.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Kernel;

public sealed class JupyterKernelHost : IJupyterKernel
{
    private readonly IJupyterKernelApplication application;
    private readonly Task controlLoop;
    private readonly Channel<JupyterWireMessage> controlRequests = Channel.CreateBounded<JupyterWireMessage>(32);
    private readonly Lock executionGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private JupyterExecutionContext? currentExecutionContext;
    private readonly ConcurrentDictionary<JupyterMessageId, TaskCompletionSource<string>> pendingInputs = new();
    private readonly Task routerLoop;
    private readonly JupyterSessionIdentity session;
    private readonly Task shellLoop;
    private readonly Channel<JupyterWireMessage> shellRequests = Channel.CreateBounded<JupyterWireMessage>(128);
    private readonly IJupyterKernelTransport transport;
    private CancellationTokenSource? currentExecution;
    private int disposeState;
    private int executionCount;
    private int iopubInitialized;

    private JupyterKernelHost(
        IJupyterKernelApplication application,
        IJupyterKernelTransport transport,
        JupyterSessionIdentity session)
    {
        this.application = application;
        this.transport = transport;
        this.session = session;
        routerLoop = RouteIncomingAsync();
        shellLoop = ProcessShellAsync();
        controlLoop = ProcessControlAsync();
        Completion = CompleteHostAsync();
    }

    public bool RestartRequested { get; private set; }

    public Task Completion { get; }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0) return;

        await StopAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    public static async Task<JupyterKernelHost> StartAsync(
        JupyterConnectionInfo connectionInfo,
        IJupyterKernelApplication application,
        JupyterSessionIdentity? session = null,
        CancellationToken cancellationToken = default)
    {
        var transport = await ZmqSharpJupyterKernelTransport.BindAsync(
            connectionInfo,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new JupyterKernelHost(application, transport, session ?? JupyterSessionIdentity.Create("kernel"));
    }

    /// <summary>
    ///     Requests cooperative cancellation of the currently executing request without a Jupyter
    ///     control-channel round trip. This mirrors the <c>interrupt_request</c> control message and
    ///     is a no-op when no request is executing.
    /// </summary>
    /// <remarks>
    ///     The request is idempotent, thread-safe, and safe to call concurrently with shutdown.
    /// </remarks>
    public void RequestInterrupt()
    {
        CancelCurrentExecution();
    }

    /// <inheritdoc />
    public async ValueTask SendCommAsync(JupyterCommMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var wireMessage = message.WireMessage;
        var outgoing = new JupyterWireMessage(
            wireMessage.Identities,
            message.Kind switch
            {
                JupyterCommKind.Open => JupyterMessage.Create(
                    "comm_open",
                    new JupyterCommOpenContent(
                        message.CommId,
                        message.TargetName ?? string.Empty,
                        message.Data),
                    JupyterJsonContext.Default.JupyterCommOpenContent,
                    session,
                    metadata: message.Metadata),
                JupyterCommKind.Close => JupyterMessage.Create(
                    "comm_close",
                    new JupyterCommCloseContent(message.CommId, message.Data),
                    JupyterJsonContext.Default.JupyterCommCloseContent,
                    session),
                _ => JupyterMessage.Create(
                    "comm_msg",
                    new JupyterCommMsgContent(message.CommId, message.Data),
                    JupyterJsonContext.Default.JupyterCommMsgContent,
                    session)
            },
            message.Buffers);
        await transport.SendAsync(JupyterKernelChannel.Iopub, outgoing, cancellationToken).ConfigureAwait(false);
    }

    private async Task RouteIncomingAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var incoming in transport.IncomingEvents.WithCancellation(lifetime.Token)
                               .ConfigureAwait(false))
                switch (incoming)
                {
                    case JupyterKernelMessageReceived { Channel: JupyterKernelChannel.Shell } shell:
                        await shellRequests.Writer.WriteAsync(shell.WireMessage, lifetime.Token).ConfigureAwait(false);
                        break;
                    case JupyterKernelMessageReceived { Channel: JupyterKernelChannel.Control } control:
                        await controlRequests.Writer.WriteAsync(control.WireMessage, lifetime.Token)
                            .ConfigureAwait(false);
                        break;
                    case JupyterKernelMessageReceived { Channel: JupyterKernelChannel.Stdin } stdin:
                        RouteInputReply(stdin.WireMessage.Message);
                        break;
                    case JupyterIopubSubscriptionReceived subscription:
                        if (Interlocked.Exchange(ref iopubInitialized, 1) == 0)
                        {
                            await SendIopubWelcomeAsync(subscription.Topic, lifetime.Token).ConfigureAwait(false);
                            await PublishStatusAsync("starting", null, lifetime.Token).ConfigureAwait(false);
                        }

                        break;
                }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            shellRequests.Writer.TryComplete(failure);
            controlRequests.Writer.TryComplete(failure);
        }
    }

    private async Task ProcessShellAsync()
    {
        await foreach (var request in shellRequests.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            await ProcessWithStatusAsync(request, JupyterKernelChannel.Shell, HandleShellRequestAsync)
                .ConfigureAwait(false);
    }

    private async Task ProcessControlAsync()
    {
        await foreach (var request in controlRequests.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            await ProcessWithStatusAsync(request, JupyterKernelChannel.Control, HandleControlRequestAsync)
                .ConfigureAwait(false);
    }

    private async Task ProcessWithStatusAsync(
        JupyterWireMessage request,
        JupyterKernelChannel channel,
        Func<JupyterWireMessage, JupyterKernelChannel, Task<bool>> handler)
    {
        await PublishStatusAsync("busy", request.Message.Header, lifetime.Token).ConfigureAwait(false);
        bool stop;
        try
        {
            stop = await handler(request, channel).ConfigureAwait(false);
        }
        finally
        {
            await PublishStatusAsync("idle", request.Message.Header, CancellationToken.None).ConfigureAwait(false);
        }

        if (stop) await lifetime.CancelAsync().ConfigureAwait(false);
    }

    private async Task<bool> HandleShellRequestAsync(JupyterWireMessage request, JupyterKernelChannel channel)
    {
        switch (request.Message.MessageType)
        {
            case "kernel_info_request":
                await SendReplyAsync(
                    channel,
                    request,
                    "kernel_info_reply",
                    application.KernelInfo,
                    JupyterJsonContext.Default.JupyterKernelInfo,
                    lifetime.Token).ConfigureAwait(false);
                return false;
            case "execute_request":
                await ExecuteAsync(request).ConfigureAwait(false);
                return false;
            case "complete_request":
                await CompleteAsync(request, channel).ConfigureAwait(false);
                return false;
            case "inspect_request":
                await InspectAsync(request, channel).ConfigureAwait(false);
                return false;
            case "is_complete_request":
                await IsCompleteAsync(request, channel).ConfigureAwait(false);
                return false;
            case "shutdown_request":
                return await HandleShutdownAsync(request, channel).ConfigureAwait(false);
            case "comm_open":
                await HandleCommAsync(JupyterCommKind.Open, request).ConfigureAwait(false);
                return false;
            case "comm_msg":
                await HandleCommAsync(JupyterCommKind.Message, request).ConfigureAwait(false);
                return false;
            case "comm_close":
                await HandleCommAsync(JupyterCommKind.Close, request).ConfigureAwait(false);
                return false;
            default:
                return false;
        }
    }

    private async Task<bool> HandleControlRequestAsync(JupyterWireMessage request, JupyterKernelChannel channel)
    {
        switch (request.Message.MessageType)
        {
            case "kernel_info_request":
                await SendReplyAsync(
                    channel,
                    request,
                    "kernel_info_reply",
                    application.KernelInfo,
                    JupyterJsonContext.Default.JupyterKernelInfo,
                    lifetime.Token).ConfigureAwait(false);
                return false;
            case "interrupt_request":
                CancelCurrentExecution();
                await SendReplyAsync(
                    channel,
                    request,
                    "interrupt_reply",
                    new JupyterInterruptReply("ok"),
                    JupyterJsonContext.Default.JupyterInterruptReply,
                    lifetime.Token).ConfigureAwait(false);
                return false;
            case "shutdown_request":
                return await HandleShutdownAsync(request, channel).ConfigureAwait(false);
            default:
                return false;
        }
    }

    private async Task ExecuteAsync(JupyterWireMessage wireRequest)
    {
        var request = wireRequest.Message.GetContent(JupyterJsonContext.Default.JupyterExecuteRequest);
        var historyEnabled = !request.Silent && request.StoreHistory;
        var count = historyEnabled ? Interlocked.Increment(ref executionCount) : Volatile.Read(ref executionCount);

        if (!request.Silent)
            await PublishAsync(
                "execute_input",
                new JupyterExecuteInput(request.Code, count),
                JupyterJsonContext.Default.JupyterExecuteInput,
                wireRequest.Message.Header,
                lifetime.Token).ConfigureAwait(false);

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var context = CreateExecutionContext(wireRequest, request, count);
        lock (executionGate)
        {
            currentExecution = execution;
            currentExecutionContext = context;
        }

        try
        {
            var result = await application.ExecuteAsync(context, request, execution.Token).ConfigureAwait(false);
            await SendReplyAsync(
                JupyterKernelChannel.Shell,
                wireRequest,
                "execute_reply",
                new JupyterExecuteReply(result.Status, count),
                JupyterJsonContext.Default.JupyterExecuteReply,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            await SendReplyAsync(
                JupyterKernelChannel.Shell,
                wireRequest,
                "execute_reply",
                new JupyterExecuteReply("aborted", count),
                JupyterJsonContext.Default.JupyterExecuteReply,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var error = ToJupyterError(exception);
            if (!request.Silent)
                await PublishAsync(
                    "error",
                    error,
                    JupyterJsonContext.Default.JupyterError,
                    wireRequest.Message.Header,
                    CancellationToken.None).ConfigureAwait(false);

            await SendReplyAsync(
                JupyterKernelChannel.Shell,
                wireRequest,
                "execute_reply",
                new JupyterExecuteReply("error", count, ErrorName: error.Name, ErrorValue: error.Value,
                    Traceback: error.Traceback),
                JupyterJsonContext.Default.JupyterExecuteReply,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (executionGate)
            {
                if (ReferenceEquals(currentExecution, execution)) currentExecution = null;
                if (ReferenceEquals(currentExecutionContext, context)) currentExecutionContext = null;
            }
        }
    }

    private async Task CompleteAsync(JupyterWireMessage wireRequest, JupyterKernelChannel channel)
    {
        var request = wireRequest.Message.GetContent(JupyterJsonContext.Default.JupyterCompleteRequest);
        try
        {
            ValidateCursorPosition(request.Code, request.CursorPosition);
            if (application is not IJupyterCompletionProvider provider)
                throw new JupyterKernelExecutionException(
                    "NotSupported",
                    "The Jupyter kernel does not provide code completion.");

            var result = await provider.CompleteAsync(request, lifetime.Token).ConfigureAwait(false);
            ValidateCursorRange(request.Code, result.CursorStart, result.CursorEnd);
            await SendReplyAsync(
                channel,
                wireRequest,
                "complete_reply",
                new JupyterCompleteReply
                {
                    Status = "ok",
                    Matches = result.Matches,
                    CursorStart = result.CursorStart,
                    CursorEnd = result.CursorEnd,
                    Metadata = result.Metadata ?? new Dictionary<string, JsonElement>()
                },
                JupyterJsonContext.Default.JupyterCompleteReply,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var error = ToJupyterError(exception);
            await SendReplyAsync(
                channel,
                wireRequest,
                "complete_reply",
                new JupyterCompleteReply
                {
                    Status = "error",
                    ErrorName = error.Name,
                    ErrorValue = error.Value,
                    Traceback = error.Traceback
                },
                JupyterJsonContext.Default.JupyterCompleteReply,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task InspectAsync(JupyterWireMessage wireRequest, JupyterKernelChannel channel)
    {
        var request = wireRequest.Message.GetContent(JupyterJsonContext.Default.JupyterInspectRequest);
        try
        {
            ValidateCursorPosition(request.Code, request.CursorPosition);
            if (request.DetailLevel is not (0 or 1))
                throw new ArgumentOutOfRangeException(nameof(request.DetailLevel),
                    "Jupyter inspect detail level must be 0 or 1.");

            if (application is not IJupyterInspectionProvider provider)
                throw new JupyterKernelExecutionException(
                    "NotSupported",
                    "The Jupyter kernel does not provide code inspection.");

            var result = await provider.InspectAsync(request, lifetime.Token).ConfigureAwait(false);
            await SendReplyAsync(
                channel,
                wireRequest,
                "inspect_reply",
                new JupyterInspectReply
                {
                    Status = "ok",
                    Found = result.Found,
                    Data = result.Data.Data,
                    Metadata = result.Metadata ?? new Dictionary<string, JsonElement>()
                },
                JupyterJsonContext.Default.JupyterInspectReply,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var error = ToJupyterError(exception);
            await SendReplyAsync(
                channel,
                wireRequest,
                "inspect_reply",
                new JupyterInspectReply
                {
                    Status = "error",
                    ErrorName = error.Name,
                    ErrorValue = error.Value,
                    Traceback = error.Traceback
                },
                JupyterJsonContext.Default.JupyterInspectReply,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task IsCompleteAsync(JupyterWireMessage wireRequest, JupyterKernelChannel channel)
    {
        var request = wireRequest.Message.GetContent(JupyterJsonContext.Default.JupyterIsCompleteRequest);
        var result = new JupyterCodeCompletenessResult(JupyterCodeCompletenessStatus.Unknown);
        if (application is IJupyterCodeCompletenessProvider provider)
            try
            {
                result = await provider.IsCompleteAsync(request, lifetime.Token).ConfigureAwait(false);
            }
            catch
            {
                result = new JupyterCodeCompletenessResult(JupyterCodeCompletenessStatus.Unknown);
            }

        await SendReplyAsync(
            channel,
            wireRequest,
            "is_complete_reply",
            new JupyterIsCompleteReply(ToWireStatus(result.Status), result.Indent),
            JupyterJsonContext.Default.JupyterIsCompleteReply,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleCommAsync(JupyterCommKind kind, JupyterWireMessage wireRequest)
    {
        if (application is not IJupyterCommSink sink)
            return;

        JupyterCommMessage message;
        try
        {
            message = CreateCommMessage(kind, wireRequest);
        }
        catch (JupyterProtocolException)
        {
            // Malformed comm content is dropped like any other unknown shell message; comm has no
            // reply contract and must not take down the kernel or the enclosing execution.
            return;
        }

        JupyterExecutionContext? context;
        lock (executionGate)
        {
            context = currentExecutionContext;
        }

        switch (kind)
        {
            case JupyterCommKind.Open:
                await sink.OnCommOpenAsync(message, context, lifetime.Token).ConfigureAwait(false);
                break;
            case JupyterCommKind.Message:
                await sink.OnCommMsgAsync(message, context, lifetime.Token).ConfigureAwait(false);
                break;
            case JupyterCommKind.Close:
                await sink.OnCommCloseAsync(message, context, lifetime.Token).ConfigureAwait(false);
                break;
        }
    }

    private static JupyterCommMessage CreateCommMessage(JupyterCommKind kind, JupyterWireMessage wireRequest)
    {
        var content = wireRequest.Message.Content;
        var commId = GetRequiredString(content, "comm_id", wireRequest.Message.MessageType);
        var targetName = kind == JupyterCommKind.Open
            ? GetRequiredString(content, "target_name", wireRequest.Message.MessageType)
            : null;
        var data = content.ValueKind == JsonValueKind.Object &&
                   content.TryGetProperty("data", out var dataProperty)
            ? (JsonElement?)dataProperty
            : null;
        var metadata = wireRequest.Message.Metadata;
        return new JupyterCommMessage(kind, commId, targetName, data, metadata, wireRequest.Buffers, wireRequest);
    }

    private static string GetRequiredString(JsonElement content, string property, string messageType)
    {
        if (content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new JupyterProtocolException(
                $"Jupyter '{messageType}' content requires a non-empty '{property}'.");

        return value.GetString()!;
    }

    private JupyterExecutionContext CreateExecutionContext(
        JupyterWireMessage wireRequest,
        JupyterExecuteRequest request,
        int count)
    {
        return new JupyterExecutionContext(
            wireRequest.Message.Header.MessageId,
            count,
            async (name, text, cancellationToken) =>
            {
                if (!request.Silent)
                    await PublishAsync(
                        "stream",
                        new JupyterStream(name, text),
                        JupyterJsonContext.Default.JupyterStream,
                        wireRequest.Message.Header,
                        cancellationToken).ConfigureAwait(false);
            },
            async (data, metadata, cancellationToken) =>
            {
                if (!request.Silent)
                    await PublishAsync(
                        "display_data",
                        new JupyterDisplayData(data.Data, metadata),
                        JupyterJsonContext.Default.JupyterDisplayData,
                        wireRequest.Message.Header,
                        cancellationToken).ConfigureAwait(false);
            },
            async (data, displayId, metadata, cancellationToken) =>
            {
                var resolvedDisplayId = displayId ?? JupyterDisplayId.Create();
                var transient = JupyterDisplayTransient.Create(resolvedDisplayId);
                if (!request.Silent)
                    await PublishAsync(
                        "display_data",
                        new JupyterDisplayData(
                            data.Data,
                            metadata,
                            transient),
                        JupyterJsonContext.Default.JupyterDisplayData,
                        wireRequest.Message.Header,
                        cancellationToken).ConfigureAwait(false);

                return resolvedDisplayId;
            },
            async (displayId, data, metadata, cancellationToken) =>
            {
                var transient = JupyterDisplayTransient.Create(displayId);
                if (!request.Silent)
                    await PublishAsync(
                        "update_display_data",
                        new JupyterUpdateDisplayData(
                            data.Data,
                            metadata,
                            transient),
                        JupyterJsonContext.Default.JupyterUpdateDisplayData,
                        wireRequest.Message.Header,
                        cancellationToken).ConfigureAwait(false);
            },
            async (wait, cancellationToken) =>
            {
                if (!request.Silent)
                    await PublishAsync(
                        "clear_output",
                        new JupyterClearOutputContent(wait),
                        JupyterJsonContext.Default.JupyterClearOutputContent,
                        wireRequest.Message.Header,
                        cancellationToken).ConfigureAwait(false);
            },
            async (data, metadata, cancellationToken) =>
            {
                if (!request.Silent)
                    await PublishAsync(
                        "execute_result",
                        new JupyterExecuteResultData(data.Data, metadata, count),
                        JupyterJsonContext.Default.JupyterExecuteResultData,
                        wireRequest.Message.Header,
                        cancellationToken).ConfigureAwait(false);
            },
            async (name, value, traceback, cancellationToken) =>
            {
                if (!request.Silent)
                    await PublishAsync(
                        "error",
                        new JupyterError(name, value, traceback),
                        JupyterJsonContext.Default.JupyterError,
                        wireRequest.Message.Header,
                        cancellationToken).ConfigureAwait(false);
            },
            (prompt, password, cancellationToken) =>
            {
                if (!request.AllowStdin)
                    return Task.FromException<string>(
                        new InvalidOperationException("The execute request did not allow stdin."));

                return SendInputRequestAsync(
                    wireRequest,
                    prompt,
                    password,
                    cancellationToken);
            });
    }

    private async Task<string> SendInputRequestAsync(
        JupyterWireMessage executeRequest,
        string prompt,
        bool password,
        CancellationToken cancellationToken)
    {
        var message = JupyterMessage.Create(
            "input_request",
            new JupyterInputRequestContent(prompt, password),
            JupyterJsonContext.Default.JupyterInputRequestContent,
            session,
            executeRequest.Message.Header);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingInputs.TryAdd(message.Header.MessageId, completion))
            throw new InvalidOperationException($"Input request '{message.Header.MessageId}' was already registered.");

        await using var registration = cancellationToken.Register(() =>
        {
            if (pendingInputs.TryRemove(message.Header.MessageId, out var pending))
                pending.TrySetCanceled(cancellationToken);
        });

        await transport.SendAsync(
            JupyterKernelChannel.Stdin,
            new JupyterWireMessage(executeRequest.Identities, message, []),
            cancellationToken).ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RouteInputReply(JupyterMessage message)
    {
        if (message.MessageType != "input_reply" || message.ParentHeader is null) return;

        if (pendingInputs.TryRemove(message.ParentHeader.MessageId, out var pending))
            pending.TrySetResult(message.GetContent(JupyterJsonContext.Default.JupyterInputReply).Value);
    }

    private async Task<bool> HandleShutdownAsync(JupyterWireMessage request, JupyterKernelChannel channel)
    {
        var shutdown = request.Message.GetContent(JupyterJsonContext.Default.JupyterShutdownRequest);
        RestartRequested = shutdown.Restart;
        await SendReplyAsync(
            channel,
            request,
            "shutdown_reply",
            new JupyterShutdownReply(shutdown.Restart),
            JupyterJsonContext.Default.JupyterShutdownReply,
            CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private void CancelCurrentExecution()
    {
        lock (executionGate)
        {
            currentExecution?.Cancel();
        }
    }

    private async ValueTask PublishStatusAsync(
        string state,
        JupyterMessageHeader? parent,
        CancellationToken cancellationToken)
    {
        await PublishAsync(
            "status",
            new JupyterStatus(state),
            JupyterJsonContext.Default.JupyterStatus,
            parent,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishAsync<TContent>(
        string messageType,
        TContent content,
        JsonTypeInfo<TContent> contentType,
        JupyterMessageHeader? parent,
        CancellationToken cancellationToken)
    {
        var message = JupyterMessage.Create(messageType, content, contentType, session, parent);
        await transport.SendAsync(
            JupyterKernelChannel.Iopub,
            new JupyterWireMessage([Encoding.UTF8.GetBytes(messageType)], message, []),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendReplyAsync<TContent>(
        JupyterKernelChannel channel,
        JupyterWireMessage request,
        string messageType,
        TContent content,
        JsonTypeInfo<TContent> contentType,
        CancellationToken cancellationToken)
    {
        var message = JupyterMessage.Create(messageType, content, contentType, session, request.Message.Header);
        await transport.SendAsync(
            channel,
            new JupyterWireMessage(request.Identities, message, []),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendIopubWelcomeAsync(byte[] topic, CancellationToken cancellationToken)
    {
        var message = JupyterMessage.Create(
            "iopub_welcome",
            new JupyterIopubWelcome(Convert.ToHexString(topic).ToLowerInvariant()),
            JupyterJsonContext.Default.JupyterIopubWelcome,
            session);
        await transport.SendAsync(
            JupyterKernelChannel.Iopub,
            new JupyterWireMessage([topic], message, []),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteHostAsync()
    {
        try
        {
            await Task.WhenAll(routerLoop, shellLoop, controlLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            CancelCurrentExecution();
            foreach (var pending in pendingInputs.Values)
                pending.TrySetException(new ObjectDisposedException(nameof(JupyterKernelHost)));

            pendingInputs.Clear();
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static JupyterError ToJupyterError(Exception exception)
    {
        if (exception is JupyterKernelExecutionException kernelException)
            return new JupyterError(kernelException.Name, kernelException.Message, kernelException.Traceback);

        return new JupyterError(exception.GetType().Name, exception.Message, exception.StackTrace?.Split('\n') ?? []);
    }

    private static void ValidateCursorPosition(string code, int cursorPosition)
    {
        ArgumentNullException.ThrowIfNull(code);
        JupyterCursorPosition.ToUtf16Index(code, cursorPosition);
    }

    private static void ValidateCursorRange(string code, int cursorStart, int cursorEnd)
    {
        if (cursorStart > cursorEnd)
            throw new ArgumentOutOfRangeException(nameof(cursorStart),
                "Jupyter completion cursor start must not exceed cursor end.");

        JupyterCursorPosition.ToUtf16Index(code, cursorStart);
        JupyterCursorPosition.ToUtf16Index(code, cursorEnd);
    }

    private static string ToWireStatus(JupyterCodeCompletenessStatus status)
    {
        return status switch
        {
            JupyterCodeCompletenessStatus.Complete => "complete",
            JupyterCodeCompletenessStatus.Incomplete => "incomplete",
            JupyterCodeCompletenessStatus.Invalid => "invalid",
            _ => "unknown"
        };
    }
}
