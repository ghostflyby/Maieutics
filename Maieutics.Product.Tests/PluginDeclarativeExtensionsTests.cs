using Maieutics.Control;
using Maieutics.DenoRepl;
using Maieutics.Mcp;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging;
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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
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
        await File.WriteAllTextAsync(Path.Combine(outside, "deno.json"), """
            {
              "name": "@dep/shared",
              "version": "0.1.0"
            }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outside, "maieutics.json"), "{}", TestContext.Current.CancellationToken);

        var root = Path.Combine(Path.GetTempPath(), $"mc-import-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var outsideReference = Path.GetRelativePath(root, Path.Combine(outside, "mod.ts"));
        await File.WriteAllTextAsync(Path.Combine(root, "deno.json"), $$"""
              {
                "name": "@maieutics/import-root",
                "version": "0.1.0",
                "imports": { "@dep/shared": "{{outsideReference.Replace("\\", "\\\\")}}" }
              }
              """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "maieutics.json"), "{}", TestContext.Current.CancellationToken);

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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
            if (Directory.Exists(outside)) await TempArea.DeleteAsync(outside, CancellationToken.None);
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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclarativeSectionAdditionTakesEffectOnReload()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = Path.Combine(Path.GetTempPath(), $"declarative-add-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "deno.json"), """
            {
              "name": "@maieutics/declarative-add",
              "permissions": { "default": { "read": ["./"] } }
            }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "maieutics.json"), """
            {
              "capabilities": ["tools.invoke"]
            }
            """, TestContext.Current.CancellationToken);
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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
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
            await canceled.CancelAsync();
            var applied = manager.ReloadApplied;
            Assert.NotNull(await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager,
                clock,
                applied,
                canceled.Token)));
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    /// <summary>Deterministic regression for the scheduled-CI hang: under load the watcher
    /// chain registers (and completes) the debounce before the test's first read — the
    /// write's continuation and the watcher event race. The read must hand that completed
    /// registration back instead of swapping it for a fresh source: a waiter whose first
    /// read loses the edge then parks on a second registration a single change never
    /// produces, and the reload applies only when the Fact deadline cancels the wait (the
    /// observed failure applied it at the deadline instant, 60 s after an idle,
    /// never-advanced debounce). The registration is captured before the write, so the
    /// completion provably lands after the capture and before the racing read.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ACompletedDebounceRegistrationIsHandedToTheNextRead()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateMcpDataFilePluginsRoot("declarative-debounce-race");
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            Task registered = manager.WatcherDebounceRegistered;
            WriteMcpDataFile(root, "{");
            await registered.WaitAsync(deadline.Token);

            // Identity, not just completion: the waiter must observe exactly the edge its
            // completion belongs to. A fresh source handed out here is the lost edge.
            Task observed = manager.WatcherDebounceRegistered;
            Assert.Same(registered, observed);
            Assert.True(observed.IsCompleted);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    /// <summary>CI diagnosis: when MAIEUTICS_LOG_DIR is set (CI sets it and uploads the
    /// directory on failure), the managers under test log their reload and approval
    /// chains to the shared maieutics-{pid}.log instead of running silent — a hung
    /// watcher reload has no other observable surface. Locally the factory stays null
    /// and the managers keep the NullLogger.</summary>
    private static readonly ILoggerFactory? ManagerLoggerFactory = CreateManagerLoggerFactory();

    private static ILoggerFactory? CreateManagerLoggerFactory()
    {
        var logDirectory = Environment.GetEnvironmentVariable("MAIEUTICS_LOG_DIR");
        if (string.IsNullOrEmpty(logDirectory)) return null;
        return LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Debug)
            .AddProvider(new FileLoggerProvider(logDirectory)));
    }

    private static PluginHostManager CreateManager(string root, FakeTimeProvider clock)
    {
        var loggerFactory = ManagerLoggerFactory;
        return new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = FakeDenoExecutable.Path },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            loggerFactory?.CreateLogger<PluginHostManager>() ?? NullLogger<PluginHostManager>.Instance,
            loggerFactory ?? NullLoggerFactory.Instance,
            clock,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(root));
    }

    /// <summary>A workerless plugin project that declares MCP servers purely through an
    /// <c>mcp.json</c> data file (ADR 0033).</summary>
    private static string CreateMcpDataFilePluginsRoot(string pluginName)
    {
        // Unique suffix: these classes run in parallel collections, and a fixed path would
        // let one run delete another run's live tree. The plugin id is the directory name,
        // so callers derive it from the returned path.
        var root = Path.Combine(Path.GetTempPath(), $"{pluginName}-{Guid.NewGuid():N}");
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
        // Unique suffix: these classes run in parallel collections, and a fixed path would
        // let one run delete another run's live tree. The plugin id is the directory name,
        // so callers derive it from the returned path.
        var root = Path.Combine(Path.GetTempPath(), $"{pluginName}-{Guid.NewGuid():N}");
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
        await File.WriteAllTextAsync(Path.Combine(root, "deno.json"), """
            {
              "name": "@maieutics/declarative-invalid",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "maieutics.json"), """
            {
              "capabilities": ["tools.invoke"],
              "extensions": {
                "McpDiscover": [
                  { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } },
                  { "module": "npm:@maieutics/broken-server", "transport": { "type": "nonsense" } }
                ]
              }
            }
            """, TestContext.Current.CancellationToken);
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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
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
        await File.WriteAllTextAsync(Path.Combine(root, "deno.json"), """
            {
              "name": "@maieutics/declarative-conflict",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "maieutics.json"), """
            {
              "capabilities": ["tools.invoke"],
              "entrypoints": { "mcp": "./mcp.json" },
              "extensions": {
                "McpDiscover": [
                  { "module": "probe", "transport": { "type": "http", "url": "http://127.0.0.1:9/mcp" } }
                ]
              }
            }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), """
            { "mcpServers": { "probe": { "command": "deno", "args": ["info"] } } }
            """, TestContext.Current.CancellationToken);
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
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }
}
