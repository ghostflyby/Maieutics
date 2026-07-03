using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Kernel;

public interface IJupyterKernel : IAsyncDisposable
{
    JupyterConnectionInfo ConnectionInfo { get; }

    Task RunAsync(CancellationToken cancellationToken = default);
}