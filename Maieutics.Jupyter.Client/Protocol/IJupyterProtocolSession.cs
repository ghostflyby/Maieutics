using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client.Protocol;

internal interface IJupyterProtocolSession : IAsyncDisposable
{
    IAsyncEnumerable<JupyterClientEvent> WatchEventsAsync(CancellationToken cancellationToken = default);

    Task<JupyterKernelInfo> GetKernelInfoAsync(CancellationToken cancellationToken = default);

    Task<JupyterKernelInfo> WaitForReadyAsync(CancellationToken cancellationToken = default);

    Task<IJupyterExecution> StartExecutionAsync(
        JupyterExecuteRequest request,
        CancellationToken cancellationToken = default);

    Task<IJupyterExecution> StartExecutionAsync(
        JupyterExecuteRequest request,
        JupyterExecutionOptions options,
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

    Task<JupyterInterruptReply> InterruptAsync(CancellationToken cancellationToken = default);

    Task<JupyterShutdownReply> ShutdownAsync(
        bool restart,
        CancellationToken cancellationToken = default);
}