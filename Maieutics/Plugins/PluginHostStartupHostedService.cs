using Microsoft.Extensions.Hosting;

namespace Maieutics.Plugins;

/// <summary>Creates the out-of-process plugin host with the application and disposes it on shutdown.</summary>
internal sealed class PluginHostStartupHostedService(Task<PluginHostManager> managerTask) : IHostedService
{
    private PluginHostManager? manager;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        manager = await managerTask.ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (manager is { } started)
        {
            await started.DisposeAsync().ConfigureAwait(false);
        }
    }
}
