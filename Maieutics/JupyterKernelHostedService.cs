using Maieutics.Configuration;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics;

internal sealed class JupyterKernelHostedService(
    IJupyterKernelApplication application,
    IMaieuticsRuntimeConfiguration configuration,
    IHostApplicationLifetime applicationLifetime,
    IKernelInterruptCoordinator interruptCoordinator,
    ILogger<JupyterKernelHostedService> logger) : BackgroundService
{
    private JupyterKernelHost? kernelHost;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var connectionInfo = await JupyterConnectionInfo.ReadFileAsync(
                configuration.ConnectionFile,
                stoppingToken).ConfigureAwait(false);
            await using var host = await JupyterKernelHost.StartAsync(
                connectionInfo,
                application,
                cancellationToken: stoppingToken).ConfigureAwait(false);
            kernelHost = host;
            interruptCoordinator.SetHost(host);
            logger.LogInformation("Maieutics Jupyter kernel started.");
            await host.Completion.ConfigureAwait(false);
        }
        finally
        {
            kernelHost = null;
            interruptCoordinator.Clear();
            applicationLifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (kernelHost is { } host) await host.StopAsync(cancellationToken).ConfigureAwait(false);

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}