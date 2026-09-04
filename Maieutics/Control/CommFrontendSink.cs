using System.Text.Json;
using Maieutics.DenoRepl;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Control;

/// <summary>
///     Holds the kernel's comm output path so the control host can relay comm messages that a
///     REPL child sends toward the frontend. The kernel host is created by
///     <c>JupyterKernelHostedService</c> after the application starts, so this holder is
///     populated at that point. Transitional (ADR 0023): once the Jupyter wiring leaves the
///     executable this holder goes with it; comm messages are neutral here and converted at
///     the Jupyter boundary only.
/// </summary>
internal sealed class CommFrontendSink
{
    private readonly Lock gate = new();
    private Func<ReplCommMessage, CancellationToken, ValueTask>? sink;

    internal void SetHost(JupyterKernelHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (gate)
        {
            sink = (message, cancellationToken) =>
                host.SendCommAsync(ToJupyterMessage(message), cancellationToken);
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
        ReplCommMessage message,
        CancellationToken cancellationToken)
    {
        Func<ReplCommMessage, CancellationToken, ValueTask>? current;
        lock (gate)
        {
            current = sink;
        }

        return current is null
            ? ValueTask.CompletedTask
            : current(message, cancellationToken);
    }

    /// <summary>Rebuilds the Jupyter comm wire envelope the kernel host expects. Transitional:
    /// the synthetic wire identity is exactly what the comm codec used to fabricate.</summary>
    private static JupyterCommMessage ToJupyterMessage(ReplCommMessage message)
    {
        return new JupyterCommMessage(
            (JupyterCommKind)message.Kind,
            message.CommId,
            message.TargetName,
            message.Data,
            message.Metadata,
            message.Buffers,
            JupyterWireMessage.Create(
                new JupyterMessage(
                    JupyterMessageHeader.Create("comm_msg", JupyterSessionIdentity.Create("maieutics")),
                    null,
                    JupyterJson.EmptyObject,
                    message.Data ?? JupyterJson.EmptyObject)));
    }
}
