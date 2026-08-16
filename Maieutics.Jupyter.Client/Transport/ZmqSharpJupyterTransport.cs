using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using Maieutics.Jupyter.Shared;
using ZmqSharp;
using ZmqSharp.Transports;

namespace Maieutics.Jupyter.Client.Transport;

/// <summary>
/// Provides an asynchronous ZeroMQ transport for a Jupyter client.
/// </summary>
public sealed class ZmqSharpJupyterTransport : IJupyterTransport, IJupyterTransportConnectionReadiness
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly JupyterConnectionInfo connectionInfo;
    private readonly Channel<PingCommand> heartbeatCommands;
    private readonly Channel<JupyterTransportMessage> incomingMessages;
    private readonly CancellationTokenSource lifetime = new();
    private readonly JupyterTransportOptions options;
    private readonly Channel<ClientCommand> outgoingCommands;
    private readonly IJupyterMessageSerializer serializer;
    private readonly TaskCompletionSource stdinConnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposeState;
    private Task ownerTask = Task.CompletedTask;
    private Exception? terminalError;

    private ZmqSharpJupyterTransport(JupyterConnectionInfo connectionInfo, JupyterTransportOptions options)
    {
        this.connectionInfo = connectionInfo;
        this.options = options;
        serializer = new JupyterMessageSerializer(connectionInfo.Key, connectionInfo.SignatureScheme);
        incomingMessages = Channel.CreateBounded<JupyterTransportMessage>(
            new BoundedChannelOptions(options.IncomingCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        outgoingCommands = Channel.CreateBounded<ClientCommand>(new BoundedChannelOptions(options.OutgoingCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        heartbeatCommands = Channel.CreateBounded<PingCommand>(new BoundedChannelOptions(options.OutgoingCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <inheritdoc />
    public IAsyncEnumerable<JupyterTransportMessage> IncomingMessages => incomingMessages.Reader.ReadAllAsync();

    /// <inheritdoc />
    public async ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default)
    {
        if (channel == JupyterTransportChannel.Iopub)
            throw new InvalidOperationException("A Jupyter client cannot send messages on IOPub.");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueAsync(new SendCommand(channel, message, completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueAsync(new PingCommand(completion), cancellationToken).ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    Task IJupyterTransportConnectionReadiness.WaitForStdinConnectedAsync(CancellationToken cancellationToken)
    {
        return stdinConnected.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var ownsDisposal = Interlocked.Exchange(ref disposeState, 1) == 0;
        if (ownsDisposal) await lifetime.CancelAsync().ConfigureAwait(false);

        await ownerTask.ConfigureAwait(false);
        if (ownsDisposal) lifetime.Dispose();
    }

    /// <summary>
    /// Starts a client transport for the supplied Jupyter connection.
    /// </summary>
    public static Task<ZmqSharpJupyterTransport> ConnectAsync(
        JupyterConnectionInfo connectionInfo,
        JupyterTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionInfo);
        cancellationToken.ThrowIfCancellationRequested();
        connectionInfo.ValidateSupported();
        var transport = new ZmqSharpJupyterTransport(connectionInfo, options ?? new JupyterTransportOptions());
        transport.Start();
        return Task.FromResult(transport);
    }

    private void Start()
    {
        ownerTask = RunOwnerAsync();
    }

    private async Task RunOwnerAsync()
    {
        var sockets = new ClientSocketOwner(this);
        try
        {
            await sockets.ConnectAsync(lifetime.Token).ConfigureAwait(false);
            await Task.WhenAll(
                    ObservePumpAsync(() => ProcessCommandsAsync(sockets, lifetime.Token)),
                    ObservePumpAsync(() => ProcessHeartbeatsAsync(sockets.Heartbeat, lifetime.Token)))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminate(exception);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);

            try
            {
                await sockets.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref terminalError, exception, null);
            }

            var completionError = Volatile.Read(ref terminalError);
            var pendingError = completionError ?? new ObjectDisposedException(nameof(ZmqSharpJupyterTransport));
            stdinConnected.TrySetException(pendingError);
            outgoingCommands.Writer.TryComplete(completionError);
            heartbeatCommands.Writer.TryComplete(completionError);
            while (outgoingCommands.Reader.TryRead(out var command)) command.Fail(pendingError);
            while (heartbeatCommands.Reader.TryRead(out var ping)) ping.Fail(pendingError);

            incomingMessages.Writer.TryComplete(completionError);
        }
    }

    private async Task ObservePumpAsync(Func<Task> pump)
    {
        try
        {
            await pump().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminate(exception);
            throw;
        }
    }

    private async Task ProcessCommandsAsync(ClientSocketOwner sockets, CancellationToken cancellationToken)
    {
        await foreach (var command in outgoingCommands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            switch (command)
            {
                case SendCommand send:
                    try
                    {
                        await SendAsync(sockets, send, cancellationToken).ConfigureAwait(false);
                        send.Completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        send.Fail(exception);
                        throw;
                    }

                    break;
                case PingCommand ping:
                    if (heartbeatCommands.Writer.TryWrite(ping)) break;

                    var backpressure = CreateOutgoingBackpressureException();
                    ping.Fail(backpressure);
                    throw backpressure;
            }
    }

    private async Task ProcessHeartbeatsAsync(ZReqSocket heartbeat, CancellationToken cancellationToken)
    {
        await foreach (var command in heartbeatCommands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = Guid.NewGuid().ToByteArray();
            var startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                using var response = await heartbeat.RequestAsync(
                        ZMessage.FromOwned(payload), cancellationToken)
                    .ConfigureAwait(false);
                if (response.Count != 1 || !response[0].ToSequence().ToArray().AsSpan().SequenceEqual(payload))
                    throw new JupyterProtocolException("Heartbeat reply did not match its request.");

                command.Completion.TrySetResult(Stopwatch.GetElapsedTime(startTimestamp));
            }
            catch (Exception exception)
            {
                command.Fail(exception);
                throw;
            }
        }
    }

    private async ValueTask SendAsync(
        ClientSocketOwner sockets,
        SendCommand command,
        CancellationToken cancellationToken)
    {
        var frames = serializer.Serialize(JupyterWireMessage.Create(command.Message)).ToArray();
        var message = ZMessage.FromOwned(frames);
        switch (command.Channel)
        {
            case JupyterTransportChannel.Shell:
                await sockets.Shell.SendAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case JupyterTransportChannel.Control:
                await sockets.Control.SendAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case JupyterTransportChannel.Stdin:
                await sockets.Stdin.SendAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            default:
                message.Dispose();
                throw new ArgumentOutOfRangeException(nameof(command), command.Channel, null);
        }
    }

    private ValueTask EnqueueAsync(ClientCommand command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfTerminated();

        if (outgoingCommands.Writer.TryWrite(command)) return ValueTask.CompletedTask;

        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        ThrowIfTerminated();
        var exception = CreateOutgoingBackpressureException();
        command.Fail(exception);
        Terminate(exception);
        throw exception;
    }

    private JupyterBackpressureException CreateOutgoingBackpressureException()
    {
        return new JupyterBackpressureException(
            $"The Jupyter client outgoing queue exceeded its capacity of {options.OutgoingCapacity}.");
    }

    private void Receive(JupyterTransportChannel channel, ZMessage message)
    {
        try
        {
            var frames = message.Select(static frame => frame.ToSequence().ToArray()).ToArray();
            var wireMessage = serializer.Deserialize(frames);
            if (incomingMessages.Writer.TryWrite(new JupyterTransportMessage(channel, wireMessage))) return;

            Terminate(new JupyterBackpressureException(
                $"The Jupyter client incoming queue exceeded its capacity of {options.IncomingCapacity}."));
        }
        catch (Exception exception)
        {
            Terminate(exception);
            throw;
        }
        finally
        {
            message.Dispose();
        }
    }

    private void OnPeerEnded(string channel, IZConnection peer, Exception? failure)
    {
        if (lifetime.IsCancellationRequested) return;

        // A peer closing the connection cleanly (failure == null) is a normal
        // shutdown signal, not an error: the kernel closes its sockets after
        // the shutdown exchange, and EOF arrival order across the five TCP
        // connections is not guaranteed - terminating the session on any
        // clean EOF would race the in-flight shutdown_reply (and fail pending
        // requests). Only an abnormal peer end (protocol/IO failure) is
        // terminal for the transport.
        if (failure is null) return;

        Terminate(failure);
    }

    private void Terminate(Exception exception)
    {
        if (Interlocked.CompareExchange(ref terminalError, exception, null) is not null) return;

        incomingMessages.Writer.TryComplete(exception);
        lifetime.Cancel();
    }

    private void ThrowIfTerminated()
    {
        if (Volatile.Read(ref terminalError) is { } exception)
            throw new JupyterProtocolException("The Jupyter client transport has terminated.", exception);
    }

    private static async Task ConnectWithRetryAsync(
        IZSocket socket,
        string endpoint,
        CancellationToken cancellationToken)
    {
        while (true)
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SocketException exception) when (
                exception.SocketErrorCode == SocketError.ConnectionRefused)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(ConnectRetryDelay, cancellationToken).ConfigureAwait(false);
            }
    }

    private abstract record ClientCommand
    {
        public abstract void Fail(Exception exception);
    }

    private sealed record SendCommand(
        JupyterTransportChannel Channel,
        JupyterMessage Message,
        TaskCompletionSource Completion) : ClientCommand
    {
        public override void Fail(Exception exception)
        {
            Completion.TrySetException(exception);
        }
    }

    private sealed record PingCommand(TaskCompletionSource<TimeSpan> Completion) : ClientCommand
    {
        public override void Fail(Exception exception)
        {
            Completion.TrySetException(exception);
        }
    }

    private sealed class IncomingSink(ZmqSharpJupyterTransport owner, JupyterTransportChannel channel) : IPatternSink
    {
        public ValueTask OnMessageAsync(
            IZConnection peer,
            ZMessage message,
            CancellationToken token = default)
        {
            owner.Receive(channel, message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ClientSocketOwner : IAsyncDisposable
    {
        private readonly ZmqSharpJupyterTransport owner;

        public ClientSocketOwner(ZmqSharpJupyterTransport owner)
        {
            this.owner = owner;
            var clientIdentity = Guid.NewGuid().ToByteArray();
            Shell = CreateDealer(clientIdentity, new IncomingSink(owner, JupyterTransportChannel.Shell));
            Control = CreateDealer(Guid.NewGuid().ToByteArray(),
                new IncomingSink(owner, JupyterTransportChannel.Control));
            Stdin = CreateDealer(clientIdentity, new IncomingSink(owner, JupyterTransportChannel.Stdin));
            Iopub = new ZSubSocket(new ZSocketOptions
            {
                MessageSink = new IncomingSink(owner, JupyterTransportChannel.Iopub)
            });
            Heartbeat = new ZReqSocket();
            Iopub.Subscribe([]);

            Shell.PeerEnded += OnShellEnded;
            Control.PeerEnded += OnControlEnded;
            Stdin.PeerEnded += OnStdinEnded;
            Iopub.PeerEnded += OnIopubEnded;
            Heartbeat.PeerEnded += OnHeartbeatEnded;
        }

        public ZDealerSocket Shell { get; }

        public ZDealerSocket Control { get; }

        public ZDealerSocket Stdin { get; }

        public ZSubSocket Iopub { get; }

        public ZReqSocket Heartbeat { get; }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            await Task.WhenAll(
                    ObserveConnectAsync(Shell, owner.connectionInfo.Endpoint(JupyterChannel.Shell), cancellationToken),
                    ObserveConnectAsync(Control, owner.connectionInfo.Endpoint(JupyterChannel.Control),
                        cancellationToken),
                    ConnectStdinAsync(cancellationToken),
                    ObserveConnectAsync(Iopub, owner.connectionInfo.Endpoint(JupyterChannel.Iopub), cancellationToken),
                    ObserveConnectAsync(Heartbeat, owner.connectionInfo.Endpoint(JupyterChannel.Heartbeat),
                        cancellationToken))
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            Shell.PeerEnded -= OnShellEnded;
            Control.PeerEnded -= OnControlEnded;
            Stdin.PeerEnded -= OnStdinEnded;
            Iopub.PeerEnded -= OnIopubEnded;
            Heartbeat.PeerEnded -= OnHeartbeatEnded;

            Exception? failure = null;
            failure = await DisposeSocketAsync(Heartbeat, failure).ConfigureAwait(false);
            failure = await DisposeSocketAsync(Iopub, failure).ConfigureAwait(false);
            failure = await DisposeSocketAsync(Stdin, failure).ConfigureAwait(false);
            failure = await DisposeSocketAsync(Control, failure).ConfigureAwait(false);
            failure = await DisposeSocketAsync(Shell, failure).ConfigureAwait(false);
            if (failure is not null) throw failure;
        }

        private async Task ConnectStdinAsync(CancellationToken cancellationToken)
        {
            await ObserveConnectAsync(
                    Stdin,
                    owner.connectionInfo.Endpoint(JupyterChannel.Stdin),
                    cancellationToken)
                .ConfigureAwait(false);
            owner.stdinConnected.TrySetResult();
        }

        private async Task ObserveConnectAsync(
            IZSocket socket,
            string endpoint,
            CancellationToken cancellationToken)
        {
            try
            {
                await ConnectWithRetryAsync(socket, endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                owner.Terminate(exception);
                throw;
            }
        }

        private static async ValueTask<Exception?> DisposeSocketAsync(
            IAsyncDisposable socket,
            Exception? failure)
        {
            try
            {
                await socket.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            return failure;
        }

        private static ZDealerSocket CreateDealer(byte[] identity, IPatternSink sink)
        {
            return new ZDealerSocket(new ZSocketOptions
            {
                Identity = identity,
                MessageSink = sink
            });
        }

        private void OnShellEnded(IZConnection peer, Exception? failure)
        {
            owner.OnPeerEnded("shell", peer, failure);
        }

        private void OnControlEnded(IZConnection peer, Exception? failure)
        {
            owner.OnPeerEnded("control", peer, failure);
        }

        private void OnStdinEnded(IZConnection peer, Exception? failure)
        {
            owner.OnPeerEnded("stdin", peer, failure);
        }

        private void OnIopubEnded(IZConnection peer, Exception? failure)
        {
            owner.OnPeerEnded("IOPub", peer, failure);
        }

        private void OnHeartbeatEnded(IZConnection peer, Exception? failure)
        {
            owner.OnPeerEnded("heartbeat", peer, failure);
        }
    }
}
