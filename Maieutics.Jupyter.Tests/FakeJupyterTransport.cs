using System.Threading.Channels;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

internal sealed class FakeJupyterTransport : IJupyterTransport
{
    private readonly Channel<JupyterTransportMessage> incomingMessages =
        Channel.CreateUnbounded<JupyterTransportMessage>();

    private readonly List<JupyterTransportMessage> sentMessages = [];

    public IAsyncEnumerable<JupyterTransportMessage> IncomingMessages => incomingMessages.Reader.ReadAllAsync();

    public IReadOnlyList<JupyterTransportMessage> SentMessages => sentMessages;

    public ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default)
    {
        sentMessages.Add(new JupyterTransportMessage(channel, message));
        return ValueTask.CompletedTask;
    }

    public void Receive(JupyterTransportChannel channel, JupyterMessage message)
    {
        incomingMessages.Writer.TryWrite(new JupyterTransportMessage(channel, message));
    }

    public ValueTask DisposeAsync()
    {
        incomingMessages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}