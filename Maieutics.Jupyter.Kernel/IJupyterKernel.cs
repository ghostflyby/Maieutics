namespace Maieutics.Jupyter.Kernel;

public interface IJupyterKernel : IAsyncDisposable
{
    Task Completion { get; }

    Task StopAsync(CancellationToken cancellationToken = default);
}