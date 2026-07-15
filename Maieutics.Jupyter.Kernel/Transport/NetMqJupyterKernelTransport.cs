using System.Threading.Channels;
using Maieutics.Jupyter.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace Maieutics.Jupyter.Kernel.Transport;

internal sealed class NetMqJupyterKernelTransport : IJupyterKernelTransport
{
    private readonly JupyterConnectionInfo connectionInfo;
    private readonly JupyterKernelTransportOptions options;
    private readonly Channel<JupyterKernelTransportEvent> incomingEvents;
    private readonly Channel<KernelCommand> outgoingCommands;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread ioThread;
    private NetMQQueue<byte>? commandSignal;
    private NetMQPoller? poller;
    private Exception? terminalError;
    private int disposeState;

    private NetMqJupyterKernelTransport(JupyterConnectionInfo connectionInfo, JupyterKernelTransportOptions options)
    {
        this.connectionInfo = connectionInfo;
        this.options = options;
        incomingEvents = Channel.CreateBounded<JupyterKernelTransportEvent>(
            new BoundedChannelOptions(options.IncomingCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        outgoingCommands = Channel.CreateBounded<KernelCommand>(new BoundedChannelOptions(options.OutgoingCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        ioThread = new Thread(RunIoThread)
        {
            IsBackground = true,
            Name = "Maieutics.Jupyter.Kernel.NetMQ"
        };
    }

    public IAsyncEnumerable<JupyterKernelTransportEvent> IncomingEvents => incomingEvents.Reader.ReadAllAsync();

    public static async Task<NetMqJupyterKernelTransport> BindAsync(
        JupyterConnectionInfo connectionInfo,
        JupyterKernelTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        connectionInfo.ValidateSupported();
        var transport = new NetMqJupyterKernelTransport(connectionInfo, options ?? new JupyterKernelTransportOptions());
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
        JupyterKernelChannel channel,
        JupyterWireMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfTerminated();

        var command = new SendCommand(channel, message, completion);
        if (!outgoingCommands.Writer.TryWrite(command))
        {
            var exception = new JupyterKernelBackpressureException(
                $"The Jupyter kernel outgoing queue exceeded its capacity of {options.OutgoingCapacity}.");
            command.Fail(exception);
            Terminate(exception);
            throw exception;
        }

        try
        {
            var signal = Volatile.Read(ref commandSignal)
                         ?? throw new ObjectDisposedException(nameof(NetMqJupyterKernelTransport));
            signal.Enqueue(0);
        }
        catch (Exception exception)
        {
            command.Fail(exception);
            Terminate(exception);
            throw;
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    private void RunIoThread()
    {
        Exception? failure = null;

        try
        {
            var serializer = new JupyterMessageSerializer(connectionInfo.Key, connectionInfo.SignatureScheme);
            using var shell = new RouterSocket();
            using var control = new RouterSocket();
            using var stdin = new RouterSocket();
            using var iopub = new XPublisherSocket();
            using var heartbeat = new ResponseSocket();
            using var queue = new NetMQQueue<byte>(0);
            using var poller = new NetMQPoller { shell, control, stdin, iopub, heartbeat, queue };

            shell.Bind(connectionInfo.Endpoint(JupyterChannel.Shell));
            control.Bind(connectionInfo.Endpoint(JupyterChannel.Control));
            stdin.Bind(connectionInfo.Endpoint(JupyterChannel.Stdin));
            iopub.Bind(connectionInfo.Endpoint(JupyterChannel.Iopub));
            heartbeat.Bind(connectionInfo.Endpoint(JupyterChannel.Heartbeat));

            shell.ReceiveReady += (_, args) =>
                ReceiveMessages(JupyterKernelChannel.Shell, args.Socket, serializer, poller);
            control.ReceiveReady += (_, args) =>
                ReceiveMessages(JupyterKernelChannel.Control, args.Socket, serializer, poller);
            stdin.ReceiveReady += (_, args) =>
                ReceiveMessages(JupyterKernelChannel.Stdin, args.Socket, serializer, poller);
            iopub.ReceiveReady += (_, args) => ReceiveSubscription(args.Socket, poller);
            heartbeat.ReceiveReady += (_, args) =>
            {
                var payload = args.Socket.ReceiveFrameBytes();
                args.Socket.SendFrame(payload);
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
                                Send(send.Channel, send.Message, serializer, shell, control, iopub, stdin);
                                send.Completion.TrySetResult();
                            }
                            catch (Exception exception)
                            {
                                send.Completion.TrySetException(exception);
                                throw;
                            }

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
            var pendingError = completionError ?? new ObjectDisposedException(nameof(NetMqJupyterKernelTransport));
            while (outgoingCommands.Reader.TryRead(out var command))
            {
                command.Fail(pendingError);
            }

            incomingEvents.Writer.TryComplete(completionError);
            stopped.TrySetResult();
        }
    }

    private void ReceiveMessages(
        JupyterKernelChannel channel,
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
            if (!incomingEvents.Writer.TryWrite(new JupyterKernelMessageReceived(channel, wireMessage)))
            {
                FailBackpressure(poller);
                return;
            }
        }
    }

    private void ReceiveSubscription(NetMQSocket socket, NetMQPoller poller)
    {
        while (socket.TryReceiveFrameBytes(out var subscription))
        {
            if (subscription.Length == 0 || subscription[0] == 0)
            {
                continue;
            }

            if (!incomingEvents.Writer.TryWrite(new JupyterIopubSubscriptionReceived(subscription[1..])))
            {
                FailBackpressure(poller);
                return;
            }
        }
    }

    private void FailBackpressure(NetMQPoller poller)
    {
        var exception = new JupyterKernelBackpressureException(
            $"The Jupyter kernel incoming queue exceeded its capacity of {options.IncomingCapacity}.");
        incomingEvents.Writer.TryComplete(exception);
        Interlocked.CompareExchange(ref terminalError, exception, null);
        poller.StopAsync();
    }

    private static void Send(
        JupyterKernelChannel channel,
        JupyterWireMessage message,
        IJupyterMessageSerializer serializer,
        RouterSocket shell,
        RouterSocket control,
        XPublisherSocket iopub,
        RouterSocket stdin)
    {
        var socket = channel switch
        {
            JupyterKernelChannel.Shell => (IOutgoingSocket)shell,
            JupyterKernelChannel.Control => control,
            JupyterKernelChannel.Iopub => iopub,
            JupyterKernelChannel.Stdin => stdin,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
        };
        var netMqMessage = new NetMQMessage();
        foreach (var frame in serializer.Serialize(message))
        {
            netMqMessage.Append(frame);
        }

        socket.SendMultipartMessage(netMqMessage);
    }

    private void Terminate(Exception exception)
    {
        if (Interlocked.CompareExchange(ref terminalError, exception, null) is not null) return;
        incomingEvents.Writer.TryComplete(exception);
        Volatile.Read(ref poller)?.StopAsync();
    }

    private void ThrowIfTerminated()
    {
        if (Volatile.Read(ref terminalError) is { } exception)
        {
            throw new JupyterProtocolException("The Jupyter kernel transport has terminated.", exception);
        }
    }

    private abstract record KernelCommand
    {
        public abstract void Fail(Exception exception);
    }

    private sealed record SendCommand(
        JupyterKernelChannel Channel,
        JupyterWireMessage Message,
        TaskCompletionSource Completion) : KernelCommand
    {
        public override void Fail(Exception exception) => Completion.TrySetException(exception);
    }
}

internal sealed class JupyterKernelBackpressureException(string message) : Exception(message);