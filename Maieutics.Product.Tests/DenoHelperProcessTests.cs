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
    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelKillsTheChildWithinTheBudget(bool cancelOwner)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
        await using var area = TempArea.Create("helper-cancel");
        var readyPath = Path.Combine(area.Path, "ready.json");
        var startInfo = new ProcessStartInfo
        {
            FileName = "deno",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("eval");
        // JSON quoting also handles Windows paths. A real timer keeps the child alive
        // until cancellation; publication lets the test acquire its OS exit handle.
        startInfo.ArgumentList.Add(
            "setInterval(() => {}, 1000); await Deno.writeTextFile(" +
            System.Text.Json.JsonSerializer.Serialize(readyPath) +
            ", JSON.stringify({ pid: Deno.pid })); await new Promise(() => {});");
        var run = DenoHelperProcess.RunAsync(startInfo, "to prove kill-on-cancel.", cancellation.Token);
        try
        {
            var ready = await FrontendDiscoveryFile.ReadAsync(
                readyPath, TimeSpan.FromSeconds(30), deadline.Token);
            using var child = Process.GetProcessById(ready.GetProperty("pid").GetInt32());
            Assert.False(child.HasExited);

            // The owner-cancel case exercises abandonment before the test's explicit
            // cancel step, the path which previously left the helper and its slot alive.
            if (cancelOwner) await owner.CancelAsync();
            else await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(deadline.Token));
            await child.WaitForExitAsync(deadline.Token);
            Assert.True(child.HasExited, "helper cancellation must terminate its child");
        }
        finally
        {
            // Teardown remains live after the test's token is canceled. Disposing a CTS
            // alone would not cancel the helper, and its budget slot would remain held.
            await cancellation.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await run.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (run.IsCanceled)
            {
            }
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
}
