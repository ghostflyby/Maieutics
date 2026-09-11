using System.Diagnostics;

namespace Maieutics.DenoRepl;

/// <summary>The result of one helper <c>deno</c> child: its exit code plus both drained
/// streams (each bounded by what the child actually wrote before exit).</summary>
internal sealed record DenoHelperRun(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs one short-lived helper <c>deno</c> child (module-graph <c>deno cache</c>,
/// esbuild-wasm <c>deno eval</c>) with kill-on-cancellation semantics. <see cref="Process.Dispose"/>
/// does not stop a running child, so cancelling a session start would otherwise orphan a live
/// <c>deno</c> process; here any abandonment — cancellation first of all — kills the whole
/// process tree before the drains are abandoned and the exception rethrown (the pattern shared
/// with <see cref="DenoModuleGraphWarmer"/>).</summary>
internal static class DenoHelperProcess
{
    /// <summary>Starts the child described by <paramref name="startInfo"/>, drains stdout and
    /// stderr concurrently, and waits for exit. When <paramref name="cancellationToken"/> fires,
    /// the child tree is killed, the drains are observed, and the cancellation is rethrown — the
    /// helper never returns while the child is still running.</summary>
    /// <param name="startInfo">The launch description; stdout and stderr must be redirected.</param>
    /// <param name="purpose">Human-readable purpose used in the start-failure message.</param>
    /// <param name="cancellationToken">Cancels the wait and kills the child tree.</param>
    internal static async Task<DenoHelperRun> RunAsync(
        ProcessStartInfo startInfo,
        string purpose,
        CancellationToken cancellationToken)
    {
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException(
                          $"Could not start '{startInfo.FileName}' {purpose}");
        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var output = await standardOutput.ConfigureAwait(false);
                var error = await standardError.ConfigureAwait(false);
                return new DenoHelperRun(process.ExitCode, output, error);
            }
            catch (Exception)
            {
                // Disposal cannot be trusted to stop the child: kill the whole tree so an
                // abandoned start never leaves a running `deno cache`/`deno eval` behind.
                TryKill(process);
                throw;
            }
            finally
            {
                // A cancelled wait abandons the drains; once the kill closes the pipes they
                // finish (or fault) — observe faults so nothing is left unobserved.
                ObserveIfPending(standardOutput);
                ObserveIfPending(standardError);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // The child already exited or the OS refused the kill; nothing further to do.
        }
    }

    private static void ObserveIfPending(Task<string> drain)
    {
        if (drain.IsCompleted)
        {
            if (drain.IsFaulted) _ = drain.Exception;
            return;
        }

        _ = drain.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
