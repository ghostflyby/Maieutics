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

    Task<JupyterCompleteReply> CompleteAsync(
        JupyterCompleteRequest request,
        CancellationToken cancellationToken = default);

    Task<JupyterInspectReply> InspectAsync(
        JupyterInspectRequest request,
        CancellationToken cancellationToken = default);

    Task<JupyterIsCompleteReply> IsCompleteAsync(
        JupyterIsCompleteRequest request,
        CancellationToken cancellationToken = default);

    Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default);
}