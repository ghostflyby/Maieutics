using System.Diagnostics;
using FluentAssertions;
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
            return; // The stamp-polling helper child is a deno eval writing under the temp path.

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

            // The child is alive: the stamp keeps advancing.
            var aliveDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!File.Exists(stampPath))
            {
                if (DateTime.UtcNow >= aliveDeadline)
                    throw new TimeoutException("the helper child never started writing its stamp");
                await Task.Delay(50, deadline.Token);
            }

            cancellation.Cancel();

            // The helper must surface the cancellation promptly (not wait for the child)...
            var runDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            await run.Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
            DateTime.UtcNow.Should().BeBefore(runDeadline, "the helper must cancel within its budget");

            // ...and the child must actually be dead: the stamp stops advancing.
            var dead = false;
            var deathDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deathDeadline && !dead)
            {
                var before = File.GetLastWriteTimeUtc(stampPath);
                await Task.Delay(TimeSpan.FromSeconds(1.5), deadline.Token);
                dead = File.GetLastWriteTimeUtc(stampPath) == before;
            }

            dead.Should().BeTrue("the cancelled helper child must be killed, not orphaned");
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

        run.ExitCode.Should().Be(0);
        run.StandardOutput.Should().Contain("helper-out");
        run.StandardError.Should().Contain("helper-err");
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
