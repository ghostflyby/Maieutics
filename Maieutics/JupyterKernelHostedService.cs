using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maieutics;

public sealed class JupyterKernelHostedService(
    IJupyterKernelApplication application,
    IOptions<MaieuticsOptions> options,
    IHostApplicationLifetime applicationLifetime,
    ILogger<JupyterKernelHostedService> logger) : BackgroundService
{
    private JupyterKernelHost? kernelHost;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var connectionInfo = await JupyterConnectionInfo.ReadFileAsync(
                options.Value.Jupyter.ConnectionFile,
                stoppingToken).ConfigureAwait(false);
            await using var host = await JupyterKernelHost.StartAsync(
                connectionInfo,
                application,
                cancellationToken: stoppingToken).ConfigureAwait(false);
            kernelHost = host;
            logger.LogInformation("Maieutics Jupyter kernel started.");
            await host.Completion.ConfigureAwait(false);
        }
        finally
        {
            kernelHost = null;
            applicationLifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (kernelHost is { } host)
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}