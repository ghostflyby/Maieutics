using Maieutics.Jupyter.Kernel;

namespace Maieutics;

/// <summary>Routes process signal interrupts to the active kernel host.</summary>
internal interface IKernelInterruptCoordinator
{
    void SetHost(JupyterKernelHost host);

    void Clear();

    void RequestInterrupt();
}

/// <summary>
/// Holds the running kernel host so signal handlers that outlive the host lifetime can request
/// execution interrupts. The host reference is set when the hosted service starts and cleared on
/// shutdown; interrupt requests without a host are no-ops.
/// </summary>
internal sealed class KernelInterruptCoordinator : IKernelInterruptCoordinator
{
    private readonly Lock gate = new();
    private JupyterKernelHost? host;

    public void SetHost(JupyterKernelHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (gate)
        {
            this.host = host;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            host = null;
        }
    }

    public void RequestInterrupt()
    {
        JupyterKernelHost? current;
        lock (gate)
        {
            current = host;
        }

        current?.RequestInterrupt();
    }
}
