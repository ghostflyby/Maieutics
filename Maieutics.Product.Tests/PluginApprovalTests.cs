using Maieutics.Commands;
using Maieutics.Control;
using Maieutics.DenoRepl;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Maieutics.Plugins.Contributions;

namespace Maieutics.Product.Tests;

/// <summary>
///     Plugin declaration approval (ADR 0037): the declaration fingerprint, the persisted
///     approval store, the fail-closed gate, change-revokes on reload, and the
///     <c>%plugin</c> command surface.
/// </summary>
public sealed class PluginApprovalTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    // —— Fingerprint ——

    [Fact]
    public void FingerprintIgnoresDeclarationOrderingAndJsonFormatting()
    {
        var first = LoadManifest(
            """
            {
              "name": "@maieutics/probe",
              "permissions": { "default": { "read": ["./src", "./"], "env": ["A", "B"] } }
            }
            """,
            """
            {
              "capabilities": ["tools.invoke", "content.read"],
              "dependencies": ["beta", "alpha"],
              "extensions": {
                "McpDiscover": [
                  { "module": "npm:@maieutics/one", "transport": { "type": "stdio", "command": "deno" } },
                  { "module": "npm:@maieutics/two", "transport": { "type": "stdio", "command": "node" } }
                ]
              }
            }
            """);
        // Same declarations, reordered arrays, reordered JSON members, extra whitespace:
        // the fingerprint covers the declaration surface, not its spelling.
        var second = LoadManifest(
            """
            {
              "permissions": {
                "default": {
                  "env":     ["B", "A"],
                  "read": ["./", "./src"]
                }
              },
              "name": "@maieutics/probe"
            }
            """,
            """
            {
              "extensions": {
                "McpDiscover": [
                  { "transport": { "command": "node", "type": "stdio" }, "module": "npm:@maieutics/two" },
                  { "transport": { "command": "deno", "type": "stdio" }, "module": "npm:@maieutics/one" }
                ]
              },
              "dependencies": ["alpha", "beta"],
              "capabilities": ["content.read", "tools.invoke"]
            }
            """);
        Assert.Equal(PluginDeclarationFingerprint.Compute(second), PluginDeclarationFingerprint.Compute(first));
    }

    [Fact]
    public void EveryDeclarationSurfaceChangeChangesTheFingerprint()
    {
        var baseline = LoadManifest(
            """
            {
              "name": "@maieutics/probe",
              "permissions": { "default": { "read": ["./"] } }
            }
            """,
            """
            { "entrypoints": { "worker": { "main": ["./main.ts"] } } }
            """);
        var baselineFingerprint = PluginDeclarationFingerprint.Compute(baseline);

        void AssertDiffers(string denoJson, string maieuticsJson) => Assert.NotEqual(baselineFingerprint, PluginDeclarationFingerprint.Compute(LoadManifest(denoJson, maieuticsJson)));

        AssertDiffers(
            """
            {
              "name": "@maueutics-typo",
              "permissions": { "default": { "read": ["./"] } }
            }
            """,
            """
            { "entrypoints": { "worker": { "main": ["./main.ts"] } } }
            """);
        AssertDiffers(
            """
            {
              "name": "@maieutics/probe",
              "permissions": { "default": { "read": ["./"], "env": ["PROBE"] } }
            }
            """,
            """
            { "entrypoints": { "worker": { "main": ["./main.ts"] } } }
            """);
        AssertDiffers(
            """
            {
              "name": "@maieutics/probe",
              "permissions": { "default": { "read": ["./", "/tmp"] } }
            }
            """,
            """
            { "entrypoints": { "worker": { "main": ["./main.ts"] } } }
            """);
        AssertDiffers(
            """
            {
              "name": "@maieutics/probe",
              "permissions": { "default": { "read": ["./"] } }
            }
            """,
            """
            { "entrypoints": { "worker": { "main": ["./other.ts"] } } }
            """);
        AssertDiffers(
            """
            {
              "name": "@maieutics/probe",
              "permissions": { "default": { "read": ["./"] } }
            }
            """,
            """
            {
              "entrypoints": { "worker": { "main": ["./main.ts"] } },
              "dependencies": ["other"],
              "inspections": { "contentReadAll": true }
            }
            """);
    }

    [Fact]
    public void OnlyEmptyDeclarationsAreApprovalExempt()
    {
        var empty = LoadManifest(
            """
            { "name": "@maieutics/empty" }
            """,
            """
            { "entrypoints": {} }
            """);
        Assert.True(PluginDeclarationFingerprint.IsApprovalExempt(empty));

        var skeleton = LoadManifest(
            """
            {
              "name": "@maieutics/plugins",
              "imports": { "@maieutics/plugin-sdk": "jsr:@maieutics/plugin-sdk@^0.1" }
            }
            """,
            """
            { "isolation": "auto", "entrypoints": {} }
            """);
        Assert.True(PluginDeclarationFingerprint.IsApprovalExempt(skeleton));

        var grantsOnly = LoadManifest(
            """
            { "name": "@maieutics/grants", "permissions": { "default": { "net": true } } }
            """,
            """
            { "entrypoints": {} }
            """);
        Assert.False(PluginDeclarationFingerprint.IsApprovalExempt(grantsOnly));
    }

    // —— Approval store ——

    [Fact]
    public void ApprovalStoreRoundTripsRecordsAndRemovals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"approvals-{Guid.NewGuid():N}.json");
        var store = PluginApprovalStore.Load(path, out var loadError);
        Assert.Null(loadError);
        Assert.False(store.TryGet("probe", out _));

        var grants = new PluginPermissionGrants(
            new PluginPermissionGrant(false, ["A"]),
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None);
        store.Set("probe", new PluginApprovalRecord("F1", "probe", DateTimeOffset.UtcNow, grants));
        Assert.False(store.Remove("missing"));

        // A fresh load of the same file observes the persisted record — the in-memory
        // view and the file never diverge.
        var reloaded = PluginApprovalStore.Load(path, out loadError);
        Assert.Null(loadError);
        Assert.True(reloaded.TryGet("probe", out var record));
        var persisted = record ?? throw new InvalidOperationException("The record is missing.");
        Assert.Equal("F1", persisted.Fingerprint);
        Assert.Equal(["A"], persisted.Grants.Env.Values);

        Assert.True(store.Remove("probe"));
        Assert.False(PluginApprovalStore.Load(path, out _).TryGet("probe", out _));
    }

    [Fact]
    public void ApprovalStoreFailsClosedOnUnusableFiles()
    {
        var invalid = Path.Combine(Path.GetTempPath(), $"approvals-{Guid.NewGuid():N}.json");
        File.WriteAllText(invalid, "{ not json");
        var invalidStore = PluginApprovalStore.Load(invalid, out var invalidError);
        Assert.NotNull(invalidError);
        Assert.False(invalidStore.TryGet("probe", out _));

        var newer = Path.Combine(Path.GetTempPath(), $"approvals-{Guid.NewGuid():N}.json");
        File.WriteAllText(newer, """{ "version": 2, "approvals": {} }""");
        var newerStore = PluginApprovalStore.Load(newer, out var newerError);
        Assert.NotNull(newerError);
        Assert.False(newerStore.TryGet("probe", out _));
    }

    // —— Manager gate ——

    [Fact(Timeout = 60_000)]
    public async Task UnapprovedPluginsStayCompletelyInertUntilApproved()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateProbePluginsRoot("approval-pending");
        var pluginId = Path.GetFileName(root);
        // An explicit per-test approvals path: the null fallback derives a fixed shared
        // temp location, and the approve/revoke below would poison later runs through it.
        var approvalsPath = Path.Combine(Path.GetTempPath(), $"mc-plugin-approvals-{Guid.NewGuid():N}.json");
        var manager = CreateManager(root, approvalsPath, new FakeTimeProvider());
        try
        {
            await StartAsync(manager);

            // Fail-closed: no synthetic registration, pending in status, and an
            // extension-point invoke refuses with the typed error instead of waking
            // a worker (ADR 0035 would otherwise restart it on demand).
            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            Assert.Equal(1, manager.GetStatus().PendingApprovals);
            var approval = Assert.Single(manager.ListPluginApprovals());
            Assert.Equal(pluginId, approval.PluginId);
            Assert.Equal(PluginApprovalState.PendingApproval, approval.State);
            Assert.Null(approval.ApprovedGrants);

            var invoke = await manager.InvokeExtensionPointAsync(
                pluginId, "./main", McpDiscoverContributionKind.ExtensionKind, null, TestContext.Current.CancellationToken);
            Assert.True(invoke.IsError);
            Assert.Equal("plugin_pending_approval", invoke.Code);

            // Approval persists the fingerprint and activates in-process: the
            // synthetic registration appears without a host restart.
            Assert.Contains("Approved", await manager.ApproveAsync(pluginId, TestContext.Current.CancellationToken));
            Assert.Equal(pluginId, Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind)).PluginId);
            Assert.Equal(0, manager.GetStatus().PendingApprovals);
            Assert.Equal(PluginApprovalState.Approved, Assert.Single(manager.ListPluginApprovals()).State);

            // Revocation returns the plugin to pending and withdraws the registration.
            await manager.RevokeAsync(pluginId, TestContext.Current.CancellationToken);
            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            Assert.Equal(1, manager.GetStatus().PendingApprovals);
        }
        finally
        {
            await manager.DisposeAsync();
            Cleanup(root);
            if (File.Exists(approvalsPath)) File.Delete(approvalsPath);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task SeededApprovalsActivateAtStartup()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateProbePluginsRoot("approval-seeded");
        var approvalsPath = PluginApprovalSeeds.SeedLocalPlugins(root);
        var manager = CreateManager(root, approvalsPath, new FakeTimeProvider());
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            Assert.Equal(0, manager.GetStatus().PendingApprovals);
        }
        finally
        {
            await manager.DisposeAsync();
            Cleanup(root);
            if (File.Exists(approvalsPath)) File.Delete(approvalsPath);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclarationChangeRevokesUntilReapproved()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateProbePluginsRoot("approval-revoke");
        var pluginId = Path.GetFileName(root);
        var approvalsPath = PluginApprovalSeeds.SeedLocalPlugins(root);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, approvalsPath, clock);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));

            // Widen the declared read grant: the fingerprint no longer matches the
            // persisted approval, so the reload revokes instead of applying it.
            var applied = manager.ReloadApplied;
            await File.WriteAllTextAsync(
                Path.Combine(root, "deno.json"),
                """
                {
                  "name": "@maieutics/probe",
                  "permissions": { "default": { "read": ["./", "/tmp"] } }
                }
                """, deadline.Token);
            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, applied, deadline.Token);
            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            Assert.Equal(1, manager.GetStatus().PendingApprovals);
            var approval = Assert.Single(manager.ListPluginApprovals());
            Assert.Equal(PluginApprovalState.PendingApproval, approval.State);
            Assert.NotNull(approval.ApprovedGrants);

            // Reverting to the previously approved shape re-matches the still-stored
            // record: the manifest revert reactivates without a new approval.
            var reverted = manager.ReloadApplied;
            await File.WriteAllTextAsync(
                Path.Combine(root, "deno.json"),
                """
                {
                  "name": "@maieutics/probe",
                  "permissions": { "default": { "read": ["./"] } }
                }
                """, deadline.Token);
            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, reverted, deadline.Token);
            Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            Assert.Equal(0, manager.GetStatus().PendingApprovals);

            // Widening again revokes again, and this time the user re-approves the new
            // declarations explicitly.
            var widened = manager.ReloadApplied;
            await File.WriteAllTextAsync(
                Path.Combine(root, "deno.json"),
                """
                {
                  "name": "@maieutics/probe",
                  "permissions": { "default": { "read": ["./", "/tmp"] } }
                }
                """, deadline.Token);
            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, widened, deadline.Token);
            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            await manager.ApproveAsync(pluginId, TestContext.Current.CancellationToken);
            Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
        }
        finally
        {
            await manager.DisposeAsync();
            Cleanup(root);
            if (File.Exists(approvalsPath)) File.Delete(approvalsPath);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task PendingDependenciesBlockTheirDependents()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        // Two nested plugins: `dep` declares an mcp data file (unapproved), and
        // `consumer` depends on `dep` and declares one of its own. Both stay inert
        // until `dep` is approved — approving the dependency unblocks the dependent.
        var root = Path.Combine(Path.GetTempPath(), $"approval-cascade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        // Import-map values point at module files; the kernel derives the package
        // directory from each target's parent (ScanProject).
        await File.WriteAllTextAsync(Path.Combine(root, "deno.json"), """
            {
              "name": "@maieutics/plugins",
              "imports": { "dep": "./dep/mod.ts", "consumer": "./consumer/mod.ts" }
            }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "maieutics.json"), """{ "entrypoints": {} }""", TestContext.Current.CancellationToken);
        WriteNestedPlugin(root, "dep", dependencies: null);
        WriteNestedPlugin(root, "consumer", dependencies: ["dep"]);
        var approvalsPath = PluginApprovalSeeds.SeedPlugins(
            Path.Combine(root, "consumer"));

        var manager = CreateManager(root, approvalsPath, new FakeTimeProvider());
        try
        {
            await StartAsync(manager);
            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));
            var approvals = manager.ListPluginApprovals();
            Assert.Equal(3, approvals.Count());
            Assert.Equal(PluginApprovalState.PendingApproval, approvals.Single(approval => approval.PluginId == "dep").State);
            Assert.Equal(PluginApprovalState.BlockedByDependency, approvals.Single(approval => approval.PluginId == "consumer").State);

            // Approving the dependency unblocks the dependent: both contribute
            // without a restart.
            await manager.ApproveAsync("dep", TestContext.Current.CancellationToken);
            Assert.Equal(2, manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind).Count());
            Assert.All(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind), registration => Assert.True(registration.PluginId == "dep" || registration.PluginId == "consumer"));
            Assert.Equal(0, manager.GetStatus().PendingApprovals);
        }
        finally
        {
            await manager.DisposeAsync();
            Cleanup(root);
            if (File.Exists(approvalsPath)) File.Delete(approvalsPath);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task PluginCommandsListApproveAndRevoke()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateProbePluginsRoot("approval-commands");
        var pluginId = Path.GetFileName(root);
        var approvalsPath = Path.Combine(Path.GetTempPath(), $"mc-plugin-approvals-{Guid.NewGuid():N}.json");
        var manager = CreateManager(root, approvalsPath, new FakeTimeProvider());
        var executor = new MaieuticsCommandExecutor(null, null, null, null, null, manager);
        try
        {
            await StartAsync(manager);

            Assert.Equal(new[] { "%maieutics", "plugin", "list" }, MaieuticsCommandLanguage.NormalizeCommandArguments(["%plugin", "list"]));
            Assert.True(MaieuticsCommandLanguage.IsCommandCell("%plugin approve x"));

            var list = await executor.ExecuteAsync("%plugin list", null, TestContext.Current.CancellationToken);
            Assert.Contains(pluginId, list.Markdown);
            Assert.Contains("pending approval", list.Markdown);

            var approve = await executor.ExecuteAsync(
                $"%plugin approve {pluginId}", null, TestContext.Current.CancellationToken);
            Assert.Contains("Approved", approve.Markdown);
            Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));

            var revoke = await executor.ExecuteAsync(
                $"%plugin revoke {pluginId}", null, TestContext.Current.CancellationToken);
            Assert.Contains("Revoked", revoke.Markdown);
            Assert.Empty(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind));

            var unknown = () => executor.ExecuteAsync(
                "%plugin approve missing", null, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<MaieuticsCommandException>(async () => { await unknown.Invoke(); });
        }
        finally
        {
            await manager.DisposeAsync();
            Cleanup(root);
            if (File.Exists(approvalsPath)) File.Delete(approvalsPath);
        }
    }

    // —— Harness ——

    private static PluginDescriptor LoadManifest(string denoJson, string maieuticsJson)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "deno.json"), denoJson);
        File.WriteAllText(Path.Combine(directory, "maieutics.json"), maieuticsJson);
        Assert.True(PluginManifest.TryLoad(directory, out var descriptor, out var error), error);
        return descriptor ?? throw new InvalidOperationException("The manifest did not load.");
    }

    /// <summary>A plugins root that is itself one declarative probe plugin: an mcp data
    /// file plus an extensions section, no workers — the registration surface the gate
    /// controls, without needing a real host connection.</summary>
    private static string CreateProbePluginsRoot(string pluginName)
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
              "name": "@maieutics/probe",
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
                  { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } }
                ]
              }
            }
            """);
        return root;
    }

    private static void WriteNestedPlugin(string root, string name, string[]? dependencies)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "deno.json"),
            $$"""
            {
              "name": "@maieutics/{{name}}",
              "exports": { "./main": "./mod.ts" },
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(Path.Combine(directory, "mod.ts"), "export const marker = 1;\n");
        File.WriteAllText(
            Path.Combine(directory, "maieutics.json"),
            $$"""
            {
              "capabilities": ["tools.invoke"],
              {{(dependencies is null ? "" : $"\"dependencies\": [\"{string.Join("\", \"", dependencies)}\"],")}}
              "extensions": {
                "McpDiscover": [
                  { "module": "npm:@maieutics/{{name}}-server", "transport": { "type": "stdio", "command": "deno" } }
                ]
              }
            }
            """);
    }

    private static async Task StartAsync(PluginHostManager manager)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await manager.StartAsync(deadline.Token);
        await manager.WaitUntilReadyAsync(deadline.Token);
    }

    /// <summary>The manager construction every suite test shares. The fake clock owns the
    /// watcher debounce: tests that change watched files advance it past
    /// <see cref="PluginHostManager.PluginReloadDebounce"/> through
    /// <see cref="PluginWatcherTestWaits"/> instead of waiting the window out in real time.</summary>
    private static PluginHostManager CreateManager(string root, string? approvalsPath, FakeTimeProvider clock)
    {
        return new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = FakeDenoExecutable.Path },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            clock,
            pluginApprovalsPath: approvalsPath);
    }


    private static void Cleanup(string root)
    {
        TempArea.DeleteTree(root);
    }
}
