using System.Threading.Channels;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

internal sealed class FakeJupyterTransport : IJupyterTransport
{
    private readonly Channel<JupyterTransportMessage> incomingMessages =
        Channel.CreateUnbounded<JupyterTransportMessage>();

    private readonly List<JupyterTransportMessage> sentMessages = [];
    private readonly object sentMessagesGate = new();

    public IAsyncEnumerable<JupyterTransportMessage> IncomingMessages => incomingMessages.Reader.ReadAllAsync();

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

    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(TimeSpan.FromMilliseconds(1));

    public void Receive(
        JupyterTransportChannel channel,
        JupyterMessage message,
        IReadOnlyList<byte[]>? identities = null)
    {
        incomingMessages.Writer.TryWrite(new JupyterTransportMessage(
            channel,
            new JupyterWireMessage(identities ?? [], message, [])));
    }

    public ValueTask DisposeAsync()
    {
        incomingMessages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}