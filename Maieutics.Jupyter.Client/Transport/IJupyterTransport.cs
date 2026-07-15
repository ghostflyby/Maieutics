using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client.Transport;

public interface IJupyterTransport : IAsyncDisposable
{
    IAsyncEnumerable<JupyterTransportMessage> IncomingMessages { get; }

    ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default);
}