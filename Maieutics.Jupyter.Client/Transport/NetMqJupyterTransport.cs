using System.Diagnostics;
using System.Threading.Channels;
using Maieutics.Jupyter.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace Maieutics.Jupyter.Client.Transport;

public sealed class NetMqJupyterTransport : IJupyterTransport
{
    private readonly JupyterConnectionInfo connectionInfo;
    private readonly JupyterTransportOptions options;
    private readonly Channel<JupyterTransportMessage> incomingMessages;
    private readonly Channel<ClientCommand> outgoingCommands;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread ioThread;
    private NetMQQueue<byte>? commandSignal;
    private NetMQPoller? poller;
    private Exception? terminalError;
    private int disposeState;

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

    public static async Task<NetMqJupyterTransport> ConnectAsync(
        JupyterConnectionInfo connectionInfo,
        JupyterTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
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

    public async ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default)
    {
        if (channel == JupyterTransportChannel.Iopub)
        {
            throw new InvalidOperationException("A Jupyter client cannot send messages on IOPub.");
        }

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

        Volatile.Read(ref poller)?.StopAsync();
        await stopped.Task.ConfigureAwait(false);
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
            var signal = Volatile.Read(ref commandSignal)
                         ?? throw new ObjectDisposedException(nameof(NetMqJupyterTransport));
            signal.Enqueue(0);
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
        var pendingPings = new Queue<PingCommand>();
        ActivePing? activePing = null;
        Exception? failure = null;

        try
        {
            using var serializer = new SerializerOwner(connectionInfo);
            using var shell =
                CreateDealerSocket(connectionInfo.Endpoint(JupyterChannel.Shell), serializer.ClientIdentity);
            using var control = CreateDealerSocket(connectionInfo.Endpoint(JupyterChannel.Control),
                Guid.NewGuid().ToByteArray());
            using var stdin =
                CreateDealerSocket(connectionInfo.Endpoint(JupyterChannel.Stdin), serializer.ClientIdentity);
            using var iopub = new SubscriberSocket();
            using var heartbeat = new RequestSocket();
            using var queue = new NetMQQueue<byte>(0);
            using var poller = new NetMQPoller { shell, control, stdin, iopub, heartbeat, queue };

            iopub.Connect(connectionInfo.Endpoint(JupyterChannel.Iopub));
            iopub.SubscribeToAnyTopic();
            heartbeat.Connect(connectionInfo.Endpoint(JupyterChannel.Heartbeat));

            shell.ReceiveReady += (_, args) =>
                ReceiveAvailable(JupyterTransportChannel.Shell, args.Socket, serializer.Serializer, poller);
            control.ReceiveReady += (_, args) =>
                ReceiveAvailable(JupyterTransportChannel.Control, args.Socket, serializer.Serializer, poller);
            stdin.ReceiveReady += (_, args) =>
                ReceiveAvailable(JupyterTransportChannel.Stdin, args.Socket, serializer.Serializer, poller);
            iopub.ReceiveReady += (_, args) =>
                ReceiveAvailable(JupyterTransportChannel.Iopub, args.Socket, serializer.Serializer, poller);
            heartbeat.ReceiveReady += (_, args) =>
            {
                var response = args.Socket.ReceiveFrameBytes();
                if (activePing is { } ping && response.AsSpan().SequenceEqual(ping.Payload))
                {
                    ping.Command.Completion.TrySetResult(Stopwatch.GetElapsedTime(ping.StartTimestamp));
                }
                else
                {
                    activePing?.Command.Completion.TrySetException(
                        new JupyterProtocolException("Heartbeat reply did not match its request."));
                }

                activePing = null;
                StartNextPing(heartbeat, pendingPings, ref activePing);
            };
            queue.ReceiveReady += (_, args) =>
            {
                while (args.Queue.TryDequeue(out byte _, TimeSpan.Zero))
                {
                }

                while (outgoingCommands.Reader.TryRead(out var command))
                {
                    switch (command)
                    {
                        case SendCommand send:
                            try
                            {
                                Send(send.Channel, send.Message, serializer.Serializer, shell, control, stdin);
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
                            StartNextPing(heartbeat, pendingPings, ref activePing);
                            break;
                    }
                }
            };

            commandSignal = queue;
            this.poller = poller;
            ready.TrySetResult();
            if (Volatile.Read(ref disposeState) == 0)
            {
                poller.Run();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            ready.TrySetException(exception);
            Interlocked.CompareExchange(ref terminalError, exception, null);
        }
        finally
        {
            commandSignal = null;
            poller = null;
            var completionError = terminalError ?? failure;
            var pendingError = completionError ?? new ObjectDisposedException(nameof(NetMqJupyterTransport));
            while (outgoingCommands.Reader.TryRead(out var command))
            {
                command.Fail(pendingError);
            }

            activePing?.Command.Fail(pendingError);
            foreach (var ping in pendingPings)
            {
                ping.Fail(pendingError);
            }

            incomingMessages.Writer.TryComplete(completionError);
            stopped.TrySetResult();
        }
    }

    private void ReceiveAvailable(
        JupyterTransportChannel channel,
        NetMQSocket socket,
        IJupyterMessageSerializer serializer,
        NetMQPoller poller)
    {
        while (true)
        {
            var message = new NetMQMessage();
            if (!socket.TryReceiveMultipartMessage(TimeSpan.Zero, ref message))
            {
                return;
            }

            var wireMessage = serializer.Deserialize(message.Select(frame => frame.ToByteArray()).ToArray());
            if (!incomingMessages.Writer.TryWrite(new JupyterTransportMessage(channel, wireMessage)))
            {
                var exception = new JupyterBackpressureException(
                    $"The Jupyter client incoming queue exceeded its capacity of {options.IncomingCapacity}.");
                incomingMessages.Writer.TryComplete(exception);
                Interlocked.CompareExchange(ref terminalError, exception, null);
                poller.StopAsync();
                return;
            }
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
        foreach (var frame in serializer.Serialize(JupyterWireMessage.Create(message)))
        {
            netMqMessage.Append(frame);
        }

        socket.SendMultipartMessage(netMqMessage);
    }

    private static void StartNextPing(
        RequestSocket heartbeat,
        Queue<PingCommand> pendingPings,
        ref ActivePing? activePing)
    {
        if (activePing is not null || !pendingPings.TryDequeue(out var command))
        {
            return;
        }

        var payload = Guid.NewGuid().ToByteArray();
        activePing = new ActivePing(command, payload, Stopwatch.GetTimestamp());
        heartbeat.SendFrame(payload);
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
            Volatile.Read(ref poller)?.StopAsync();
        }
    }

    private void ThrowIfTerminated()
    {
        if (Volatile.Read(ref terminalError) is { } exception)
        {
            throw new JupyterProtocolException("The Jupyter client transport has terminated.", exception);
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
        public override void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed record PingCommand(TaskCompletionSource<TimeSpan> Completion) : ClientCommand
    {
        public override void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed record ActivePing(PingCommand Command, byte[] Payload, long StartTimestamp);

    private sealed class SerializerOwner : IDisposable
    {
        public SerializerOwner(JupyterConnectionInfo connectionInfo)
        {
            ClientIdentity = Guid.NewGuid().ToByteArray();
            Serializer = new JupyterMessageSerializer(connectionInfo.Key, connectionInfo.SignatureScheme);
        }

        public byte[] ClientIdentity { get; }

        public IJupyterMessageSerializer Serializer { get; }

        public void Dispose()
        {
        }
    }
}