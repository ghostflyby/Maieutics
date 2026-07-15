using System.Threading.Channels;
using Maieutics.Jupyter.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace Maieutics.Jupyter.Client.Transport;

public sealed class NetMqJupyterTransport : IJupyterTransport
{
    private readonly DealerSocket shell;
    private readonly DealerSocket control;
    private readonly DealerSocket stdin;
    private readonly SubscriberSocket iopub;
    private readonly IJupyterMessageSerializer serializer;

    private readonly Channel<JupyterTransportMessage> incomingMessages =
        Channel.CreateUnbounded<JupyterTransportMessage>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });

    private readonly CancellationTokenSource disposal = new();
    private readonly Task receiveLoop;
    private readonly Lock sendLock = new();
    private bool disposed;

    public NetMqJupyterTransport(JupyterConnectionInfo connectionInfo)
    {
        serializer = new JupyterMessageSerializer(connectionInfo.Key);
        shell = CreateDealerSocket(connectionInfo.Endpoint(JupyterChannel.Shell));
        control = CreateDealerSocket(connectionInfo.Endpoint(JupyterChannel.Control));
        stdin = CreateDealerSocket(connectionInfo.Endpoint(JupyterChannel.Stdin));
        iopub = new SubscriberSocket();
        iopub.Connect(connectionInfo.Endpoint(JupyterChannel.Iopub));
        iopub.SubscribeToAnyTopic();

        receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public IAsyncEnumerable<JupyterTransportMessage> IncomingMessages =>
        incomingMessages.Reader.ReadAllAsync();

    public ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var socket = channel switch
        {
            JupyterTransportChannel.Shell => shell,
            JupyterTransportChannel.Control => control,
            JupyterTransportChannel.Stdin => stdin,
            JupyterTransportChannel.Iopub => throw new InvalidOperationException("Cannot send messages on IOPub."),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
        };

        var netMqMessage = new NetMQMessage();
        foreach (var frame in serializer.Serialize(message))
        {
            netMqMessage.Append(frame);
        }

        lock (sendLock)
        {
            socket.SendMultipartMessage(netMqMessage);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await disposal.CancelAsync();

        try
        {
            await receiveLoop;
        }
        catch (OperationCanceledException)
        {
        }

        shell.Dispose();
        control.Dispose();
        stdin.Dispose();
        iopub.Dispose();
        disposal.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!disposal.IsCancellationRequested)
            {
                ReceiveAvailable(JupyterTransportChannel.Shell, shell);
                ReceiveAvailable(JupyterTransportChannel.Control, control);
                ReceiveAvailable(JupyterTransportChannel.Stdin, stdin);
                ReceiveAvailable(JupyterTransportChannel.Iopub, iopub);

                await Task.Delay(10, disposal.Token);
            }
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            incomingMessages.Writer.TryComplete(ex);
            return;
        }

        incomingMessages.Writer.TryComplete();
    }

    private void ReceiveAvailable(JupyterTransportChannel channel, IReceivingSocket socket)
    {
        while (!disposal.IsCancellationRequested)
        {
            var message = new NetMQMessage();
            if (!socket.TryReceiveMultipartMessage(TimeSpan.Zero, ref message))
            {
                return;
            }

            var jupyterMessage = serializer.Deserialize(message.Select(frame => frame.ToByteArray()).ToArray());
            incomingMessages.Writer.TryWrite(new JupyterTransportMessage(channel, jupyterMessage));
        }
    }

    private static DealerSocket CreateDealerSocket(string endpoint)
    {
        var socket = new DealerSocket();
        socket.Options.Identity = Guid.NewGuid().ToByteArray();
        socket.Connect(endpoint);
        return socket;
    }
}