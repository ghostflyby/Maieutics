using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using Maieutics.Jupyter.Shared;
using ZmqSharp;
using ZmqSharp.Transports;

namespace Maieutics.Jupyter.Kernel.Transport;

internal sealed class ZmqSharpJupyterKernelTransport : IJupyterKernelTransport
{
    private readonly ZRouterSocket control;
    private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ZRepSocket heartbeat;
    private readonly Channel<JupyterKernelTransportEvent> incomingEvents;
    private readonly ZXPubSocket iopub;
    private readonly CancellationTokenSource lifetime = new();
    private readonly JupyterKernelTransportOptions options;
    private readonly Channel<SendCommand> outgoingCommands;
    private readonly IJupyterMessageSerializer serializer;
    private readonly ZRouterSocket shell;
    private readonly ZRouterSocket stdin;
    private int disposeState;
    private Task outgoingPump = Task.CompletedTask;
    private Exception? terminalError;

    private ZmqSharpJupyterKernelTransport(
        JupyterConnectionInfo connectionInfo,
        JupyterKernelTransportOptions options)
    {
        this.options = options;
        serializer = new JupyterMessageSerializer(connectionInfo.Key, connectionInfo.SignatureScheme);
        incomingEvents = Channel.CreateBounded<JupyterKernelTransportEvent>(
            new BoundedChannelOptions(options.IncomingCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        outgoingCommands = Channel.CreateBounded<SendCommand>(new BoundedChannelOptions(options.OutgoingCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        shell = CreateRouter(JupyterKernelChannel.Shell);
        control = CreateRouter(JupyterKernelChannel.Control);
        stdin = CreateRouter(JupyterKernelChannel.Stdin);
        iopub = new ZXPubSocket(new ZSocketOptions
        {
            MessageSink = new SubscriptionSink(this)
        });
        heartbeat = new ZRepSocket();
        heartbeat.BindRequestHandler(EchoHeartbeatAsync);

        // Abnormal peer ends must be observable: without these subscriptions ZmqSharp removes
        // and disposes the peer connection silently, leaving the kernel heartbeating on a
        // connection that can no longer carry protocol traffic.
        shell.PeerEnded += OnShellPeerEnded;
        control.PeerEnded += OnControlPeerEnded;
        stdin.PeerEnded += OnStdinPeerEnded;
        iopub.PeerEnded += OnIopubPeerEnded;
        heartbeat.PeerEnded += OnHeartbeatPeerEnded;
    }

    public IAsyncEnumerable<JupyterKernelTransportEvent> IncomingEvents => incomingEvents.Reader.ReadAllAsync();

    public async ValueTask SendAsync(
        JupyterKernelChannel channel,
        JupyterWireMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        ThrowIfTerminated();

        var command = new SendCommand(
            channel,
            message,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!outgoingCommands.Writer.TryWrite(command))
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
            ThrowIfTerminated();

            var exception = new JupyterKernelBackpressureException(
                $"The Jupyter kernel outgoing queue exceeded its capacity of {options.OutgoingCapacity}.");
            await TerminateAsync(exception).ConfigureAwait(false);
            throw exception;
        }

        await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) == 0)
        {
            try
            {
                outgoingCommands.Writer.TryComplete(Volatile.Read(ref terminalError));
                incomingEvents.Writer.TryComplete(Volatile.Read(ref terminalError));
                await lifetime.CancelAsync().ConfigureAwait(false);
                await outgoingPump.ConfigureAwait(false);
                await DisposeSocketsAsync().ConfigureAwait(false);
                disposed.TrySetResult();
            }
            catch (Exception exception)
            {
                disposed.TrySetException(exception);
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        await disposed.Task.ConfigureAwait(false);
    }

    public static async Task<ZmqSharpJupyterKernelTransport> BindAsync(
        JupyterConnectionInfo connectionInfo,
        JupyterKernelTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connectionInfo.ValidateSupported();
        var transport = new ZmqSharpJupyterKernelTransport(
            connectionInfo,
            options ?? new JupyterKernelTransportOptions());
        try
        {
            await transport.shell.BindAsync(
                connectionInfo.Endpoint(JupyterChannel.Shell),
                cancellationToken).ConfigureAwait(false);
            await transport.control.BindAsync(
                connectionInfo.Endpoint(JupyterChannel.Control),
                cancellationToken).ConfigureAwait(false);
            await transport.stdin.BindAsync(
                connectionInfo.Endpoint(JupyterChannel.Stdin),
                cancellationToken).ConfigureAwait(false);
            await transport.iopub.BindAsync(
                connectionInfo.Endpoint(JupyterChannel.Iopub),
                cancellationToken).ConfigureAwait(false);
            await transport.heartbeat.BindAsync(
                connectionInfo.Endpoint(JupyterChannel.Heartbeat),
                cancellationToken).ConfigureAwait(false);
            transport.outgoingPump = transport.ProcessOutgoingAsync();
            return transport;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private ZRouterSocket CreateRouter(JupyterKernelChannel channel)
    {
        return new ZRouterSocket(new ZSocketOptions
        {
            MessageSink = new MessageSink(this, channel)
        });
    }

    private async Task ProcessOutgoingAsync()
    {
        try
        {
            await foreach (var command in outgoingCommands.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                try
                {
                    await SendCoreAsync(command.Channel, command.Message, lifetime.Token).ConfigureAwait(false);
                    command.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    command.Completion.TrySetException(
                        new ObjectDisposedException(nameof(ZmqSharpJupyterKernelTransport)));
                    return;
                }
                catch (Exception exception)
                {
                    command.Completion.TrySetException(exception);
                    await TerminateAsync(exception).ConfigureAwait(false);
                    return;
                }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await TerminateAsync(exception).ConfigureAwait(false);
        }
        finally
        {
            var pendingError = Volatile.Read(ref terminalError)
                               ?? new ObjectDisposedException(nameof(ZmqSharpJupyterKernelTransport));
            while (outgoingCommands.Reader.TryRead(out var command)) command.Completion.TrySetException(pendingError);
        }
    }

    private async ValueTask SendCoreAsync(
        JupyterKernelChannel channel,
        JupyterWireMessage message,
        CancellationToken cancellationToken)
    {
        var frames = serializer.Serialize(message);
        switch (channel)
        {
            case JupyterKernelChannel.Shell:
                await SendRouterAsync(shell, frames, cancellationToken).ConfigureAwait(false);
                break;
            case JupyterKernelChannel.Control:
                await SendRouterAsync(control, frames, cancellationToken).ConfigureAwait(false);
                break;
            case JupyterKernelChannel.Iopub:
                await iopub.SendAsync(ZMessage.FromOwned(frames.ToArray()), cancellationToken).ConfigureAwait(false);
                break;
            case JupyterKernelChannel.Stdin:
                await SendRouterAsync(stdin, frames, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channel), channel, null);
        }
    }

    private static async ValueTask SendRouterAsync(
        ZRouterSocket socket,
        IReadOnlyList<byte[]> frames,
        CancellationToken cancellationToken)
    {
        if (frames.Count < 2)
            throw new JupyterProtocolException("A routed Jupyter message requires a routing identity and payload.");

        var payload = new byte[frames.Count - 1][];
        for (var index = 1; index < frames.Count; index++) payload[index - 1] = frames[index];

        await socket.SendAsync(
            frames[0],
            ZMessage.FromOwned(payload),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EchoHeartbeatAsync(ZRequestContext context, CancellationToken cancellationToken)
    {
        var frames = new byte[context.Count][];
        for (var index = 0; index < context.Count; index++)
            frames[index] = context[index].ToSequence().ToArray();

        await heartbeat.SendReplyAsync(
            context,
            ZMessage.FromOwned(frames),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnqueueAsync(JupyterKernelTransportEvent transportEvent)
    {
        if (incomingEvents.Writer.TryWrite(transportEvent)) return;
        if (lifetime.IsCancellationRequested) return;

        var exception = new JupyterKernelBackpressureException(
            $"The Jupyter kernel incoming queue exceeded its capacity of {options.IncomingCapacity}.");
        await TerminateAsync(exception).ConfigureAwait(false);
        throw exception;
    }

    private async ValueTask TerminateAsync(Exception exception)
    {
        if (Interlocked.CompareExchange(ref terminalError, exception, null) is not null) return;

        outgoingCommands.Writer.TryComplete(exception);
        incomingEvents.Writer.TryComplete(exception);
        await lifetime.CancelAsync().ConfigureAwait(false);
    }

    private void ThrowIfTerminated()
    {
        if (Volatile.Read(ref terminalError) is { } exception)
            throw new JupyterProtocolException("The Jupyter kernel transport has terminated.", exception);
    }

    private void OnPeerEnded(string channel, IZConnection peer, Exception? failure)
    {
        if (failure is null || lifetime.IsCancellationRequested) return;

        // Unlike the client transport (one dedicated peer per socket, where an abnormal end
        // is terminal), the server-side ROUTER/XPUB/REP sockets serve many peers: one peer
        // ending abnormally - typically a client closing while kernel replies are still in
        // flight, which surfaces as a connection reset - is routine and must not terminate
        // the transport for the remaining peers. The teardown is made observable instead of
        // silently removing the peer; events may be dropped when the queue is saturated.
        incomingEvents.Writer.TryWrite(new JupyterKernelPeerEnded(channel, failure));
    }

    private void OnShellPeerEnded(IZConnection peer, Exception? failure)
    {
        OnPeerEnded("shell", peer, failure);
    }

    private void OnControlPeerEnded(IZConnection peer, Exception? failure)
    {
        OnPeerEnded("control", peer, failure);
    }

    private void OnStdinPeerEnded(IZConnection peer, Exception? failure)
    {
        OnPeerEnded("stdin", peer, failure);
    }

    private void OnIopubPeerEnded(IZConnection peer, Exception? failure)
    {
        OnPeerEnded("IOPub", peer, failure);
    }

    private void OnHeartbeatPeerEnded(IZConnection peer, Exception? failure)
    {
        OnPeerEnded("heartbeat", peer, failure);
    }

    private async Task DisposeSocketsAsync()
    {
        shell.PeerEnded -= OnShellPeerEnded;
        control.PeerEnded -= OnControlPeerEnded;
        stdin.PeerEnded -= OnStdinPeerEnded;
        iopub.PeerEnded -= OnIopubPeerEnded;
        heartbeat.PeerEnded -= OnHeartbeatPeerEnded;

        List<Exception>? failures = null;
        // Shutdown travels over control. Closing that ROUTER first preserves the
        // reply-before-EOF order on one connection before unrelated peers end.
        IAsyncDisposable[] sockets = [control, shell, stdin, iopub, heartbeat];
        foreach (var socket in sockets)
            try
            {
                await socket.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

        if (failures is not null) throw new AggregateException("Failed to dispose ZmqSharp kernel sockets.", failures);
    }

    private sealed record SendCommand(
        JupyterKernelChannel Channel,
        JupyterWireMessage Message,
        TaskCompletionSource Completion);

    private sealed class MessageSink(
        ZmqSharpJupyterKernelTransport owner,
        JupyterKernelChannel channel) : IPatternSink
    {
        public async ValueTask OnMessageAsync(
            IZConnection peer,
            ZMessage message,
            CancellationToken token = default)
        {
            try
            {
                var frames = new byte[message.Count][];
                for (var index = 0; index < message.Count; index++)
                    frames[index] = message[index].ToSequence().ToArray();

                JupyterWireMessage wireMessage;
                try
                {
                    wireMessage = owner.serializer.Deserialize(frames);
                }
                catch (JupyterSignatureException exception)
                {
                    // Frames that fail HMAC verification cannot be trusted as protocol
                    // traffic: terminate instead of interpreting them further.
                    await owner.TerminateAsync(exception).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (exception is JsonException or JupyterProtocolException)
                {
                    // One malformed payload must not remove the peer or fault ZmqSharp's
                    // receive pump for the channel; the message is dropped and the
                    // connection stays usable for well-formed traffic.
                    return;
                }

                await owner.EnqueueAsync(new JupyterKernelMessageReceived(channel, wireMessage)).ConfigureAwait(false);
            }
            finally
            {
                message.Dispose();
            }
        }
    }

    private sealed class SubscriptionSink(ZmqSharpJupyterKernelTransport owner) : IPatternSink
    {
        public async ValueTask OnMessageAsync(
            IZConnection peer,
            ZMessage message,
            CancellationToken token = default)
        {
            try
            {
                for (var index = 0; index < message.Count; index++)
                {
                    var subscription = message[index].ToSequence().ToArray();
                    if (subscription.Length == 0 || subscription[0] == 0) continue;

                    await owner.EnqueueAsync(
                        new JupyterIopubSubscriptionReceived(subscription[1..])).ConfigureAwait(false);
                }
            }
            finally
            {
                message.Dispose();
            }
        }
    }
}

internal sealed class JupyterKernelBackpressureException(string message) : Exception(message);
