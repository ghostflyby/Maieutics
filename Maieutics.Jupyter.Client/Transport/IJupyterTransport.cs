using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client.Transport;

public interface IJupyterTransport : IAsyncDisposable
{
    IAsyncEnumerable<JupyterTransportMessage> IncomingMessages { get; }

    /// <summary>Gets the number of incoming messages currently buffered but not yet routed.</summary>
    int PendingIncomingCount { get; }

    /// <summary>
    /// Attempts to read a buffered incoming message without waiting for new traffic.
    /// </summary>
    bool TryReadIncoming(out JupyterTransportMessage message);

    /// <summary>
    /// Waits for an incoming message to become available, for the timeout to elapse, or for the
    /// channel to complete. Returns false when the timeout elapses or the channel completes.
    /// </summary>
    ValueTask<bool> WaitToReadAsync(TimeSpan timeout, CancellationToken cancellationToken);

    ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default);

    Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default);
}

public sealed record JupyterTransportOptions
{
    public int IncomingCapacity { get; init; } = 1024;

    public int OutgoingCapacity { get; init; } = 256;
}
