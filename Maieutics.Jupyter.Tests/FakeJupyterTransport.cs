using System.Threading.Channels;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

internal sealed class FakeJupyterTransport : IJupyterTransport
{
    private readonly Channel<JupyterTransportMessage> incomingMessages =
        Channel.CreateUnbounded<JupyterTransportMessage>();

    private readonly List<JupyterTransportMessage> sentMessages = [];
    private readonly Lock sentMessagesGate = new();

    public IReadOnlyList<JupyterTransportMessage> SentMessages
    {
        get
        {
            lock (sentMessagesGate)
            {
                return sentMessages.ToArray();
            }
        }
    }

    public IAsyncEnumerable<JupyterTransportMessage> IncomingMessages => incomingMessages.Reader.ReadAllAsync();

    public ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sentMessagesGate)
        {
            sentMessages.Add(new JupyterTransportMessage(channel, JupyterWireMessage.Create(message)));
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
