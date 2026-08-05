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
    private KernelIoLoop? ioLoop;
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
        cancellationToken.ThrowIfCancellationRequested();
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
            var loop = Volatile.Read(ref ioLoop)
                       ?? throw new ObjectDisposedException(nameof(NetMqJupyterKernelTransport));
            if (!loop.TrySignalCommandsAvailable())
            {
                throw new ObjectDisposedException(nameof(NetMqJupyterKernelTransport));
            }
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

        Volatile.Read(ref ioLoop)?.RequestStop();
        await stopped.Task.ConfigureAwait(false);
    }

    private void RunIoThread()
    {
        KernelIoLoop? loop = null;
        Exception? failure = null;

        try
        {
            loop = KernelIoLoop.Create(this);
            Volatile.Write(ref ioLoop, loop);
            if (Volatile.Read(ref disposeState) != 0 || Volatile.Read(ref terminalError) is not null)
            {
                loop.RequestStop();
            }

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
            if (loop is not null)
            {
                Interlocked.CompareExchange(ref ioLoop, null, loop);
            }

            var completionError = terminalError ?? failure;
            var pendingError = completionError ?? new ObjectDisposedException(nameof(NetMqJupyterKernelTransport));
            while (outgoingCommands.Reader.TryRead(out var command))
            {
                command.Fail(pendingError);
            }

            loop?.Dispose();
            incomingEvents.Writer.TryComplete(completionError);
            stopped.TrySetResult();
        }
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
        Volatile.Read(ref ioLoop)?.RequestStop();
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

    private enum IoSignal
    {
        CommandsAvailable,
        Stop
    }

    private sealed class KernelIoLoop : IDisposable
    {
        private readonly NetMqJupyterKernelTransport owner;
        private readonly Lock lifecycleGate = new();
        private readonly IJupyterMessageSerializer serializer;
        private readonly RouterSocket shell;
        private readonly RouterSocket control;
        private readonly RouterSocket stdin;
        private readonly XPublisherSocket iopub;
        private readonly ResponseSocket heartbeat;
        private readonly NetMQQueue<IoSignal> signals;
        private readonly NetMQPoller poller;
        private int stopRequested;
        private bool disposed;

        private KernelIoLoop(NetMqJupyterKernelTransport owner)
        {
            this.owner = owner;
            try
            {
                serializer = new JupyterMessageSerializer(
                    owner.connectionInfo.Key,
                    owner.connectionInfo.SignatureScheme);
                shell = new RouterSocket();
                control = new RouterSocket();
                stdin = new RouterSocket();
                iopub = new XPublisherSocket();
                heartbeat = new ResponseSocket();
                signals = new NetMQQueue<IoSignal>();
                poller = new NetMQPoller();

                shell.Bind(owner.connectionInfo.Endpoint(JupyterChannel.Shell));
                control.Bind(owner.connectionInfo.Endpoint(JupyterChannel.Control));
                stdin.Bind(owner.connectionInfo.Endpoint(JupyterChannel.Stdin));
                iopub.Bind(owner.connectionInfo.Endpoint(JupyterChannel.Iopub));
                heartbeat.Bind(owner.connectionInfo.Endpoint(JupyterChannel.Heartbeat));

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

        public static KernelIoLoop Create(NetMqJupyterKernelTransport owner) => new(owner);

        public void Run() => poller.Run();

        public bool TrySignalCommandsAvailable()
        {
            lock (lifecycleGate)
            {
                if (disposed || Volatile.Read(ref stopRequested) != 0)
                {
                    return false;
                }

                signals.Enqueue(IoSignal.CommandsAvailable);
                return true;
            }
        }

        public void RequestStop()
        {
            lock (lifecycleGate)
            {
                if (disposed || Interlocked.Exchange(ref stopRequested, 1) != 0)
                {
                    return;
                }

                signals.Enqueue(IoSignal.Stop);
            }
        }

        public void Dispose()
        {
            lock (lifecycleGate)
            {
                if (disposed)
                {
                    return;
                }

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

        private void OnShellReceiveReady(object? sender, NetMQSocketEventArgs args) =>
            ReceiveMessages(JupyterKernelChannel.Shell, args.Socket);

        private void OnControlReceiveReady(object? sender, NetMQSocketEventArgs args) =>
            ReceiveMessages(JupyterKernelChannel.Control, args.Socket);

        private void OnStdinReceiveReady(object? sender, NetMQSocketEventArgs args) =>
            ReceiveMessages(JupyterKernelChannel.Stdin, args.Socket);

        private void OnIopubReceiveReady(object? sender, NetMQSocketEventArgs args) =>
            ReceiveSubscription(args.Socket);

        private static void OnHeartbeatReceiveReady(object? sender, NetMQSocketEventArgs args)
        {
            var payload = args.Socket.ReceiveFrameBytes();
            args.Socket.SendFrame(payload);
        }

        private void OnSignalReady(object? sender, NetMQQueueEventArgs<IoSignal> args)
        {
            var commandsAvailable = false;
            while (args.Queue.TryDequeue(out var signal, TimeSpan.Zero))
            {
                commandsAvailable |= signal == IoSignal.CommandsAvailable;
            }

            if (Volatile.Read(ref stopRequested) != 0)
            {
                poller.Stop();
                return;
            }

            if (commandsAvailable)
            {
                ProcessCommands();
            }
        }

        private void ProcessCommands()
        {
            while (owner.outgoingCommands.Reader.TryRead(out var command))
            {
                switch (command)
                {
                    case SendCommand send:
                        try
                        {
                            Send(
                                send.Channel,
                                send.Message,
                                serializer,
                                shell,
                                control,
                                iopub,
                                stdin);
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
        }

        private void ReceiveMessages(JupyterKernelChannel channel, NetMQSocket socket)
        {
            while (true)
            {
                var message = new NetMQMessage();
                if (!socket.TryReceiveMultipartMessage(TimeSpan.Zero, ref message))
                {
                    return;
                }

                var wireMessage = serializer.Deserialize(
                    message.Select(frame => frame.ToByteArray()).ToArray());
                if (owner.incomingEvents.Writer.TryWrite(new JupyterKernelMessageReceived(channel, wireMessage)))
                {
                    continue;
                }

                FailBackpressure();
                return;
            }
        }

        private void ReceiveSubscription(NetMQSocket socket)
        {
            while (socket.TryReceiveFrameBytes(out var subscription))
            {
                if (subscription.Length == 0 || subscription[0] == 0)
                {
                    continue;
                }

                if (owner.incomingEvents.Writer.TryWrite(new JupyterIopubSubscriptionReceived(subscription[1..])))
                {
                    continue;
                }

                FailBackpressure();
                return;
            }
        }

        private void FailBackpressure()
        {
            owner.Terminate(new JupyterKernelBackpressureException(
                $"The Jupyter kernel incoming queue exceeded its capacity of {owner.options.IncomingCapacity}."));
        }
    }
}

internal sealed class JupyterKernelBackpressureException(string message) : Exception(message);
