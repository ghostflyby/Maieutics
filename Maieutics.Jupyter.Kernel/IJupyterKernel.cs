using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Kernel;

public interface IJupyterKernel : IAsyncDisposable
{
    Task Completion { get; }

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sends a comm message to the frontend over the iopub channel. Used to relay comm traffic
    ///     that originates outside an execute request (for example from a REPL child process).
    /// </summary>
    ValueTask SendCommAsync(JupyterCommMessage message, CancellationToken cancellationToken = default);
}