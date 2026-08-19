using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoRepl;

/// <summary>
///     Warms the Deno REPL module graph at process startup so the first REPL session does not pay
///     the full network fetch of the jsr.io module graph inside its startup timeout (a cold
///     <c>DENO_DIR</c> could exceed it; ADR 0018 resolved decision 4). Runs in the background and
///     never fails startup: a failed warm is logged and the REPL session falls back to the existing
///     on-demand install path.
/// </summary>
internal sealed class DenoModuleGraphWarmer(
    DenoReplOptions options,
    DenoReplModule modules,
    ILogger<DenoModuleGraphWarmer> logger) : IHostedService
{
    private readonly CancellationTokenSource lifetime = new();
    private Task? warming;

    /// <summary>Completes when the background warm finishes (success or failure). Test seam; the
    /// host never waits on it.</summary>
    internal Task? WarmCompletion => warming;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        warming = WarmAsync();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifetime.CancelAsync();
        if (warming is not null)
        {
            try
            {
                await warming.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when shutdown cancels the warm.
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Deno REPL module-graph warm was cancelled or failed during shutdown.");
            }
        }
    }

    private async Task WarmAsync()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = options.Executable,
                WorkingDirectory = modules.ModuleDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("cache");
            startInfo.ArgumentList.Add($"--config={modules.ConfigFile}");
            startInfo.ArgumentList.Add($"--lock={modules.LockFile}");
            startInfo.ArgumentList.Add(modules.MainUrl);

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException(
                                    $"Could not start '{options.Executable}' to warm the Deno REPL module graph.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(lifetime.Token);
            var standardError = process.StandardError.ReadToEndAsync(lifetime.Token);
            await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            _ = await standardOutput.ConfigureAwait(false);
            if (process.ExitCode != 0)
                logger.LogDebug(
                    "Deno REPL module-graph warm failed with exit code {ExitCode}: {Error}",
                    process.ExitCode,
                    error.Trim());
            else
                logger.LogInformation("Deno REPL module graph warmed successfully.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // A failed warm must not take down the host; the REPL session re-installs on demand.
            logger.LogDebug(exception, "Deno REPL module-graph warm failed; the REPL will install on demand.");
        }
    }
}
