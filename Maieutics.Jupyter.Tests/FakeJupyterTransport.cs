using System.Threading.Channels;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

internal sealed class FakeJupyterTransport : IJupyterTransport
{
    private readonly Channel<JupyterTransportMessage> incomingMessages =
        Channel.CreateUnbounded<JupyterTransportMessage>();

    private readonly Channel<JupyterTransportMessage> sentMessages =
        Channel.CreateUnbounded<JupyterTransportMessage>();

    private readonly List<JupyterTransportMessage> sentMessagesHistory = [];
    private readonly Lock sentMessagesGate = new();

    public IReadOnlyList<JupyterTransportMessage> SentMessages
    {
        get
        {
            lock (sentMessagesGate)
            {
                return sentMessagesHistory.ToArray();
            }
        }
    }

    public IAsyncEnumerable<JupyterTransportMessage> SentMessageStream => sentMessages.Reader.ReadAllAsync();

    public IAsyncEnumerable<JupyterTransportMessage> IncomingMessages => incomingMessages.Reader.ReadAllAsync();

    public ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transportMessage = new JupyterTransportMessage(channel, JupyterWireMessage.Create(message));
        sentMessages.Writer.TryWrite(transportMessage);
        lock (sentMessagesGate)
        {
            sentMessagesHistory.Add(transportMessage);
        }

        return ValueTask.CompletedTask;
    }

    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TimeSpan.FromMilliseconds(1));
    }

    public ValueTask DisposeAsync()
    {
        incomingMessages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public void Receive(
        JupyterTransportChannel channel,
        JupyterMessage message,
        IReadOnlyList<byte[]>? identities = null)
    {
        incomingMessages.Writer.TryWrite(new JupyterTransportMessage(
            channel,
            new JupyterWireMessage(identities ?? [], message, [])));
    }
}
