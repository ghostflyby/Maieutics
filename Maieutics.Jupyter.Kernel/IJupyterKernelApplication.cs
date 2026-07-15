using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Kernel;

public interface IJupyterKernelApplication
{
    JupyterKernelInfo KernelInfo { get; }

    ValueTask<JupyterExecuteResult> ExecuteAsync(
        JupyterExecutionContext context,
        JupyterExecuteRequest request,
        CancellationToken cancellationToken);
}

public sealed record JupyterExecuteResult(string Status = "ok")
{
    public static JupyterExecuteResult Ok { get; } = new();
}

public sealed class JupyterKernelExecutionException(
    string name,
    string message,
    IReadOnlyList<string>? traceback = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Name { get; } = name;

    public IReadOnlyList<string> Traceback { get; } = traceback ?? [];
}