using System.Diagnostics;
using System.Threading.Channels;
using Maieutics.Jupyter.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace Maieutics.Jupyter.Client.Transport;

public sealed class NetMqJupyterTransport : IJupyterTransport
{
    private readonly JupyterConnectionInfo connectionInfo;
    private readonly Channel<JupyterTransportMessage> incomingMessages;
    private readonly Thread ioThread;
    private readonly JupyterTransportOptions options;
    private readonly Channel<ClientCommand> outgoingCommands;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposeState;
    private ClientIoLoop? ioLoop;
    private Exception? terminalError;

    private NetMqJupyterTransport(JupyterConnectionInfo connectionInfo, JupyterTransportOptions options)
    {
        this.connectionInfo = connectionInfo;
        this.options = options;
        incomingMessages = Channel.CreateBounded<JupyterTransportMessage>(
            new BoundedChannelOptions(options.IncomingCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        outgoingCommands = Channel.CreateBounded<ClientCommand>(new BoundedChannelOptions(options.OutgoingCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        ioThread = new Thread(RunIoThread)
        {
            IsBackground = true,
            Name = "Maieutics.Jupyter.Client.NetMQ"
        };
    }

    public IAsyncEnumerable<JupyterTransportMessage> IncomingMessages => incomingMessages.Reader.ReadAllAsync();

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

    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueAsync(new PingCommand(completion), cancellationToken).ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            await stopped.Task.ConfigureAwait(false);
            return;
        }

        Volatile.Read(ref ioLoop)?.RequestStop();
        await stopped.Task.ConfigureAwait(false);
    }

    public static async Task<NetMqJupyterTransport> ConnectAsync(
        JupyterConnectionInfo connectionInfo,
        JupyterTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connectionInfo.ValidateSupported();
        var transport = new NetMqJupyterTransport(connectionInfo, options ?? new JupyterTransportOptions());
        transport.ioThread.Start();
        try
        {
            await transport.ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return transport;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask EnqueueAsync(ClientCommand command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfTerminated();

        if (!outgoingCommands.Writer.TryWrite(command))
        {
            var exception = new JupyterBackpressureException(
                $"The Jupyter client outgoing queue exceeded its capacity of {options.OutgoingCapacity}.");
            command.Fail(exception);
            Terminate(exception);
            throw exception;
        }

        try
        {
            var loop = Volatile.Read(ref ioLoop)
                       ?? throw new ObjectDisposedException(nameof(NetMqJupyterTransport));
            if (!loop.TrySignalCommandsAvailable()) throw new ObjectDisposedException(nameof(NetMqJupyterTransport));
        }
        catch (Exception exception)
        {
            command.Fail(exception);
            Terminate(exception);
            throw;
        }
    }

    private void RunIoThread()
    {
        ClientIoLoop? loop = null;
        Exception? failure = null;

        try
        {
            loop = ClientIoLoop.Create(this);
            Volatile.Write(ref ioLoop, loop);
            if (Volatile.Read(ref disposeState) != 0 || Volatile.Read(ref terminalError) is not null)
                loop.RequestStop();

            ready.TrySetResult();
            loop.Run();
        }
        catch (Exception exception)
        {
            failure = exception;
            ready.TrySetException(exception);
            Interlocked.CompareExchange(ref terminalError, exception, null);
        }
        finally
        {
            if (loop is not null) Interlocked.CompareExchange(ref ioLoop, null, loop);

            var completionError = terminalError ?? failure;
            var pendingError = completionError ?? new ObjectDisposedException(nameof(NetMqJupyterTransport));
            while (outgoingCommands.Reader.TryRead(out var command)) command.Fail(pendingError);

            loop?.FailPending(pendingError);
            loop?.Dispose();

            incomingMessages.Writer.TryComplete(completionError);
            stopped.TrySetResult();
        }
    }

    private static void Send(
        JupyterTransportChannel channel,
        JupyterMessage message,
        IJupyterMessageSerializer serializer,
        DealerSocket shell,
        DealerSocket control,
        DealerSocket stdin)
    {
        var socket = channel switch
        {
            JupyterTransportChannel.Shell => shell,
            JupyterTransportChannel.Control => control,
            JupyterTransportChannel.Stdin => stdin,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
        };
        var netMqMessage = new NetMQMessage();
        foreach (var frame in serializer.Serialize(JupyterWireMessage.Create(message))) netMqMessage.Append(frame);

        socket.SendMultipartMessage(netMqMessage);
    }

    private static DealerSocket CreateDealerSocket(string endpoint, byte[] identity)
    {
        var socket = new DealerSocket();
        socket.Options.Identity = identity;
        socket.Connect(endpoint);
        return socket;
    }

    private void Terminate(Exception exception)
    {
        if (Interlocked.CompareExchange(ref terminalError, exception, null) is null)
        {
            incomingMessages.Writer.TryComplete(exception);
            Volatile.Read(ref ioLoop)?.RequestStop();
        }
    }

    private void ThrowIfTerminated()
    {
        if (Volatile.Read(ref terminalError) is { } exception)
            throw new JupyterProtocolException("The Jupyter client transport has terminated.", exception);
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

    private sealed record ActivePing(PingCommand Command, byte[] Payload, long StartTimestamp);

    private enum IoSignal
    {
        CommandsAvailable,
        Stop
    }

    private sealed class ClientIoLoop : IDisposable
    {
        private readonly DealerSocket control;
        private readonly RequestSocket heartbeat;
        private readonly SubscriberSocket iopub;
        private readonly Lock lifecycleGate = new();
        private readonly NetMqJupyterTransport owner;
        private readonly Queue<PingCommand> pendingPings = new();
        private readonly NetMQPoller poller;
        private readonly SerializationContext serialization;
        private readonly DealerSocket shell;
        private readonly NetMQQueue<IoSignal> signals;
        private readonly DealerSocket stdin;
        private ActivePing? activePing;
        private bool disposed;
        private int stopRequested;

        private ClientIoLoop(NetMqJupyterTransport owner)
        {
            this.owner = owner;
            try
            {
                serialization = new SerializationContext(owner.connectionInfo);
                shell = CreateDealerSocket(
                    owner.connectionInfo.Endpoint(JupyterChannel.Shell),
                    serialization.ClientIdentity);
                control = CreateDealerSocket(
                    owner.connectionInfo.Endpoint(JupyterChannel.Control),
                    Guid.NewGuid().ToByteArray());
                stdin = CreateDealerSocket(
                    owner.connectionInfo.Endpoint(JupyterChannel.Stdin),
                    serialization.ClientIdentity);
                iopub = new SubscriberSocket();
                heartbeat = new RequestSocket();
                signals = new NetMQQueue<IoSignal>();
                poller = new NetMQPoller();

                iopub.Connect(owner.connectionInfo.Endpoint(JupyterChannel.Iopub));
                iopub.SubscribeToAnyTopic();
                heartbeat.Connect(owner.connectionInfo.Endpoint(JupyterChannel.Heartbeat));

                shell.ReceiveReady += OnShellReceiveReady;
                control.ReceiveReady += OnControlReceiveReady;
                stdin.ReceiveReady += OnStdinReceiveReady;
                iopub.ReceiveReady += OnIopubReceiveReady;
                heartbeat.ReceiveReady += OnHeartbeatReceiveReady;
                signals.ReceiveReady += OnSignalReady;

                poller.Add(shell);
                poller.Add(control);
                poller.Add(stdin);
                poller.Add(iopub);
                poller.Add(heartbeat);
                poller.Add(signals);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            lock (lifecycleGate)
            {
                if (disposed) return;

                disposed = true;
            }

            shell.ReceiveReady -= OnShellReceiveReady;

            control.ReceiveReady -= OnControlReceiveReady;

            stdin.ReceiveReady -= OnStdinReceiveReady;

            iopub.ReceiveReady -= OnIopubReceiveReady;

            heartbeat.ReceiveReady -= OnHeartbeatReceiveReady;

            signals.ReceiveReady -= OnSignalReady;

            poller.Dispose();
            signals.Dispose();
            heartbeat.Dispose();
            iopub.Dispose();
            stdin.Dispose();
            control.Dispose();
            shell.Dispose();
        }

        public static ClientIoLoop Create(NetMqJupyterTransport owner)
        {
            return new ClientIoLoop(owner);
        }

        public void Run()
        {
            poller.Run();
        }

        public bool TrySignalCommandsAvailable()
        {
            lock (lifecycleGate)
            {
                if (disposed || Volatile.Read(ref stopRequested) != 0) return false;

                signals.Enqueue(IoSignal.CommandsAvailable);
                return true;
            }
        }

        public void RequestStop()
        {
            lock (lifecycleGate)
            {
                if (disposed || Interlocked.Exchange(ref stopRequested, 1) != 0) return;

                signals.Enqueue(IoSignal.Stop);
            }
        }

        public void FailPending(Exception exception)
        {
            activePing?.Command.Fail(exception);
            activePing = null;
            while (pendingPings.TryDequeue(out var ping)) ping.Fail(exception);
        }

        private void OnShellReceiveReady(object? sender, NetMQSocketEventArgs args)
        {
            ReceiveAvailable(JupyterTransportChannel.Shell, args.Socket);
        }

        private void OnControlReceiveReady(object? sender, NetMQSocketEventArgs args)
        {
            ReceiveAvailable(JupyterTransportChannel.Control, args.Socket);
        }

        private void OnStdinReceiveReady(object? sender, NetMQSocketEventArgs args)
        {
            ReceiveAvailable(JupyterTransportChannel.Stdin, args.Socket);
        }

        private void OnIopubReceiveReady(object? sender, NetMQSocketEventArgs args)
        {
            ReceiveAvailable(JupyterTransportChannel.Iopub, args.Socket);
        }

        private void OnHeartbeatReceiveReady(object? sender, NetMQSocketEventArgs args)
        {
            var response = args.Socket.ReceiveFrameBytes();
            if (activePing is { } ping && response.AsSpan().SequenceEqual(ping.Payload))
                ping.Command.Completion.TrySetResult(Stopwatch.GetElapsedTime(ping.StartTimestamp));
            else
                activePing?.Command.Completion.TrySetException(
                    new JupyterProtocolException("Heartbeat reply did not match its request."));

            activePing = null;
            StartNextPing();
        }

        private void OnSignalReady(object? sender, NetMQQueueEventArgs<IoSignal> args)
        {
            var commandsAvailable = false;
            while (args.Queue.TryDequeue(out var signal, TimeSpan.Zero))
                commandsAvailable |= signal == IoSignal.CommandsAvailable;

            if (Volatile.Read(ref stopRequested) != 0)
            {
                poller.Stop();
                return;
            }

            if (commandsAvailable) ProcessCommands();
        }

        private void ProcessCommands()
        {
            while (owner.outgoingCommands.Reader.TryRead(out var command))
                switch (command)
                {
                    case SendCommand send:
                        try
                        {
                            Send(
                                send.Channel,
                                send.Message,
                                serialization.Serializer,
                                shell,
                                control,
                                stdin);
                            send.Completion.TrySetResult();
                        }
                        catch (Exception exception)
                        {
                            send.Completion.TrySetException(exception);
                            throw;
                        }

                        break;
                    case PingCommand ping:
                        pendingPings.Enqueue(ping);
                        StartNextPing();
                        break;
                }
        }

        private void StartNextPing()
        {
            if (activePing is not null || !pendingPings.TryDequeue(out var command)) return;

            var payload = Guid.NewGuid().ToByteArray();
            activePing = new ActivePing(command, payload, Stopwatch.GetTimestamp());
            heartbeat.SendFrame(payload);
        }

        private void ReceiveAvailable(JupyterTransportChannel channel, NetMQSocket socket)
        {
            while (true)
            {
                var message = new NetMQMessage();
                if (!socket.TryReceiveMultipartMessage(TimeSpan.Zero, ref message)) return;

                var wireMessage = serialization.Serializer.Deserialize(
                    message.Select(frame => frame.ToByteArray()).ToArray());
                if (owner.incomingMessages.Writer.TryWrite(new JupyterTransportMessage(channel, wireMessage))) continue;

                var exception = new JupyterBackpressureException(
                    $"The Jupyter client incoming queue exceeded its capacity of {owner.options.IncomingCapacity}.");
                owner.Terminate(exception);
                return;
            }
        }
    }

    private sealed class SerializationContext
    {
        public SerializationContext(JupyterConnectionInfo connectionInfo)
        {
            ClientIdentity = Guid.NewGuid().ToByteArray();
            Serializer = new JupyterMessageSerializer(connectionInfo.Key, connectionInfo.SignatureScheme);
        }

        public byte[] ClientIdentity { get; }

        public IJupyterMessageSerializer Serializer { get; }
    }
}
