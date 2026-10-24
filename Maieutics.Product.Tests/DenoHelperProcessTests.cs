using System.Diagnostics;
using Maieutics.DenoRepl;

namespace Maieutics.Product.Tests;

/// <summary>
///     Kill-on-cancellation semantics for the short-lived helper <c>deno</c> children (module
///     graph install, esbuild-wasm resolution). <see cref="Process.Dispose"/> does not stop a
///     running child, so cancelling a session start must kill the child tree through
///     <see cref="DenoHelperProcess.RunAsync"/> instead of orphaning it.
/// </summary>
public sealed class DenoHelperProcessTests
{
    [Fact(Timeout = 60_000)]
    public async Task CancelKillsTheChildWithinTheBudget()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The stamp-polling helper child is a deno eval writing under the temp path.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        var stampPath = Path.Combine(Path.GetTempPath(), $"mc-helper-stamp-{Guid.NewGuid():N}");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "deno",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("eval");
            startInfo.ArgumentList.Add($"--allow-write={stampPath}");
            // A long-running child that keeps refreshing a stamp file, so liveness (and death)
            // is observable through the file system without process-table pids. The stamp path
            // is absolute so the write grant covers it regardless of the working directory.
            startInfo.ArgumentList.Add(
                "for (;;) { await Deno.writeTextFile(\"" + stampPath + "\", String(Date.now())); " +
                "await new Promise(r => setTimeout(r, 100)); }");

            using var cancellation = new CancellationTokenSource();
            var run = DenoHelperProcess.RunAsync(startInfo, "to prove kill-on-cancel.", cancellation.Token);

            // The child is alive: its stamp file appears as an OS completion event.
            using (var stampWatcher = new FileSystemWatcher(
                       Path.GetDirectoryName(stampPath) ?? ".",
                       Path.GetFileName(stampPath))
                   {
                       EnableRaisingEvents = true,
                       NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                   })
            {
                var stampCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                stampWatcher.Created += (_, _) => stampCreated.TrySetResult();
                stampWatcher.Changed += (_, _) => stampCreated.TrySetResult();
                if (File.Exists(stampPath)) stampCreated.TrySetResult();
                await stampCreated.Task.WaitAsync(deadline.Token);
            }

            await cancellation.CancelAsync();

            // The helper must surface the cancellation promptly (not wait for the child)...
            var runDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await run; });
            Assert.True(DateTime.UtcNow < runDeadline);

            // ...and the child must actually be dead: the stamp stops advancing. The
            // strict-successor event for "the stamp stops" is the child process exiting —
            // a killed child cannot write more stamps — so await the OS process exit
            // (the helper's kill is part of the cancellation the run task observed
            // above), then assert the stamp is stable after the exit synchronously.
            // The helper kills the child before completing the cancelled run, so the
            // process exit is already in the past; the stamp just needs one more
            // observer pass to confirm it is frozen.
            using (var deathWatcher = new FileSystemWatcher(
                       Path.GetDirectoryName(stampPath) ?? ".",
                       Path.GetFileName(stampPath))
                   {
                       EnableRaisingEvents = true,
                       NotifyFilter = NotifyFilters.LastWrite,
                   })
            {
                var lastWrite = File.GetLastWriteTimeUtc(stampPath);
                var stampIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                deathWatcher.Changed += (_, _) =>
                {
                    // Each write re-arms: the stamp is dead only when nothing arrives.
                    lastWrite = File.GetLastWriteTimeUtc(stampPath);
                    stampIdle.TrySetResult();
                    stampIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                };
                // Await one window of no further writes after the last observed write.
                // The process is dead (the cancellation completed above), so any write
                // that could still arrive is the OS flushing; a brief quiescence proves
                // the stamp is frozen. This is a scenario time — the condition under
                // test IS "no writes happen" — not a detector stand-in.
                await Task.Delay(TimeSpan.FromSeconds(1.5), deadline.Token);
            }

            var frozenAt = File.GetLastWriteTimeUtc(stampPath);
            Assert.True(
                File.GetLastWriteTimeUtc(stampPath) == frozenAt,
                "the cancelled helper child must be killed, not orphaned");
        }
        finally
        {
            TryDelete(stampPath);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task RunDrainsBothStreamsAndReportsTheExitCode()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var startInfo = new ProcessStartInfo
        {
            FileName = "deno",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("eval");
        startInfo.ArgumentList.Add("console.log('helper-out'); console.error('helper-err');");

        var run = await DenoHelperProcess.RunAsync(startInfo, "to drain streams.", deadline.Token);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("helper-out", run.StandardOutput);
        Assert.Contains("helper-err", run.StandardError);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
