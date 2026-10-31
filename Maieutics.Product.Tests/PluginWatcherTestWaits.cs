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
        using var watchdogLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchdog = CaptureHangDumpAsync(watchdogLifetime.Token);
        try
        {
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
        finally
        {
            await watchdogLifetime.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
        }
    }

    /// <summary>Opt-in diagnosis owned by one reload wait. Successful waits cancel
    /// the delay; cancellation also kills and reaps any diagnostic child. Only the first
    /// wait that actually reaches the threshold captures a dump.</summary>
    private static async Task CaptureHangDumpAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("MAIEUTICS_HANG_DUMP") is not "1") return;
        var logDirectory = Environment.GetEnvironmentVariable("MAIEUTICS_LOG_DIR");
        if (string.IsNullOrEmpty(logDirectory)) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _hangDumpStarted, 1) == 1) return;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromMinutes(2));
            var dump = Path.Combine(logDirectory, $"hang-{Environment.ProcessId}.dmp");
            // CI installs the tool before tests. Installing from a background test task
            // could block on the network and mutate the user's global tool directory.
            var tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dotnet", "tools", OperatingSystem.IsWindows() ? "dotnet-dump.exe" : "dotnet-dump");
            var collect = new ProcessStartInfo(tool);
            foreach (var argument in new[] { "collect", "-p", Environment.ProcessId.ToString(), "-o", dump })
                collect.ArgumentList.Add(argument);
            await RunDiagnosticAsync(collect, budget.Token).ConfigureAwait(false);
            var report = new ProcessStartInfo(tool);
            foreach (var argument in new[] { "analyze", dump, "-c", "clrstack -all", "-c", "exit" })
                report.ArgumentList.Add(argument);
            var stacks = await RunDiagnosticAsync(report, budget.Token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(logDirectory, $"hang-{Environment.ProcessId}.stacks.txt"),
                stacks, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            TestContext.Current.SendDiagnosticMessage($"Hang diagnostics failed: {exception.GetType().Name}");
        }
    }

    internal static async Task<string> RunDiagnosticAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        await using var child = await TestChildProcess.StartAsync(startInfo, cancellationToken).ConfigureAwait(false);
        return await child.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int _hangDumpStarted;
}
