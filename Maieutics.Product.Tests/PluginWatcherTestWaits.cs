using System.Diagnostics;
using Maieutics.Plugins;
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

/// <summary>
///     Signal-driven virtual-time waits for the plugin watcher's debounced reload: the
///     manager registers its debounce with the injected <see cref="TimeProvider"/>, so a test
///     supplies a <c>FakeTimeProvider</c> and advances past the window instead of waiting it
///     out in real time.
/// </summary>
internal static class PluginWatcherTestWaits
{
    /// <summary>Awaits <paramref name="applied"/> by advancing the fake clock past the
    /// watcher debounce. Each pass awaits one of two facts — the reload applied, or another
    /// debounce registered with the provider (the advance fires it) — and never a sleep: a
    /// file change bursts into several watcher events, each re-arming the debounce, and the
    /// pass loop converges on the event whose debounce wins. A registration that completed
    /// before this pass read its signal is observed as an already-completed task (the source
    /// is swapped, not dropped), so the advance still runs; the watcher chain can win the
    /// race against the test's continuation after the write.
    /// Cancellation surfaces: a canceled <c>WaitAsync</c> completes
    /// synchronously forever, and treating it as "another debounce registered" would turn
    /// the loop into a hot spin that masks the true failure site (this exact spin once
    /// converted a reload-wait timeout into a downstream OCE at ApproveAsync).</summary>
    internal static async Task AwaitReloadAppliedByAdvancingAsync(
        PluginHostManager manager,
        FakeTimeProvider clock,
        Task applied,
        CancellationToken cancellationToken)
    {
        StartHangDumpWatchdog();
        while (!applied.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task registered = manager.WatcherDebounceRegistered;
            Task settled = await Task.WhenAny(
                applied,
                registered.WaitAsync(cancellationToken)).ConfigureAwait(false);
            if (settled == applied) return;

            // Surface cancellation here instead of advancing: a canceled wait is not a
            // registered debounce, and advancing it would start the reload at the very
            // instant the deadline fired — the signature that hid the lost-edge race
            // behind a "runtime will not run timers" theory.
            await settled.ConfigureAwait(false);
            clock.Advance(PluginHostManager.PluginReloadDebounce);
        }
    }

    /// <summary>Flake-hunting diagnosis, opt-in only: when a watched reload parks past
    /// 25s, capture a dump of the test host so the parked stacks name the stall. The
    /// watchdog is enabled solely by MAIEUTICS_HANG_DUMP (the workflow_dispatch
    /// hang_dump input) — never by logging alone, because every dump is hundreds of
    /// megabytes and collection plus `clrstack -all` takes minutes. Best-effort: the
    /// dump and its stack report land in MAIEUTICS_LOG_DIR and are uploaded with the
    /// test logs. Runs once per process.</summary>
    private static void StartHangDumpWatchdog()
    {
        if (Interlocked.Exchange(ref _hangDumpStarted, 1) == 1) return;
        if (Environment.GetEnvironmentVariable("MAIEUTICS_HANG_DUMP") is not "1") return;
        var logDirectory = Environment.GetEnvironmentVariable("MAIEUTICS_LOG_DIR");
        if (string.IsNullOrEmpty(logDirectory)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                var dump = Path.Combine(logDirectory, $"hang-{Environment.ProcessId}.dmp");
                var install = Process.Start(
                    new ProcessStartInfo("dotnet", "tool install --global dotnet-dump")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    });
                using var _ = install;
                await install!.WaitForExitAsync().ConfigureAwait(false);
                var tool = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dotnet", "tools", "dotnet-dump");
                var collect = Process.Start(
                    new ProcessStartInfo(tool, $"collect -p {Environment.ProcessId} -o {dump}")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    });
                using var __ = collect;
                await collect!.WaitForExitAsync().ConfigureAwait(false);
                // Managed stacks cannot be read cross-OS, so the report is produced on
                // the collecting runner and uploaded as text next to the dump.
                var report = Process.Start(
                    new ProcessStartInfo(
                        tool,
                        $"analyze {dump} -c \"clrstack -all\" -c \"exit\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    });
                using var ___ = report;
                var stacks = await report!.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await report.WaitForExitAsync().ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(logDirectory, $"hang-{Environment.ProcessId}.stacks.txt"),
                    stacks).ConfigureAwait(false);
            }
            catch
            {
                // Diagnosis must never fail a test.
            }
        });
    }

    private static int _hangDumpStarted;
}
