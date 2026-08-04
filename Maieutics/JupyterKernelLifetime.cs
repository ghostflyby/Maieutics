using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics;

/// <summary>
/// Replaces the default console lifetime so SIGINT interrupts the currently executing kernel
/// request instead of shutting the application down, while SIGQUIT and SIGTERM keep the standard
/// graceful shutdown behavior.
/// </summary>
internal sealed class JupyterKernelLifetime(
    IHostApplicationLifetime applicationLifetime,
    IKernelInterruptCoordinator coordinator,
    ILogger<JupyterKernelLifetime> logger)
    : IHostLifetime, IDisposable
{
    private readonly IHostApplicationLifetime applicationLifetime = applicationLifetime
                                                                    ?? throw new ArgumentNullException(
                                                                        nameof(applicationLifetime));

    private readonly IKernelInterruptCoordinator coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    private readonly Lock gate = new();
    private PosixSignalRegistration? sigintRegistration;
    private PosixSignalRegistration? sigquitRegistration;
    private PosixSignalRegistration? sigtermRegistration;
    private int disposed;

    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (gate)
        {
            if (sigintRegistration is null)
            {
                sigintRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, HandlePosixSignal);
                sigquitRegistration = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, HandlePosixSignal);
                sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandlePosixSignal);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unregister();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Unregister();
    }

    internal void HandleSignal(PosixSignal signal)
    {
        switch (signal)
        {
            case PosixSignal.SIGINT:
                logger.LogDebug("SIGINT received; interrupting the current execution.");
                coordinator.RequestInterrupt();
                break;
            case PosixSignal.SIGQUIT:
            case PosixSignal.SIGTERM:
                logger.LogDebug("{Signal} received; stopping the application.", signal);
                applicationLifetime.StopApplication();
                break;
        }
    }

    private void HandlePosixSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        HandleSignal(context.Signal);
    }

    private void Unregister()
    {
        PosixSignalRegistration? sigint;
        PosixSignalRegistration? sigquit;
        PosixSignalRegistration? sigterm;
        lock (gate)
        {
            sigint = sigintRegistration;
            sigquit = sigquitRegistration;
            sigterm = sigtermRegistration;
            sigintRegistration = null;
            sigquitRegistration = null;
            sigtermRegistration = null;
        }

        sigint?.Dispose();
        sigquit?.Dispose();
        sigterm?.Dispose();
    }
}