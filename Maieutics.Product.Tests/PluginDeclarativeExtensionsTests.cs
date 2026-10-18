using Maieutics.Control;
using Maieutics.DenoRepl;
using Maieutics.Mcp;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Maieutics.Plugins.Contributions;

namespace Maieutics.Product.Tests;

/// <summary>
///     Declarative (manifest-only) extensions: a plugin with zero entrypoints but an
///     `extensions` section contributes to the kernel without spawning any worker. The
///     kernel parses the manifest, exposes the entries as synthetic registrations, and
///     serves discovery straight from the manifest snapshot.
/// </summary>
public sealed class PluginDeclarativeExtensionsTests
{
    // Headroom over the watcher→debounce→reload chain's real-time tail under the
    // nightly's three-pass parallel load (a 20 s budget expired twice there), while
    // still firing inside the 60 s Fact timeout so the failure reports at the true
    // wait instead of the outer kill.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(50);

    [Fact(Timeout = 60_000)]
    public async Task DeclarativeOnlyPluginsPublishSyntheticRegistrations()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateDeclarativePluginsRoot("declarative");
        var pluginName = Path.GetFileName(root);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            // The synthetic registration exists without any worker ever spawning:
            // the manifest snapshot is the discovery source.
            var registrations = manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind);
            var registration = Assert.Single(registrations);
            Assert.Equal(pluginName, registration.PluginId);
            Assert.Equal(PluginHostManager.ManifestExportName, registration.ExportName);

            var discovery = manager.DiscoverManifestMcpAsync(registration);
            Assert.True(discovery.IsSuccess, discovery.Failure);
            Assert.Equal($"plugin:{pluginName}::npm:@maieutics/probe-server", Assert.Single(discovery.Definitions).Id);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task UndeclaredCapabilityStyleEntriesAndUnknownKindsStayInert()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateDeclarativePluginsRoot("declarative-unknown", includeUnknownKind: true);
        var pluginName = Path.GetFileName(root);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            // The known kind is registered; the unknown kind produced a diagnostic
            // and no registration of its own.
            var registrations = manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind);
            Assert.Equal(pluginName, Assert.Single(registrations).PluginId);
            Assert.Empty(manager.GetRegistrations("FutureKind"));
            var discovery = manager.DiscoverManifestMcpAsync(
                new PluginRegistration(pluginName, PluginHostManager.ManifestExportName, McpDiscoverContributionKind.ExtensionKind));
            Assert.True(discovery.IsSuccess);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AnOutOfRootImportTargetDoesNotEscalateContentEditsToRestarts()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        // A dependency plugin directory OUTSIDE the plugins root, reachable through a
        // relative file import: loadable, so the set scan used to count it — but the
        // loaded descriptor set is root-contained, so the difference was permanently
        // true and every watched change (this test's pure content edit included)
        // escalated to a generation restart that re-detected itself.
        var outside = Path.Combine(Path.GetTempPath(), $"mc-import-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(
            Path.Combine(outside, "deno.json"),
            """
            {
              "name": "@dep/shared",
              "version": "0.1.0"
            }
            """);
        File.WriteAllText(Path.Combine(outside, "maieutics.json"), "{}");

        var root = Path.Combine(Path.GetTempPath(), $"mc-import-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var outsideReference = Path.GetRelativePath(root, Path.Combine(outside, "mod.ts"));
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            $$"""
            {
              "name": "@maieutics/import-root",
              "version": "0.1.0",
              "imports": { "@dep/shared": "{{outsideReference.Replace("\\", "\\\\")}}" }
            }
            """);
        File.WriteAllText(Path.Combine(root, "maieutics.json"), "{}");

        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            // A pure content edit inside the root must take the in-process reload path:
            // reaching the reload-applied signal proves no set-mismatch restart fired.
            var applied = CaptureReloadApplied(manager);
            File.WriteAllText(Path.Combine(root, "maieutics.json"), "{}\n");
            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, applied, deadline.Token);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclarativeSectionRemovalTakesEffectOnReload()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateDeclarativePluginsRoot("declarative-reload");
        var pluginId = Path.GetFileName(root);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));

            // Remove the section from the manifest and apply the reload: the synthetic
            // registration and the contribution must both disappear.
            var applied = CaptureReloadApplied(manager);
            File.WriteAllText(
                Path.Combine(root, "maieutics.json"),
                """
                {
                  "capabilities": ["tools.invoke"]
                }
                """);
            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, applied, deadline.Token);

            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            var discovery = manager.DiscoverManifestMcpAsync(
                new PluginRegistration(pluginId, PluginHostManager.ManifestExportName, McpDiscoverContributionKind.ExtensionKind));
            Assert.False(discovery.IsSuccess, discovery.Failure);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclarativeSectionAdditionTakesEffectOnReload()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = Path.Combine(Path.GetTempPath(), $"declarative-add-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/declarative-add",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "capabilities": ["tools.invoke"]
            }
            """);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));

            // Add the section and apply the reload (a workerless plugin never
            // triggers a host registry resend, so the manager republishes itself).
            var applied = CaptureReloadApplied(manager);
            File.WriteAllText(
                Path.Combine(root, "maieutics.json"),
                """
                {
                  "capabilities": ["tools.invoke"],
                  "extensions": {
                    "McpDiscover": [
                      { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } }
                    ]
                  }
                }
                """);
            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, applied, deadline.Token);

            // The added extensions section changed the declaration surface, so the
            // approval gate holds the contribution until the user approves it (ADR 0037);
            // approval then activates it without a restart.
            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            await manager.ApproveAsync(Path.GetFileName(root), deadline.Token);

            var registrations = manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind);
            var registration = Assert.Single(registrations);
            Assert.Equal(Path.GetFileName(root), registration.PluginId);
            var discovery = manager.DiscoverManifestMcpAsync(registration);
            Assert.True(discovery.IsSuccess, discovery.Failure);
            Assert.Equal($"plugin:{Path.GetFileName(root)}::npm:@maieutics/probe-server", Assert.Single(discovery.Definitions).Id);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task McpDataFilePluginsPublishSyntheticRegistrations()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        // A pure mcp.json data file (ADR 0033): no manifest extensions section, no
        // entrypoints — the data file alone contributes through the same synthetic
        // registration and discovery path the manifest section uses.
        var root = CreateMcpDataFilePluginsRoot("declarative-data");
        var pluginId = Path.GetFileName(root);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            var registration = Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            Assert.Equal(pluginId, registration.PluginId);
            Assert.Equal(PluginHostManager.ManifestExportName, registration.ExportName);

            var discovery = manager.DiscoverManifestMcpAsync(registration);
            Assert.True(discovery.IsSuccess, discovery.Failure);
            var definition = Assert.Single(discovery.Definitions);
            Assert.Equal($"plugin:{pluginId}::probe", definition.Id);
            Assert.IsType<StdioMcpTransportDefinition>(definition.Transport);
            Assert.True(definition.RootsEnabled);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task McpDataFileEditsReplaceTheContributionOnReload()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateMcpDataFilePluginsRoot("declarative-data-edit");
        var pluginId = Path.GetFileName(root);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            var registration = new PluginRegistration(
                pluginId,
                PluginHostManager.ManifestExportName,
                McpDiscoverContributionKind.ExtensionKind);
            var original = manager.DiscoverManifestMcpAsync(registration);
            Assert.True(original.IsSuccess, original.Failure);

            // Rewriting the data file with a different transport is a declarative-only
            // change, but it changes the declaration surface: the approval gate holds
            // the contribution until the user approves the new servers (ADR 0037).
            // Approval then serves them with the new generation key.
            var applied = CaptureReloadApplied(manager);
            WriteMcpDataFile(
                root,
                """
                {
                  "mcpServers": {
                    "probe": { "type": "http", "url": "https://example.test/mcp" }
                  }
                }
                """);
            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, applied, deadline.Token);
            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            await manager.ApproveAsync(pluginId, deadline.Token);

            var reloaded = manager.DiscoverManifestMcpAsync(registration);
            Assert.True(reloaded.IsSuccess, reloaded.Failure);
            var definition = Assert.Single(reloaded.Definitions);
            Assert.IsType<HttpMcpTransportDefinition>(definition.Transport);
            Assert.NotEqual(original.Definitions[0].GenerationKey, definition.GenerationKey);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AnInvalidMcpDataFileEditKeepsThePreviousContribution()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateMcpDataFilePluginsRoot("declarative-data-broken");
        var pluginId = Path.GetFileName(root);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            var registration = new PluginRegistration(
                pluginId,
                PluginHostManager.ManifestExportName,
                McpDiscoverContributionKind.ExtensionKind);
            var original = manager.DiscoverManifestMcpAsync(registration);
            Assert.True(original.IsSuccess, original.Failure);

            // The broken rewrite is a declaration-surface change the approval gate treats
            // fail-closed (ADR 0037): the contribution is withdrawn and the plugin goes
            // pending. Repairing the file restores the EXACT approved fingerprint, so the
            // still-stored record re-matches and the contribution returns without a new
            // approval — the repair form of sticky-last-good.
            var applied = CaptureReloadApplied(manager);
            WriteMcpDataFile(root, "{");
            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, applied, deadline.Token);

            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            Assert.Equal(PluginApprovalState.PendingApproval, Assert.Single(manager.ListPluginApprovals()).State);

            var repaired = CaptureReloadApplied(manager);
            WriteMcpDataFile(
                root,
                """
                {
                  "mcpServers": {
                    "probe": { "command": "deno", "args": ["info"] }
                  }
                }
                """);
            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, repaired, deadline.Token);
            Assert.Equal(pluginId, Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind)).PluginId);
            var recovered = manager.DiscoverManifestMcpAsync(registration);
            Assert.True(recovered.IsSuccess, recovered.Failure);
            Assert.Equal(original.Definitions[0].GenerationKey, recovered.Definitions[0].GenerationKey);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Captures the reload-applied signal so the awaited task observes exactly the
    /// automatic reload triggered by the watched change written right after the capture.</summary>
    private static Task CaptureReloadApplied(PluginHostManager manager)
    {
        return manager.ReloadApplied;
    }

    /// <summary>The manager construction every test in this suite shares, seeded with
    /// approvals for the current declarations (ADR 0037): these tests assert what an
    /// APPROVED declarative plugin does, so they approve the boot state through the real
    /// approvals file. The fake clock owns the watcher debounce: tests that change watched
    /// files advance it past <see cref="PluginHostManager.PluginReloadDebounce"/> through
    /// <see cref="PluginWatcherTestWaits"/> instead of waiting the window out in real time.</summary>
    /// <summary>Deterministic regression for the nightly failures behind
    /// McpDataFileEditsReplaceTheContributionOnReload (an OperationCanceledException at
    /// ApproveAsync after the reload wait silently ate the whole budget): the wait now
    /// surfaces cancellation at its own line instead of hot-spinning, and the budget
    /// carries enough headroom for the watcher chain's real-time tail under load.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ReloadWaitSurfacesCancellationAtTheWait()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateMcpDataFilePluginsRoot("declarative-wait-cancel");
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);
        try
        {
            using var lifecycle = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            await manager.StartAsync(lifecycle.Token);
            await manager.WaitUntilReadyAsync(lifecycle.Token);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var applied = manager.ReloadApplied;
            Func<Task> wait = async () => await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager,
                clock,
                applied,
                canceled.Token);
            Assert.NotNull((await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await wait.Invoke(); })));
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Deterministic regression for the latent signal skip on the restart path:
    /// a watched edit that adds a local plugin (a set change) settles through a host
    /// restart, and the ReloadApplied signal completes there — before the fix a waiter
    /// watching this edit parked forever.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ASetChangeCompletesTheReloadAppliedSignalThroughTheRestart()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateMcpDataFilePluginsRoot("declarative-set-change");
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            var applied = CaptureReloadApplied(manager);
            Directory.CreateDirectory(Path.Combine(root, "second-plugin"));
            File.WriteAllText(
                Path.Combine(root, "second-plugin", "maieutics.json"),
                """
                {
                  "capabilities": ["tools.invoke"]
                }
                """);

            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, applied, deadline.Token);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static PluginHostManager CreateManager(string root, FakeTimeProvider clock)
    {
        return new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = CreateFakeDenoExecutable() },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            clock,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(root));
    }

    /// <summary>A workerless plugin project that declares MCP servers purely through an
    /// <c>mcp.json</c> data file (ADR 0033).</summary>
    private static string CreateMcpDataFilePluginsRoot(string pluginName)
    {
        var root = Path.Combine(Path.GetTempPath(), pluginName);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/mcp-data",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        // The data file is declared as a string-valued entrypoint (ADR 0033): the name
        // and location are the plugin's choice — here a subdirectory and a custom name.
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "capabilities": ["tools.invoke"],
              "entrypoints": { "mcp": "config/servers.json" }
            }
            """);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(
            Path.Combine(root, "config", "servers.json"),
            """
            {
              "mcpServers": {
                "probe": { "command": "deno", "args": ["info"] }
              }
            }
            """);
        return root;
    }

    private static void WriteMcpDataFile(string root, string contents)
    {
        File.WriteAllText(Path.Combine(root, "config", "servers.json"), contents);
    }

    private static string CreateDeclarativePluginsRoot(
        string pluginName,
        bool includeUnknownKind = false)
    {
        // The kernel-side plugin id is the plugins-root-relative directory name
        // (the same id the host relay asserts), not the deno.json package name.
        var root = Path.Combine(Path.GetTempPath(), pluginName);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/declarative",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        var unknownBlock = includeUnknownKind
            ? ",\n    \"FutureKind\": [{ \"opaque\": true }]"
            : string.Empty;
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            $$"""
            {
              "capabilities": ["tools.invoke"],
              "extensions": {
                "McpDiscover": [
                  { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } }
                ]{{unknownBlock}}
              }
            }
            """);
        // Deliberately no mod.ts and no entrypoints: the plugin is data-only.
        return root;
    }

    [Fact(Timeout = 60_000)]
    public async Task OneInvalidManifestEntryFailsTheWholeManifestDiscovery()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        // The discovery-time granularity decision (framework §3.2): a manifest's
        // extensions entries validate per discovery pass with all-or-nothing failure —
        // one invalid entry fails the whole manifest discovery (never a per-entry
        // inert degradation, which is the skills grammar's shape, not MCP's).
        var root = Path.Combine(Path.GetTempPath(), $"declarative-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/declarative-invalid",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "capabilities": ["tools.invoke"],
              "extensions": {
                "McpDiscover": [
                  { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } },
                  { "module": "npm:@maieutics/broken-server", "transport": { "type": "nonsense" } }
                ]
              }
            }
            """);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            var registration = Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            var discovery = manager.DiscoverManifestMcpAsync(registration);
            Assert.False(discovery.IsSuccess);
            Assert.Equal("invalid_server_definition", discovery.Failure);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AManifestEntryConflictingWithTheDataFileFailsTheDiscovery()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        // Same server id with a different generation key across the two declarative
        // sources (extensions entry vs mcp.json data file) fails the whole discovery —
        // the merge rules mirror the coordinator's cross-plugin rules.
        var root = Path.Combine(Path.GetTempPath(), $"declarative-conflict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/declarative-conflict",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "capabilities": ["tools.invoke"],
              "entrypoints": { "mcp": "./mcp.json" },
              "extensions": {
                "McpDiscover": [
                  { "module": "probe", "transport": { "type": "http", "url": "http://127.0.0.1:9/mcp" } }
                ]
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "mcp.json"),
            """
            { "mcpServers": { "probe": { "command": "deno", "args": ["info"] } } }
            """);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            var registration = Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            var discovery = manager.DiscoverManifestMcpAsync(registration);
            Assert.False(discovery.IsSuccess);
            Assert.Equal("conflicting_server_definition", discovery.Failure);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string CreateFakeDenoExecutable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc-fake-deno-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
