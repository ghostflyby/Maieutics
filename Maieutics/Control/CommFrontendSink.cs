using Maieutics.Jupyter.Kernel;

namespace Maieutics.Control;

/// <summary>
///     Holds the kernel's comm output path so the control host can relay comm messages that a REPL
///     child sends toward the frontend. The kernel host is created by <c>JupyterKernelHostedService</c>
///     after the application starts, so this holder is populated at that point.
/// </summary>
internal sealed class CommFrontendSink
{
    private readonly Lock gate = new();
    private Func<JupyterCommMessage, CancellationToken, ValueTask>? sink;

    internal void SetHost(JupyterKernelHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (gate)
        {
            sink = host.SendCommAsync;
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            sink = null;
        }
    }

    internal ValueTask ForwardAsync(
        JupyterCommMessage message,
        CancellationToken cancellationToken)
    {
        Func<JupyterCommMessage, CancellationToken, ValueTask>? current;
        lock (gate)
        {
            current = sink;
        }

        return current is null
            ? ValueTask.CompletedTask
            : current(message, cancellationToken);
    }
}
