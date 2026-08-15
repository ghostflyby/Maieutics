using Maieutics.Plugins;
using Microsoft.Extensions.Hosting;

namespace Maieutics.Configuration;

internal sealed class MaieuticsRuntimeReadinessHostedService(
    MaieuticsRuntimeConfiguration runtimeConfiguration,
    PluginHostManager pluginHosts) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return runtimeConfiguration.InitializeAsync(pluginHosts, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
