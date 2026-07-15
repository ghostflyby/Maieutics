using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client.Transport;

public interface IJupyterTransport : IAsyncDisposable
{
    IAsyncEnumerable<JupyterTransportMessage> IncomingMessages { get; }

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