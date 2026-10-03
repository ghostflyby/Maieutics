using System.Text.Json;
using FluentAssertions;
using Maieutics.Commands;
using Maieutics.Control;
using Maieutics.DenoRepl;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

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
        PluginDeclarationFingerprint.Compute(first).Should()
            .Be(PluginDeclarationFingerprint.Compute(second));
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

        Action<string, string> assertDiffers = (denoJson, maieuticsJson) =>
            PluginDeclarationFingerprint.Compute(LoadManifest(denoJson, maieuticsJson)).Should()
                .NotBe(baselineFingerprint, "the declaration change must revoke the approval");

        assertDiffers(
            """
            {
              "name": "@maueutics-typo",
              "permissions": { "default": { "read": ["./"] } }
            }
            """,
            """
            { "entrypoints": { "worker": { "main": ["./main.ts"] } } }
            """);
        assertDiffers(
            """
            {
              "name": "@maieutics/probe",
              "permissions": { "default": { "read": ["./"], "env": ["PROBE"] } }
            }
            """,
            """
            { "entrypoints": { "worker": { "main": ["./main.ts"] } } }
            """);
        assertDiffers(
            """
            {
              "name": "@maieutics/probe",
              "permissions": { "default": { "read": ["./", "/tmp"] } }
            }
            """,
            """
            { "entrypoints": { "worker": { "main": ["./main.ts"] } } }
            """);
        assertDiffers(
            """
            {
              "name": "@maieutics/probe",
              "permissions": { "default": { "read": ["./"] } }
            }
            """,
            """
            { "entrypoints": { "worker": { "main": ["./other.ts"] } } }
            """);
        assertDiffers(
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
        PluginDeclarationFingerprint.IsApprovalExempt(empty).Should().BeTrue();

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
        PluginDeclarationFingerprint.IsApprovalExempt(skeleton).Should().BeTrue();

        var grantsOnly = LoadManifest(
            """
            { "name": "@maieutics/grants", "permissions": { "default": { "net": true } } }
            """,
            """
            { "entrypoints": {} }
            """);
        PluginDeclarationFingerprint.IsApprovalExempt(grantsOnly).Should().BeFalse();
    }

    // —— Approval store ——

    [Fact]
    public void ApprovalStoreRoundTripsRecordsAndRemovals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"approvals-{Guid.NewGuid():N}.json");
        var store = PluginApprovalStore.Load(path, out var loadError);
        loadError.Should().BeNull();
        store.TryGet("probe", out _).Should().BeFalse();

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
        store.Remove("missing").Should().BeFalse();

        // A fresh load of the same file observes the persisted record — the in-memory
        // view and the file never diverge.
        var reloaded = PluginApprovalStore.Load(path, out loadError);
        loadError.Should().BeNull();
        reloaded.TryGet("probe", out var record).Should().BeTrue();
        var persisted = record ?? throw new InvalidOperationException("The record is missing.");
        persisted.Fingerprint.Should().Be("F1");
        persisted.Grants.Env.Values.Should().Equal("A");

        store.Remove("probe").Should().BeTrue();
        PluginApprovalStore.Load(path, out _).TryGet("probe", out _).Should().BeFalse();
    }

    [Fact]
    public void ApprovalStoreFailsClosedOnUnusableFiles()
    {
        var invalid = Path.Combine(Path.GetTempPath(), $"approvals-{Guid.NewGuid():N}.json");
        File.WriteAllText(invalid, "{ not json");
        var invalidStore = PluginApprovalStore.Load(invalid, out var invalidError);
        invalidError.Should().NotBeNull();
        invalidStore.TryGet("probe", out _).Should().BeFalse();

        var newer = Path.Combine(Path.GetTempPath(), $"approvals-{Guid.NewGuid():N}.json");
        File.WriteAllText(newer, """{ "version": 2, "approvals": {} }""");
        var newerStore = PluginApprovalStore.Load(newer, out var newerError);
        newerError.Should().NotBeNull();
        newerStore.TryGet("probe", out _).Should().BeFalse();
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
        var manager = CreateManager(root, approvalsPath);
        try
        {
            await StartAsync(manager);

            // Fail-closed: no synthetic registration, pending in status, and an
            // extension-point invoke refuses with the typed error instead of waking
            // a worker (ADR 0035 would otherwise restart it on demand).
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().BeEmpty();
            manager.GetStatus().PendingApprovals.Should().Be(1);
            var approval = manager.ListPluginApprovals().Should().ContainSingle().Which;
            approval.PluginId.Should().Be(pluginId);
            approval.State.Should().Be(PluginApprovalState.PendingApproval);
            approval.ApprovedGrants.Should().BeNull();

            var invoke = await manager.InvokeExtensionPointAsync(
                pluginId, "./main", PluginExtensionKind.McpDiscover, null, TestContext.Current.CancellationToken);
            invoke.IsError.Should().BeTrue();
            invoke.Code.Should().Be("plugin_pending_approval");

            // Approval persists the fingerprint and activates in-process: the
            // synthetic registration appears without a host restart.
            (await manager.ApproveAsync(pluginId, TestContext.Current.CancellationToken)).Should()
                .Contain("Approved");
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().ContainSingle()
                .Which.PluginId.Should().Be(pluginId);
            manager.GetStatus().PendingApprovals.Should().Be(0);
            manager.ListPluginApprovals().Should().ContainSingle()
                .Which.State.Should().Be(PluginApprovalState.Approved);

            // Revocation returns the plugin to pending and withdraws the registration.
            await manager.RevokeAsync(pluginId, TestContext.Current.CancellationToken);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().BeEmpty();
            manager.GetStatus().PendingApprovals.Should().Be(1);
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
        var manager = CreateManager(root, approvalsPath);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().ContainSingle();
            manager.GetStatus().PendingApprovals.Should().Be(0);
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
        var manager = CreateManager(root, approvalsPath);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().ContainSingle();

            // Widen the declared read grant: the fingerprint no longer matches the
            // persisted approval, so the reload revokes instead of applying it.
            var applied = manager.ReloadApplied;
            File.WriteAllText(
                Path.Combine(root, "deno.json"),
                """
                {
                  "name": "@maieutics/probe",
                  "permissions": { "default": { "read": ["./", "/tmp"] } }
                }
                """);
            await applied.WaitAsync(deadline.Token);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().BeEmpty();
            manager.GetStatus().PendingApprovals.Should().Be(1);
            var approval = manager.ListPluginApprovals().Should().ContainSingle().Which;
            approval.State.Should().Be(PluginApprovalState.PendingApproval);
            approval.ApprovedGrants.Should().NotBeNull("the stale record stays for the diff");

            // Reverting to the previously approved shape re-matches the still-stored
            // record: the manifest revert reactivates without a new approval.
            var reverted = manager.ReloadApplied;
            File.WriteAllText(
                Path.Combine(root, "deno.json"),
                """
                {
                  "name": "@maieutics/probe",
                  "permissions": { "default": { "read": ["./"] } }
                }
                """);
            await reverted.WaitAsync(deadline.Token);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().ContainSingle();
            manager.GetStatus().PendingApprovals.Should().Be(0);

            // Widening again revokes again, and this time the user re-approves the new
            // declarations explicitly.
            var widened = manager.ReloadApplied;
            File.WriteAllText(
                Path.Combine(root, "deno.json"),
                """
                {
                  "name": "@maieutics/probe",
                  "permissions": { "default": { "read": ["./", "/tmp"] } }
                }
                """);
            await widened.WaitAsync(deadline.Token);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().BeEmpty();
            await manager.ApproveAsync(pluginId, TestContext.Current.CancellationToken);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().ContainSingle();
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
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/plugins",
              "imports": { "dep": "./dep/mod.ts", "consumer": "./consumer/mod.ts" }
            }
            """);
        File.WriteAllText(Path.Combine(root, "maieutics.json"), """{ "entrypoints": {} }""");
        WriteNestedPlugin(root, "dep", dependencies: null);
        WriteNestedPlugin(root, "consumer", dependencies: ["dep"]);
        var approvalsPath = PluginApprovalSeeds.SeedPlugins(
            Path.Combine(root, "consumer"));

        var manager = CreateManager(root, approvalsPath);
        try
        {
            await StartAsync(manager);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().BeEmpty();
            var approvals = manager.ListPluginApprovals();
            approvals.Should().HaveCount(3);
            approvals.Single(approval => approval.PluginId == "dep").State
                .Should().Be(PluginApprovalState.PendingApproval);
            approvals.Single(approval => approval.PluginId == "consumer").State
                .Should().Be(PluginApprovalState.BlockedByDependency);

            // Approving the dependency unblocks the dependent: both contribute
            // without a restart.
            await manager.ApproveAsync("dep", TestContext.Current.CancellationToken);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover)
                .Should().HaveCount(2)
                .And.OnlyContain(registration =>
                    registration.PluginId == "dep" || registration.PluginId == "consumer");
            manager.GetStatus().PendingApprovals.Should().Be(0);
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
        var manager = CreateManager(root, approvalsPath);
        var executor = new MaieuticsCommandExecutor(null, null, null, null, null, manager);
        try
        {
            await StartAsync(manager);

            MaieuticsCommandLanguage.NormalizeCommandArguments(["%plugin", "list"]).Should()
                .Equal("%maieutics", "plugin", "list");
            MaieuticsCommandLanguage.IsCommandCell("%plugin approve x").Should().BeTrue();

            var list = await executor.ExecuteAsync("%plugin list", null, TestContext.Current.CancellationToken);
            list.Markdown.Should().Contain(pluginId).And.Contain("pending approval");

            var approve = await executor.ExecuteAsync(
                $"%plugin approve {pluginId}", null, TestContext.Current.CancellationToken);
            approve.Markdown.Should().Contain("Approved");
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().ContainSingle();

            var revoke = await executor.ExecuteAsync(
                $"%plugin revoke {pluginId}", null, TestContext.Current.CancellationToken);
            revoke.Markdown.Should().Contain("Revoked");
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().BeEmpty();

            var unknown = () => executor.ExecuteAsync(
                "%plugin approve missing", null, TestContext.Current.CancellationToken);
            await unknown.Should().ThrowAsync<MaieuticsCommandException>();
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
        PluginManifest.TryLoad(directory, out var descriptor, out var error).Should()
            .BeTrue(error);
        return descriptor ?? throw new InvalidOperationException("The manifest did not load.");
    }

    /// <summary>A plugins root that is itself one declarative probe plugin: an mcp data
    /// file plus an extensions section, no workers — the registration surface the gate
    /// controls, without needing a real host connection.</summary>
    private static string CreateProbePluginsRoot(string pluginName)
    {
        var root = Path.Combine(Path.GetTempPath(), pluginName);
        if (Directory.Exists(root)) Directory.Delete(root, true);
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

    private static PluginHostManager CreateManager(string root, string? approvalsPath)
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
            TimeProvider.System,
            pluginApprovalsPath: approvalsPath);
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

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
