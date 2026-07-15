namespace Maieutics.Jupyter.Client.Protocol;

public interface IJupyterProtocolSession : IAsyncDisposable
{
    IAsyncEnumerable<KernelEvent> Events { get; }

    Task<KernelInfoReply> GetKernelInfoAsync(CancellationToken cancellationToken = default);

    Task<IJupyterExecution> StartExecutionAsync(
        ExecuteRequest request,
        CancellationToken cancellationToken = default);
}