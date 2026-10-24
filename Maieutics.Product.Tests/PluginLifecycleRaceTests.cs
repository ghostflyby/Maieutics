using System.Text.Json;
using Maieutics.Control;
using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

/// <summary>
///     Lifecycle races of the plugin host manager (review F5). A call that belongs to a retired
///     host connection generation must never be delivered to the generation that replaced it,
///     a call that races the stop of its generation's lifetime source must stay a typed,
///     recoverable failure instead of surfacing an <see cref="ObjectDisposedException"/> out of
///     the invoke path, and a dispose that races a plugin-set restart must join the restart
///     before returning so nothing lives or attaches behind it. The interleavings are driven
///     deterministically through the simulated-host seam
///     (<see cref="PluginHostInvokeTests.CreateHarnessAsync"/> +
///     <see cref="PluginHostManager.HandleHostMessage"/> + <see cref="PluginHostManager.AttachHostAsync"/>)
///     with the fake deno executable, so no real deno host process runs; the dispose-restart
///     interleaving additionally arms the manager's real watcher and triggers it through the
///     plugin-set file-change seam.
/// </summary>
public sealed class PluginLifecycleRaceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact(Timeout = 90_000)]
    public async Task ARetiredGenerationsCapabilityReplyIsNotDeliveredToTheReplacementHost()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(deadline.Token);

        harness.Manager.SetCapabilityGrants("plugin-1", ["tools.invoke"]);
        var staleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStale = new TaskCompletionSource<JsonElement>(TaskCreationOptions.None);
        harness.Manager.CapabilityExecutor = (_, arguments, _) =>
        {
            // Only the retired call parks; every later call answers immediately so the
            // replacement host's frame stream stays observable.
            if (arguments.TryGetProperty("marker", out var marker) &&
                marker.ValueKind == JsonValueKind.String &&
                marker.GetString() == "stale")
            {
                staleEntered.TrySetResult();
                return releaseStale.Task;
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { status = "ok" }));
        };

        // The capability call captures the live connection under the state gate before the
        // executor runs, so its reply belongs to this connection generation.
        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"capability.invoke\",\"correlationId\":\"retired-1\"," +
            "\"payload\":{\"pluginId\":\"plugin-1\",\"capability\":\"tools.invoke\"," +
            "\"payload\":{\"tool\":\"workspace_list\",\"arguments\":{\"marker\":\"stale\"}}}}");
        await staleEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), deadline.Token);

        // The connection that issued the call is retired and a replacement takes the outbound
        // slot while the call is still in flight — the same connection swap a plugin-set
        // restart produces when the retired host detaches and its successor attaches.
        var released = harness.Manager.HostConnectionReleased;
        harness.Host.Dispose();
        await released.WaitAsync(TimeSpan.FromSeconds(30), deadline.Token);
        var replacement = new TestWebSocket();
        var attached = harness.Manager.HostConnectionAttached;
        var replacementAttach = harness.Manager.AttachHostAsync(replacement, deadline.Token);
        await attached.WaitAsync(TimeSpan.FromSeconds(30), deadline.Token);

        // The replacement host's own call: its reply must be delivered, proving the new
        // connection is live and its outbound channel ordered.
        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"capability.invoke\",\"correlationId\":\"replacement-1\"," +
            "\"payload\":{\"pluginId\":\"plugin-1\",\"capability\":\"tools.invoke\"," +
            "\"payload\":{\"tool\":\"workspace_list\",\"arguments\":{\"marker\":\"fresh\"}}}}");
        var freshReply = await replacement.ReadSentAsync(deadline.Token);
        Assert.Contains("replacement-1", freshReply);

        // The retired call completes now; its reply was captured for the retired connection.
        // Before the fix the reply is written into the replacement's outbound channel after the
        // fresh call's reply and delivered to this socket, so the second read below fails the
        // test; after the fix the push is refused and the socket receives nothing more.
        releaseStale.TrySetResult(JsonSerializer.SerializeToElement(new { status = "ok" }));

        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        quiet.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var retired = await replacement.ReadSentAsync(quiet.Token);
            Assert.Fail(
                $"the replacement host received a frame for the retired generation: {retired}");
        }
        catch (OperationCanceledException) when (quiet.IsCancellationRequested)
        {
            // The retired generation's reply was refused: the replacement host received
            // nothing for it.
        }

        replacement.Dispose();
        await replacementAttach.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnInvokeRacingADisposedGenerationLifetimeIsATypedUnavailableOutcome()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(deadline.Token);

        // StopCoreAsync ends by disposing the generation's lifetime source. A call that races
        // that window — e.g. a tool describe pass or an MCP lease delivery dispatched right
        // before the stop — used to build its linked budget from the disposed source and
        // escape the invoke path with a raw ObjectDisposedException instead of a typed,
        // recoverable failure (AGENTS.md invariant 18).
        await harness.Manager.DisposeAsync();

        var outcome = await harness.Manager.InvokeExtensionPointAsync(
            "plugin-1",
            "./main",
            ReplExtensionPointName.ToolPreInvoke,
            request: null,
            deadline.Token);

        Assert.True(outcome.IsError, outcome.Message);
        Assert.Equal("plugin_unavailable", outcome.Code);
    }

    /// <summary>Acceptance clause of review F5: across dispose×restart interleavings, no host
    /// generation survives DisposeAsync. The deterministic interleaving here is dispose during
    /// a plugin-set restart whose successor generation is mid-startup. The fixed DisposeAsync
    /// captures the in-flight restart under the lifecycle gate and returns only after it
    /// settles, so the lifecycle state is final at return and the accept path is dead. The
    /// unfixed DisposeAsync returns as soon as the stop completes while the restart still
    /// flies; the restart's last lifecycle writes then land after the return, which the
    /// finality read below observes. A literally surviving deno host is not reachable through
    /// the unfixed guards (RestartCoreAsync aborts on the externally-stopped flag and
    /// StartCoreAsync re-checks stopping under the lifecycle lock), so what this test pins is
    /// the join semantics itself.</summary>
    [Fact(Timeout = 90_000)]
    public async Task DisposeAsyncJoinsAnInFlightPluginSetRestartAndLeavesNoLiveGenerationBehind()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var pluginsRoot = Path.Combine(Path.GetTempPath(), $"mc-host-restart-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginsRoot);
        // The local plugin set the watcher diffs against is the plugins root's import-wired
        // set: the root deno.json imports that resolve to directories carrying a loadable
        // maieutics.json (PluginManifest.ReadLocalImportTargets). Start from an empty import
        // map so the started generation's set is empty and the wiring below is a set change.
        WriteLocalImports(pluginsRoot, []);
        try
        {
            await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(pluginsRoot, deadline.Token);

            // A watched change that rewires local plugins cannot be applied in-process (the
            // new module entries belong to the process import map), so the manager restarts
            // the host generation. The filler plugins widen the successor generation's
            // discovery scan so the dispose below reliably lands while that successor is
            // still starting up; the import-map rewrite at the end of this block adds the
            // whole set past the started generation, which trips that restart.
            const int fillerCount = 24;
            for (var index = 0; index < fillerCount; index++)
            {
                var filler = Path.Combine(pluginsRoot, $"filler-{index:D2}");
                Directory.CreateDirectory(filler);
                File.WriteAllText(
                    Path.Combine(filler, "maieutics.json"),
                    """{ "capabilities": ["tools.invoke"] }""");
            }

            var restarted = harness.Manager.RegistryChangesReplaced;
            var second = Path.Combine(pluginsRoot, "second-plugin");
            Directory.CreateDirectory(second);
            File.WriteAllText(
                Path.Combine(second, "maieutics.json"),
                """{ "capabilities": ["tools.invoke"] }""");

            // Wire the new directories into the root import map: the watched deno.json change
            // is the plugin-set seam. A bare maieutics.json drop-in is invisible to the set
            // scan (the scan counts only the import-wired roots) and would never restart the
            // host, leaving the replacement signal below permanently pending.
            WriteLocalImports(
                pluginsRoot,
                [.. Enumerable.Range(0, fillerCount).Select(index => $"filler-{index:D2}"), "second-plugin"]);

            // The replacement signal is completed in the same locked transition that resets the
            // lifecycle state and starts the successor generation, so awaiting it proves the
            // restart passed its externally-stopped guard and its successor is mid-startup —
            // the deterministic restart-in-progress point this test disposes from.
            await restarted.WaitAsync(TimeSpan.FromSeconds(30), deadline.Token);

            await harness.Manager.DisposeAsync();

            var nextAttach = harness.Manager.HostConnectionAttached;

            // Whatever state the interleaving produced must be terminal: no live generation.
            var stateAtReturn = harness.Manager.GetStatus().State;
            Assert.True(
                stateAtReturn is PluginHostState.Stopped or PluginHostState.Canceled or PluginHostState.Failed,
                $"the manager reported a live generation ({stateAtReturn}) after DisposeAsync returned.");

            // And final: the observable trace of a restart the dispose failed to join is a late
            // lifecycle write — the successor generation's startup faults against the stopped
            // manager and the reported state moves after the return. On the unfixed build this
            // read observes that move; the join makes it impossible by construction.
            await Task.Delay(TimeSpan.FromSeconds(1.5), deadline.Token);
            Assert.Equal(stateAtReturn, harness.Manager.GetStatus().State);

            // The accept path died with the last generation: the readiness source the racing
            // successor's startup unwound into is terminal (canceled, or faulted when that
            // startup hit the stop's disposal check), so an attach faults instead of being
            // installed; if the successor had already completed its startup before the
            // dispose landed, readiness is completed but the previous generation's connection
            // slot is still held and the attach is refused instead. Either way nothing
            // attaches, which the untouched accept signal below pins.
            using var probe = new TestWebSocket();
            try
            {
                await harness.Manager.AttachHostAsync(probe, deadline.Token);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException)
            {
                // The attach faulted against the terminal lifecycle readiness.
            }

            Assert.False(
                nextAttach.IsCompleted,
                "a host connection was accepted after DisposeAsync returned.");
        }
        finally
        {
            if (Directory.Exists(pluginsRoot)) await TempArea.DeleteAsync(pluginsRoot, CancellationToken.None);
        }
    }

    /// <summary>Writes the plugins root's deno.json import map with one local entry per
    /// plugin directory — the watched plugin-set seam. The target points at a file inside
    /// the plugin directory because the set scan resolves each import target to its
    /// containing directory (<c>Path.GetDirectoryName</c>).</summary>
    private static void WriteLocalImports(string pluginsRoot, IReadOnlyList<string> pluginDirectories)
    {
        var imports = string.Join(
            ",\n    ",
            pluginDirectories.Select(directory =>
                JsonSerializer.Serialize($"@local/{directory}/main") + ": " +
                JsonSerializer.Serialize($"./{directory}/maieutics.json")));
        File.WriteAllText(
            Path.Combine(pluginsRoot, "deno.json"),
            "{\n  \"imports\": {\n    " + imports + "\n  }\n}");
    }
}
