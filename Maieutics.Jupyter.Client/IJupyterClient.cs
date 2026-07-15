using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public interface IJupyterClient : IAsyncDisposable
{
    IAsyncEnumerable<JupyterClientEvent> WatchEventsAsync(
        CancellationToken cancellationToken = default);

    Task<JupyterKernelInfo> GetKernelInfoAsync(CancellationToken cancellationToken = default);

    Task<IJupyterExecution> ExecuteAsync(
        JupyterExecuteRequest request,
        CancellationToken cancellationToken = default);

    Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default);
}