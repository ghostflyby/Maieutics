using Microsoft.Extensions.Hosting;

namespace Maieutics.Configuration;

internal sealed class MaieuticsRuntimeReadinessHostedService(
    MaieuticsRuntimeConfiguration runtimeConfiguration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        runtimeConfiguration.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}