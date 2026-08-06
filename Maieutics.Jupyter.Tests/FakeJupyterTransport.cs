using System.Diagnostics.CodeAnalysis;
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

    public int PendingIncomingCount => incomingMessages.Reader.Count;

    public bool TryReadIncoming([NotNullWhen(true)] out JupyterTransportMessage? message)
    {
        if (incomingMessages.Reader.TryRead(out var item))
        {
            message = item;
            return true;
        }

        message = null;
        return false;
    }

    public async ValueTask<bool> WaitToReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (incomingMessages.Reader.Count > 0) return true;

        try
        {
            return await incomingMessages.Reader.WaitToReadAsync(cancellationToken).AsTask()
                .WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
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