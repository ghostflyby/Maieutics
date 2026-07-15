using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Maieutics.Jupyter.Kernel.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Kernel;

public sealed class JupyterKernelHost : IJupyterKernel
{
    private readonly IJupyterKernelApplication application;
    private readonly IJupyterKernelTransport transport;
    private readonly JupyterSessionIdentity session;
    private readonly Channel<JupyterWireMessage> shellRequests = Channel.CreateBounded<JupyterWireMessage>(128);
    private readonly Channel<JupyterWireMessage> controlRequests = Channel.CreateBounded<JupyterWireMessage>(32);
    private readonly ConcurrentDictionary<JupyterMessageId, TaskCompletionSource<string>> pendingInputs = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock executionGate = new();
    private readonly Task routerLoop;
    private readonly Task shellLoop;
    private readonly Task controlLoop;
    private CancellationTokenSource? currentExecution;
    private int executionCount;
    private int disposeState;

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

    public Task Completion { get; }

    public bool RestartRequested { get; private set; }

    public static async Task<JupyterKernelHost> StartAsync(
        JupyterConnectionInfo connectionInfo,
        IJupyterKernelApplication application,
        JupyterSessionIdentity? session = null,
        CancellationToken cancellationToken = default)
    {
        var transport = await NetMqJupyterKernelTransport.BindAsync(
            connectionInfo,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new JupyterKernelHost(application, transport, session ?? JupyterSessionIdentity.Create("kernel"));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    private async Task RouteIncomingAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var incoming in transport.IncomingEvents.WithCancellation(lifetime.Token)
                               .ConfigureAwait(false))
            {
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
                        await SendIopubWelcomeAsync(subscription.Topic, lifetime.Token).ConfigureAwait(false);
                        break;
                }
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
        {
            await ProcessWithStatusAsync(request, JupyterKernelChannel.Shell, HandleShellRequestAsync)
                .ConfigureAwait(false);
        }
    }

    private async Task ProcessControlAsync()
    {
        await foreach (var request in controlRequests.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
        {
            await ProcessWithStatusAsync(request, JupyterKernelChannel.Control, HandleControlRequestAsync)
                .ConfigureAwait(false);
        }
    }

    private async Task ProcessWithStatusAsync(
        JupyterWireMessage request,
        JupyterKernelChannel channel,
        Func<JupyterWireMessage, JupyterKernelChannel, Task<bool>> handler)
    {
        await PublishStatusAsync("busy", request.Message.Header, lifetime.Token).ConfigureAwait(false);
        var stop = false;
        try
        {
            stop = await handler(request, channel).ConfigureAwait(false);
        }
        finally
        {
            await PublishStatusAsync("idle", request.Message.Header, CancellationToken.None).ConfigureAwait(false);
        }

        if (stop)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
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
            case "shutdown_request":
                return await HandleShutdownAsync(request, channel).ConfigureAwait(false);
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
        {
            await PublishAsync(
                "execute_input",
                new JupyterExecuteInput(request.Code, count),
                JupyterJsonContext.Default.JupyterExecuteInput,
                wireRequest.Message.Header,
                lifetime.Token).ConfigureAwait(false);
        }

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        lock (executionGate)
        {
            currentExecution = execution;
        }

        try
        {
            var context = CreateExecutionContext(wireRequest, request, count);
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
            {
                await PublishAsync(
                    "error",
                    error,
                    JupyterJsonContext.Default.JupyterError,
                    wireRequest.Message.Header,
                    CancellationToken.None).ConfigureAwait(false);
            }

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
                if (ReferenceEquals(currentExecution, execution))
                {
                    currentExecution = null;
                }
            }
        }
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
                {
                    await PublishAsync(
                        "stream",
                        new JupyterStream(name, text),
                        JupyterJsonContext.Default.JupyterStream,
                        wireRequest.Message.Header,
                        cancellationToken).ConfigureAwait(false);
                }
            },
            async (data, metadata, cancellationToken) =>
            {
                if (!request.Silent)
                {
                    await PublishAsync(
                        "display_data",
                        new JupyterDisplayData(data.Data, metadata),
                        JupyterJsonContext.Default.JupyterDisplayData,
                        wireRequest.Message.Header,
                        cancellationToken).ConfigureAwait(false);
                }
            },
            async (data, metadata, cancellationToken) =>
            {
                if (!request.Silent)
                {
                    await PublishAsync(
                        "execute_result",
                        new JupyterExecuteResultData(data.Data, metadata, count),
                        JupyterJsonContext.Default.JupyterExecuteResultData,
                        wireRequest.Message.Header,
                        cancellationToken).ConfigureAwait(false);
                }
            },
            (prompt, password, cancellationToken) => RequestInputAsync(
                wireRequest,
                request.AllowStdin,
                prompt,
                password,
                cancellationToken));
    }

    private async Task<string> RequestInputAsync(
        JupyterWireMessage executeRequest,
        bool allowStdin,
        string prompt,
        bool password,
        CancellationToken cancellationToken)
    {
        if (!allowStdin)
        {
            throw new InvalidOperationException("The execute request did not allow stdin.");
        }

        var message = JupyterMessage.Create(
            "input_request",
            new JupyterInputRequestContent(prompt, password),
            JupyterJsonContext.Default.JupyterInputRequestContent,
            session,
            executeRequest.Message.Header);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingInputs.TryAdd(message.Header.MessageId, completion))
        {
            throw new InvalidOperationException($"Input request '{message.Header.MessageId}' was already registered.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            if (pendingInputs.TryRemove(message.Header.MessageId, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        await transport.SendAsync(
            JupyterKernelChannel.Stdin,
            new JupyterWireMessage(executeRequest.Identities, message, []),
            cancellationToken).ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RouteInputReply(JupyterMessage message)
    {
        if (message.MessageType != "input_reply" || message.ParentHeader is null)
        {
            return;
        }

        if (pendingInputs.TryRemove(message.ParentHeader.MessageId, out var pending))
        {
            pending.TrySetResult(message.GetContent(JupyterJsonContext.Default.JupyterInputReply).Value);
        }
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
        JupyterMessageHeader parent,
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
        JupyterMessageHeader parent,
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
            {
                pending.TrySetException(new ObjectDisposedException(nameof(JupyterKernelHost)));
            }

            pendingInputs.Clear();
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static JupyterError ToJupyterError(Exception exception)
    {
        if (exception is JupyterKernelExecutionException kernelException)
        {
            return new JupyterError(kernelException.Name, kernelException.Message, kernelException.Traceback);
        }

        return new JupyterError(exception.GetType().Name, exception.Message, exception.StackTrace?.Split('\n') ?? []);
    }
}