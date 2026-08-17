using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoRepl;

/// <summary>
///     Disposes every Deno REPL session when the application host stops. The registry owns the
///     spawned child processes and eval connections, so this service runs before Kestrel's shutdown
///     timeout and lets each session shut down gracefully.
/// </summary>
internal sealed class DenoReplShutdownHostedService(
    DenoReplRegistry registry,
    ILogger<DenoReplShutdownHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("DenoReplShutdownHostedService.StopAsync starting.");
        try
        {
            await registry.DisposeAsync().ConfigureAwait(false);
            logger.LogInformation("DenoReplShutdownHostedService.StopAsync completed.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not dispose Deno REPL sessions during shutdown.");
        }
    }
}
