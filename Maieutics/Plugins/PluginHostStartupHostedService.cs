using Microsoft.Extensions.Hosting;

namespace Maieutics.Plugins;

/// <summary>Starts plugin discovery and the out-of-process plugin host with the application.</summary>
internal sealed class PluginHostStartupHostedService(PluginHostManager pluginHostManager) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        pluginHostManager.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
