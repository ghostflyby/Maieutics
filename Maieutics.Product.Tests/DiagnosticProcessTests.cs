using System.Diagnostics;
using System.Text.Json;

namespace Maieutics.Product.Tests;

public sealed class DiagnosticProcessTests
{
    [Fact(Timeout = 30_000)]
    public async Task DiagnosticDrainsBothPipesBeforeWaitingForExit()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo("deno");
        start.ArgumentList.Add("eval");
        start.ArgumentList.Add("console.log('o'.repeat(512 * 1024)); console.error('e'.repeat(512 * 1024));");

        var output = await PluginWatcherTestWaits.RunDiagnosticAsync(start, deadline.Token);

        Assert.Equal(512 * 1024, output.Count(character => character == 'o'));
        Assert.Equal(512 * 1024, output.Count(character => character == 'e'));
    }

    [Fact(Timeout = 30_000)]
    public async Task AbandoningDiagnosticsKillsAndReapsTheChild()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var area = TempArea.Create("diagnostic-cancel");
        var readyPath = Path.Combine(area.Path, "ready.json");
        var start = new ProcessStartInfo("deno");
        start.ArgumentList.Add("eval");
        start.ArgumentList.Add("setInterval(() => {}, 1000); await Deno.writeTextFile(" +
                               JsonSerializer.Serialize(readyPath) + ", JSON.stringify({ pid: Deno.pid }));");
        var diagnostic = PluginWatcherTestWaits.RunDiagnosticAsync(start, cancellation.Token);
        try
        {
            var ready = await FrontendDiscoveryFile.ReadAsync(readyPath, TimeSpan.FromSeconds(10), deadline.Token);
            using var child = Process.GetProcessById(ready.GetProperty("pid").GetInt32());
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnostic.WaitAsync(deadline.Token));
            Assert.True(child.HasExited, "diagnostic cancellation must reap its owned child before returning");
        }
        finally
        {
            await cancellation.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await diagnostic.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (diagnostic.IsCanceled)
            {
            }
        }
    }
}
