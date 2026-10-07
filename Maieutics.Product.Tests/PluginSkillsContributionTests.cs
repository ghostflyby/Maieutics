using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Permissions;
using Maieutics.Plugins;
using Maieutics.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

/// <summary>Plugin skill contributions (ADR 0039 stage 2): declarative roots through the
/// extensions Skills kind, the read-grant gate for outside-root roots, approval gating,
/// and the worker generator invoke with sticky-last-good.</summary>
public sealed class PluginSkillsContributionTests : IDisposable
{
    private const int Deadline = 60_000;

    private readonly string workspaceRoot =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-plugin-ws-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void WriteSkill(string root, string name, string description)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\nBody of {name}.\n");
    }

    /// <summary>A plugins root whose single plugin declares Skills roots; read grants are
    /// the caller's choice.</summary>
    private static string CreateSkillsPluginsRoot(string pluginName, string rootsJson, string readGrantJson)
    {
        var root = Path.Combine(Path.GetTempPath(), pluginName);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            $$"""
            {
              "name": "@maieutics/plugins",
              "version": "0.1.0",
              "permissions": { "default": { "read": {{readGrantJson}} } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            $$"""
            {
              "extensions": { "Skills": [ { "roots": {{rootsJson}} } ] }
            }
            """);
        return root;
    }

    private static Skills.SkillCatalog CreateCatalog(string workspaceRoot) =>
        SkillCatalog.Create(
            [(SkillSource.Workspace, workspaceRoot)],
            new FakeTimeProvider(),
            NullLogger<Skills.SkillCatalog>.Instance);

    private static VariableTable CreateVariables(Dictionary<string, string> environment) =>
        new(
            new EmptyVariableSource(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            name => environment.GetValueOrDefault(name));

    private static PluginHostManager CreateManager(
        string root,
        TimeProvider clock,
        Skills.SkillCatalog catalog,
        VariableTable variables,
        string? approvalsPath)
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
            pluginApprovalsPath: approvalsPath,
            skillCatalog: catalog,
            manifestVariables: variables,
            workspaceRootAccessor: () => null);
    }

    private static async Task AwaitCatalogAsync(
        Skills.SkillCatalog catalog,
        Func<SkillCatalogSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(catalog.Current)) return;
            await Task.Delay(20, cancellationToken);
        }

        Assert.Fail(
            $"The skill catalog did not reach the expected state within 15s " +
            $"(currently {catalog.Current.Skills.Length} skills, {catalog.Current.Diagnostics.Length} diagnostics).");
    }

    [Fact(Timeout = Deadline)]
    public async Task DeclarativeRootInsideThePluginIsEnumerated()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateSkillsPluginsRoot(
            "skills-plain",
            """["./skills"]""",
            """["./"]""");
        WriteSkill(Path.Combine(root, "skills"), "alpha", "declared by the plugin");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var manager = CreateManager(
            root,
            clock,
            catalog,
            CreateVariables([]),
            PluginApprovalSeeds.SeedLocalPlugins(root));
        try
        {
            await manager.StartAsync(TestContext.Current.CancellationToken);
            await manager.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "alpha"),
                TestContext.Current.CancellationToken);

            var skill = catalog.Current.Skills.Single(skill => skill.Name == "alpha");
            skill.Source.Should().Be(SkillSource.PluginDeclared);
            skill.BodyPath.Should().Contain("skills-plain");
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task LowercaseManifestKindIsAccepted()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateSkillsPluginsRoot(
            "skills-lowercase",
            """["./skills"]""",
            """["./"]""");
        // Rewrite the manifest with the recommended lowercase spelling of the kind.
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "extensions": { "skills": [ { "roots": ["./skills"] } ] }
            }
            """);
        WriteSkill(Path.Combine(root, "skills"), "delta", "declared in lowercase");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var manager = CreateManager(
            root,
            clock,
            catalog,
            CreateVariables([]),
            PluginApprovalSeeds.SeedLocalPlugins(root));
        try
        {
            await manager.StartAsync(TestContext.Current.CancellationToken);
            await manager.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "delta"),
                TestContext.Current.CancellationToken);

            catalog.Current.Skills.Single(skill => skill.Name == "delta").Source
                .Should().Be(SkillSource.PluginDeclared);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task InterpolatedOutsideRootRequiresReadGrantCoverage()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var outside = Path.Combine(Path.GetTempPath(), $"maieutics-skill-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        WriteSkill(outside, "beta", "lives outside the plugin root");

        // Same plugin shape, once with a grant covering the outside root and once with
        // only the in-plugin-root grant.
        var granted = CreateSkillsPluginsRoot(
            "skills-granted",
            $$"""["${env.SKILL_TEST_ROOT}"]""",
            $$"""["./", "{{outside.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}}"]""");
        var ungranted = CreateSkillsPluginsRoot(
            "skills-ungranted",
            $$"""["${env.SKILL_TEST_ROOT}"]""",
            """["./"]""");
        var variables = CreateVariables(new Dictionary<string, string> { ["SKILL_TEST_ROOT"] = outside });
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var grantedManager = CreateManager(
            granted, clock, catalog, variables, PluginApprovalSeeds.SeedLocalPlugins(granted));
        var ungrantedManager = CreateManager(
            ungranted, clock, catalog, variables, PluginApprovalSeeds.SeedLocalPlugins(ungranted));
        try
        {
            await grantedManager.StartAsync(TestContext.Current.CancellationToken);
            await ungrantedManager.StartAsync(TestContext.Current.CancellationToken);

            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "beta"),
                TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            var skill = catalog.Current.Skills.Single(skill => skill.Name == "beta");
            skill.Source.Should().Be(SkillSource.PluginDeclared);
            catalog.Current.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Contains("not covered by a read grant"));
        }
        finally
        {
            await grantedManager.DisposeAsync();
            await ungrantedManager.DisposeAsync();
            if (Directory.Exists(granted)) Directory.Delete(granted, true);
            if (Directory.Exists(ungranted)) Directory.Delete(ungranted, true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task UnapprovedPluginContributesNothing()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateSkillsPluginsRoot(
            "skills-unapproved",
            """["./skills"]""",
            """["./"]""");
        WriteSkill(Path.Combine(root, "skills"), "gamma", "never loads without approval");
        var approvals = Path.Combine(Path.GetTempPath(), $"mc-approvals-empty-{Guid.NewGuid():N}.json");
        File.WriteAllText(approvals, """{"approvals": []}""");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var manager = CreateManager(root, clock, catalog, CreateVariables([]), approvals);
        try
        {
            await manager.StartAsync(TestContext.Current.CancellationToken);
            await manager.WaitUntilReadyAsync(TestContext.Current.CancellationToken);
            await Task.Delay(500, TestContext.Current.CancellationToken);

            catalog.Current.Skills.Should().BeEmpty();
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
            File.Delete(approvals);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task GeneratorContributionValidatesAndServesInlineBody()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket harness.");

        var root = Path.Combine(Path.GetTempPath(), $"skills-generator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/plugins",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "entrypoints": { "worker": { "main": ["./mod.ts"] } }
            }
            """);
        File.WriteAllText(Path.Combine(root, "mod.ts"), "export const Skills = {};\n");
        var approvals = PluginApprovalSeeds.SeedLocalPlugins(root);
        var pluginId = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var manager = CreateManager(root, clock, catalog, CreateVariables([]), approvals);
        var fakeSocket = new FakeHostWebSocket();
        try
        {
            await manager.StartAsync(TestContext.Current.CancellationToken);
            var attached = manager.HostConnectionAttached;
            var attach = manager.AttachHostAsync(fakeSocket, TestContext.Current.CancellationToken);
            await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _ = attach;

            // The host reports the worker's Skills export; the kernel reconciles and
            // invokes the generator through the same socket.
            manager.HandleHostMessage(SkillsRegistryFrame((pluginId, "./mod.ts")));

            var invokeEnvelope = await fakeSocket.ReadSkillInvokeAsync(TestContext.Current.CancellationToken);
            fakeSocket.Reply(
                manager,
                invokeEnvelope,
                """
                {"value":[{"name":"gen-skill","description":"computed by the worker","body":"# Generated\n"}]}
                """);

            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "gen-skill"),
                TestContext.Current.CancellationToken);

            var skill = catalog.Current.Skills.Single(skill => skill.Name == "gen-skill");
            skill.Source.Should().Be(SkillSource.PluginGenerated);
            skill.BodyText.Should().Be("# Generated\n");

            // The plane serves the inline body like any filesystem skill.
            var provider = new SkillResourceProvider(catalog);
            var result = await provider.ReadAsync(
                "skill://gen-skill",
                new Execution.ResourceReadRequest(long.MaxValue),
                TestContext.Current.CancellationToken);
            using var reader = new StreamReader(result.Content);
            (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).Should().Contain("# Generated");

            // A later failing invoke keeps the last good contribution (sticky). An
            // unchanged registry frame no longer re-invokes the generator (the
            // differential reconcile), so the re-run rides a trigger — the event whose
            // semantics are exactly "re-run this plugin".
            manager.HandleHostMessage(PluginTriggerFrame(pluginId));
            var secondInvoke = await fakeSocket.ReadSkillInvokeAsync(TestContext.Current.CancellationToken);
            fakeSocket.ReplyError(manager, secondInvoke, "generator_broken", "the worker failed");
            await Task.Delay(300, TestContext.Current.CancellationToken);

            catalog.Current.Skills.Should().Contain(skill => skill.Name == "gen-skill");
        }
        finally
        {
            fakeSocket.Dispose();
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task PublishedSkillsReplaceThePluginSPublishedPartAtRuntime()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket harness.");

        var root = Path.Combine(Path.GetTempPath(), $"skills-publisher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/plugins",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "capabilities": ["skills.publish"],
              "entrypoints": { "worker": { "main": ["./mod.ts"] } },
              "extensions": { "skills": [ { "roots": ["./skills"] } ] }
            }
            """);
        WriteSkill(Path.Combine(root, "skills"), "declared", "the declarative part");
        File.WriteAllText(Path.Combine(root, "mod.ts"), "export const Skills = {};\n");
        var approvals = PluginApprovalSeeds.SeedLocalPlugins(root);
        var pluginId = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var manager = CreateManager(root, clock, catalog, CreateVariables([]), approvals);
        var fakeSocket = new FakeHostWebSocket();
        try
        {
            await manager.StartAsync(TestContext.Current.CancellationToken);
            var attached = manager.HostConnectionAttached;
            var attach = manager.AttachHostAsync(fakeSocket, TestContext.Current.CancellationToken);
            await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _ = attach;

            // The declarative part is reconciled at start.
            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "declared"),
                TestContext.Current.CancellationToken);

            // A granted publish adds the published part beside it.
            manager.HandleHostMessage(SkillsPublishFrame(
                pluginId,
                """[{"name":"published-one","description":"pushed at runtime","body":"# Pushed\n"}]"""));
            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "published-one"),
                TestContext.Current.CancellationToken);
            var published = catalog.Current.Skills.Single(skill => skill.Name == "published-one");
            published.Source.Should().Be(SkillSource.PluginPublished);
            catalog.Current.Skills.Should().Contain(skill => skill.Name == "declared");

            // The next publish replaces the published set wholesale (empty clears it)
            // while the declarative part survives.
            manager.HandleHostMessage(
                SkillsPublishFrame(pluginId, """[{"name":"published-two","description":"the replacement"}]"""));
            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "published-two"),
                TestContext.Current.CancellationToken);
            catalog.Current.Skills.Should()
                .Contain(skill => skill.Name == "declared")
                .And.Contain(skill => skill.Name == "published-two")
                .And.NotContain(skill => skill.Name == "published-one");

            manager.HandleHostMessage(SkillsPublishFrame(pluginId, "[]"));
            await Task.Delay(300, TestContext.Current.CancellationToken);
            catalog.Current.Skills.Should()
                .Contain(skill => skill.Name == "declared")
                .And.NotContain(skill => skill.Name == "published-two");

            // An unrelated reconcile event (any registry frame) must not wipe the
            // published part of a publish-only plugin: cleanup is keyed on descriptor
            // presence and approval, not on pass participation.
            manager.HandleHostMessage(SkillsPublishFrame(
                pluginId,
                """[{"name":"published-three","description":"after a reconcile"}]"""));
            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "published-three"),
                TestContext.Current.CancellationToken);
            manager.HandleHostMessage(
                SkillsRegistryFrame((pluginId, "./mod.ts")));
            await Task.Delay(500, TestContext.Current.CancellationToken);
            catalog.Current.Skills.Should()
                .Contain(skill => skill.Name == "declared")
                .And.Contain(skill => skill.Name == "published-three");
        }
        finally
        {
            fakeSocket.Dispose();
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task PublishWithoutTheCapabilityGrantIsDenied()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket harness.");

        var root = Path.Combine(Path.GetTempPath(), $"skills-ungranted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/plugins",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "entrypoints": { "worker": { "main": ["./mod.ts"] } }
            }
            """);
        File.WriteAllText(Path.Combine(root, "mod.ts"), "export const Skills = {};\n");
        var approvals = PluginApprovalSeeds.SeedLocalPlugins(root);
        var pluginId = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var manager = CreateManager(root, clock, catalog, CreateVariables([]), approvals);
        var fakeSocket = new FakeHostWebSocket();
        try
        {
            await manager.StartAsync(TestContext.Current.CancellationToken);
            var attached = manager.HostConnectionAttached;
            var attach = manager.AttachHostAsync(fakeSocket, TestContext.Current.CancellationToken);
            await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _ = attach;

            manager.HandleHostMessage(SkillsPublishFrame(
                pluginId,
                """[{"name":"denied","description":"never lands"}]"""));

            var reply = await fakeSocket.ReadAsync(TestContext.Current.CancellationToken);
            reply.Should().Contain("capability_denied");
            await Task.Delay(300, TestContext.Current.CancellationToken);
            catalog.Current.Skills.Should().BeEmpty();
        }
        finally
        {
            fakeSocket.Dispose();
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task APoisonRootDegradesToADiagnosticWithoutAbortingThePass()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        // One poisoned plugin (a NUL-bearing root throws in Path.GetFullPath) enumerated
        // ahead of a healthy one: the healthy plugin must still contribute.
        var poisoned = CreateSkillsPluginsRoot(
            "skills-poisoned",
            $$"""["bad\u0000root"]""",
            """["./"]""");
        var healthy = CreateSkillsPluginsRoot(
            "skills-healthy",
            """["./skills"]""",
            """["./"]""");
        WriteSkill(Path.Combine(healthy, "skills"), "epsilon", "after the poison");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var poisonedManager = CreateManager(
            poisoned, clock, catalog, CreateVariables([]), PluginApprovalSeeds.SeedLocalPlugins(poisoned));
        var healthyManager = CreateManager(
            healthy, clock, catalog, CreateVariables([]), PluginApprovalSeeds.SeedLocalPlugins(healthy));
        try
        {
            await poisonedManager.StartAsync(TestContext.Current.CancellationToken);
            await healthyManager.StartAsync(TestContext.Current.CancellationToken);

            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "epsilon"),
                TestContext.Current.CancellationToken);
            catalog.Current.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Contains("cannot be resolved") ||
                diagnostic.Contains("not covered by a read grant"));
        }
        finally
        {
            await poisonedManager.DisposeAsync();
            await healthyManager.DisposeAsync();
            if (Directory.Exists(poisoned)) Directory.Delete(poisoned, true);
            if (Directory.Exists(healthy)) Directory.Delete(healthy, true);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task OversizedPublishArrayIsCappedWithAVisibleDiagnostic()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket harness.");

        var root = Path.Combine(Path.GetTempPath(), $"skills-flood-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/plugins",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "capabilities": ["skills.publish"],
              "entrypoints": { "worker": { "main": ["./mod.ts"] } }
            }
            """);
        File.WriteAllText(Path.Combine(root, "mod.ts"), "export const Skills = {};\n");
        var approvals = PluginApprovalSeeds.SeedLocalPlugins(root);
        var pluginId = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var manager = CreateManager(root, clock, catalog, CreateVariables([]), approvals);
        var fakeSocket = new FakeHostWebSocket();
        try
        {
            await manager.StartAsync(TestContext.Current.CancellationToken);
            var attached = manager.HostConnectionAttached;
            var attach = manager.AttachHostAsync(fakeSocket, TestContext.Current.CancellationToken);
            await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _ = attach;

            var entries = string.Join(
                ",",
                Enumerable.Range(0, 300).Select(index => $"{{\"name\":\"flood-{index:D4}\",\"description\":\"flood\"}}"));
            manager.HandleHostMessage(SkillsPublishFrame(pluginId, $"[{entries}]"));

            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "flood-0000"),
                TestContext.Current.CancellationToken);
            catalog.Current.Skills.Count(skill => skill.Name.StartsWith("flood-", StringComparison.Ordinal))
                .Should().Be(256);
            catalog.Current.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Contains("exceeds") && diagnostic.Contains("256"));
        }
        finally
        {
            fakeSocket.Dispose();
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task AnUnchangedRegistryFrameInvokesNoGenerator()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket harness.");

        var root = Path.Combine(Path.GetTempPath(), $"skills-diff-zero-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/plugins",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "entrypoints": { "worker": { "main": ["./mod.ts"] } }
            }
            """);
        File.WriteAllText(Path.Combine(root, "mod.ts"), "export const Skills = {};\n");
        var approvals = PluginApprovalSeeds.SeedLocalPlugins(root);
        var pluginId = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var manager = CreateManager(root, clock, catalog, CreateVariables([]), approvals);
        var fakeSocket = new FakeHostWebSocket();
        try
        {
            await manager.StartAsync(TestContext.Current.CancellationToken);
            var attached = manager.HostConnectionAttached;
            var attach = manager.AttachHostAsync(fakeSocket, TestContext.Current.CancellationToken);
            await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _ = attach;

            manager.HandleHostMessage(SkillsRegistryFrame((pluginId, "./mod.ts")));
            var invoke = await fakeSocket.ReadSkillInvokeAsync(TestContext.Current.CancellationToken);
            fakeSocket.Reply(
                manager,
                invoke,
                """{"value":[{"name":"gen-skill","description":"computed by the worker","body":"# Generated\n"}]}""");
            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "gen-skill"),
                TestContext.Current.CancellationToken);
            fakeSocket.TotalSkillInvokes.Should().Be(1);

            // The differential reconcile: a registry frame whose Skills export set is
            // identical to the previous frame's takes zero action — the generator is
            // not re-invoked and the contribution stays as committed.
            manager.HandleHostMessage(SkillsRegistryFrame((pluginId, "./mod.ts")));
            await Task.Delay(500, TestContext.Current.CancellationToken);
            fakeSocket.TotalSkillInvokes.Should().Be(
                1, "an unchanged Skills export set must not re-invoke the generator");
            catalog.Current.Skills.Should().Contain(skill => skill.Name == "gen-skill");
        }
        finally
        {
            fakeSocket.Dispose();
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task ATriggerReinvokesOnlyTheTriggeredPluginGenerator()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket harness.");

        var root = CreateTwoGeneratorPluginsRoot();
        var approvals = PluginApprovalSeeds.SeedLocalPlugins(root);
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var manager = CreateManager(root, clock, catalog, CreateVariables([]), approvals);
        var fakeSocket = new FakeHostWebSocket();
        try
        {
            await manager.StartAsync(TestContext.Current.CancellationToken);
            var attached = manager.HostConnectionAttached;
            var attach = manager.AttachHostAsync(fakeSocket, TestContext.Current.CancellationToken);
            await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _ = attach;

            manager.HandleHostMessage(
                SkillsRegistryFrame(("alpha", "./mod.ts"), ("beta", "./mod.ts")));

            // Answer both plugins' first invokes; the two reconciles run in parallel,
            // so the arrival order is not deterministic.
            var answered = new HashSet<string>(StringComparer.Ordinal);
            while (answered.Count < 2)
            {
                var invoke = await fakeSocket.ReadSkillInvokeAsync(TestContext.Current.CancellationToken);
                var owner = PluginIdOf(invoke);
                answered.Add(owner);
                fakeSocket.Reply(
                    manager,
                    invoke,
                    $$"""{"value":[{"name":"{{owner}}-skill","description":"computed by {{owner}}"}]}""");
            }

            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "alpha-skill") &&
                            snapshot.Skills.Any(skill => skill.Name == "beta-skill"),
                TestContext.Current.CancellationToken);

            // A trigger re-runs exactly the triggered plugin's generator; the sibling's
            // contribution is untouched and its generator is never invoked.
            manager.HandleHostMessage(PluginTriggerFrame("alpha"));
            var triggerInvoke = await fakeSocket.ReadSkillInvokeAsync(TestContext.Current.CancellationToken);
            PluginIdOf(triggerInvoke).Should().Be("alpha");
            fakeSocket.Reply(manager, triggerInvoke, """{"value":[]}""");
            await Task.Delay(300, TestContext.Current.CancellationToken);

            fakeSocket.SkillInvokesBy("alpha").Should().Be(2);
            fakeSocket.SkillInvokesBy("beta").Should().Be(
                1, "a trigger must not re-invoke another plugin's generator");
            catalog.Current.Skills.Should()
                .Contain(skill => skill.Name == "alpha-skill")
                .And.Contain(skill => skill.Name == "beta-skill");
        }
        finally
        {
            fakeSocket.Dispose();
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = Deadline)]
    public async Task RevokingAPluginRemovesOnlyItsContribution()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket harness.");

        var root = CreateTwoGeneratorPluginsRoot();
        var approvals = PluginApprovalSeeds.SeedLocalPlugins(root);
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(workspaceRoot);
        var manager = CreateManager(root, clock, catalog, CreateVariables([]), approvals);
        var fakeSocket = new FakeHostWebSocket();
        try
        {
            await manager.StartAsync(TestContext.Current.CancellationToken);
            var attached = manager.HostConnectionAttached;
            var attach = manager.AttachHostAsync(fakeSocket, TestContext.Current.CancellationToken);
            await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _ = attach;

            manager.HandleHostMessage(
                SkillsRegistryFrame(("alpha", "./mod.ts"), ("beta", "./mod.ts")));
            var answered = new HashSet<string>(StringComparer.Ordinal);
            while (answered.Count < 2)
            {
                var invoke = await fakeSocket.ReadSkillInvokeAsync(TestContext.Current.CancellationToken);
                var owner = PluginIdOf(invoke);
                answered.Add(owner);
                fakeSocket.Reply(
                    manager,
                    invoke,
                    $$"""{"value":[{"name":"{{owner}}-skill","description":"computed by {{owner}}"}]}""");
            }

            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.Any(skill => skill.Name == "alpha-skill") &&
                            snapshot.Skills.Any(skill => skill.Name == "beta-skill"),
                TestContext.Current.CancellationToken);

            // The approval transition touches only the transitioning plugin: beta's
            // contribution slot is removed, alpha's stays, and neither alpha's nor
            // beta's generator is invoked again.
            await manager.RevokeAsync("beta", TestContext.Current.CancellationToken);
            await AwaitCatalogAsync(
                catalog,
                snapshot => snapshot.Skills.All(skill => skill.Name != "beta-skill"),
                TestContext.Current.CancellationToken);

            catalog.Current.Skills.Should().Contain(skill => skill.Name == "alpha-skill");
            fakeSocket.SkillInvokesBy("alpha").Should().Be(
                1, "an approval transition must not re-invoke an unrelated plugin's generator");
            fakeSocket.SkillInvokesBy("beta").Should().Be(1);
        }
        finally
        {
            fakeSocket.Dispose();
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>A plugins root whose project imports two worker plugins (<c>alpha</c>,
    /// <c>beta</c>), each exporting a Skills generator; the import target's directory
    /// names are the kernel plugin ids (import targets resolve to entry files).</summary>
    private static string CreateTwoGeneratorPluginsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skills-two-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/plugins",
              "version": "0.1.0",
              "imports": {
                "alpha": "./plugins/alpha/mod.ts",
                "beta": "./plugins/beta/mod.ts"
              }
            }
            """);
        foreach (var plugin in (string[])["alpha", "beta"])
        {
            var directory = Path.Combine(root, "plugins", plugin);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "deno.json"),
                $$"""
                {
                  "name": "@maieutics/{{plugin}}",
                  "permissions": { "default": { "read": ["./"] } }
                }
                """);
            File.WriteAllText(
                Path.Combine(directory, "maieutics.json"),
                """
                {
                  "entrypoints": { "worker": { "main": ["./mod.ts"] } }
                }
                """);
            File.WriteAllText(Path.Combine(directory, "mod.ts"), "export const Skills = {};\n");
        }

        return root;
    }

    private static string SkillsRegistryFrame(params (string PluginId, string ExportName)[] workers)
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                plugins = workers
                    .Select(worker => new
                    {
                        pluginId = worker.PluginId,
                        exportName = worker.ExportName,
                        extensionPoints = new[] { ReplExtensionPointName.Skills }
                    })
                    .ToArray()
            });
        var envelope = JsonSerializer.SerializeToElement(
            new ReplEnvelope(1, ReplMessageType.ExtensionRegistry, Guid.NewGuid().ToString("N"), payload),
            ReplControlJsonContext.Default.ReplEnvelope);
        return envelope.GetRawText();
    }

    /// <summary>One <c>plugin.trigger</c> frame for the named plugin.</summary>
    private static string PluginTriggerFrame(string pluginId)
    {
        var payload = JsonSerializer.SerializeToElement(
            new PluginTriggerPayload(pluginId, "watch"),
            ReplControlJsonContext.Default.PluginTriggerPayload);
        var envelope = JsonSerializer.SerializeToElement(
            new ReplEnvelope(1, ReplMessageType.PluginTrigger, Guid.NewGuid().ToString("N"), payload),
            ReplControlJsonContext.Default.ReplEnvelope);
        return envelope.GetRawText();
    }

    /// <summary>The owning plugin id of one outbound Skills host.invoke envelope.</summary>
    private static string PluginIdOf(string invokeEnvelope)
    {
        using var document = JsonDocument.Parse(invokeEnvelope);
        return document.RootElement.GetProperty("payload").GetProperty("pluginId").GetString()
               ?? string.Empty;
    }

    /// <summary>One <c>capability.invoke</c> frame for skills.publish, carrying the
    /// replacement entry array as the payload.</summary>
    private static string SkillsPublishFrame(string pluginId, string entriesJson)
    {
        var payload = JsonSerializer.SerializeToElement(
            new CapabilityInvokePayload(
                pluginId,
                ReplCapabilityName.SkillsPublish,
                JsonDocument.Parse(entriesJson).RootElement.Clone()),
            ReplControlJsonContext.Default.CapabilityInvokePayload);
        var envelope = JsonSerializer.SerializeToElement(
            new ReplEnvelope(1, ReplMessageType.CapabilityInvoke, Guid.NewGuid().ToString("N"), payload),
            ReplControlJsonContext.Default.ReplEnvelope);
        return envelope.GetRawText();
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

    private sealed class EmptyVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name) => null;
    }

    /// <summary>In-memory host socket: captures kernel envelopes, answers Skills invokes
    /// with canned results (the PluginHostInvokeTests fake-socket shape, trimmed to what
    /// these tests need), and counts outbound Skills host.invoke envelopes per plugin so
    /// the differential tests can assert that unchanged events invoke no generator.</summary>
    private sealed class FakeHostWebSocket : WebSocket
    {
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<string> sent = Channel.CreateUnbounded<string>();
        private readonly Dictionary<string, int> skillInvokes = new(StringComparer.Ordinal);
        private WebSocketState state = WebSocketState.Open;

        internal int TotalSkillInvokes => skillInvokes.Values.Sum(static count => count);

        internal int SkillInvokesBy(string pluginId) => skillInvokes.GetValueOrDefault(pluginId, 0);

        public override WebSocketState State => state;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public async Task<string> ReadSkillInvokeAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (sent.Reader.TryRead(out var message) && message.Contains("\"Skills\"", StringComparison.Ordinal))
                    return message;
                await Task.Delay(20, cancellationToken);
            }

            throw new TimeoutException("No Skills host.invoke envelope arrived.");
        }

        public async Task<string> ReadAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (sent.Reader.TryRead(out var message)) return message;
                await Task.Delay(20, cancellationToken);
            }

            throw new TimeoutException("No host envelope arrived.");
        }

        public void Reply(PluginHostManager manager, string invokeEnvelope, string resultJson)
        {
            var envelope = JsonSerializer.Deserialize(
                invokeEnvelope,
                ReplControlJsonContext.Default.ReplEnvelope)!;
            manager.HandleHostMessage(
                "{\"version\":1,\"type\":\"host.invokeResult\",\"correlationId\":\"" + envelope.CorrelationId +
                "\",\"payload\":" + resultJson + "}");
        }

        public void ReplyError(PluginHostManager manager, string invokeEnvelope, string code, string message)
        {
            var envelope = JsonSerializer.Deserialize(
                invokeEnvelope,
                ReplControlJsonContext.Default.ReplEnvelope)!;
            manager.HandleHostMessage(
                "{\"version\":1,\"type\":\"host.invokeError\",\"correlationId\":\"" + envelope.CorrelationId +
                "\",\"payload\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\"}}");
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            await closed.Task.WaitAsync(cancellationToken);
            state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await closed.Task.WaitAsync(cancellationToken);
            state = WebSocketState.CloseReceived;
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (buffer.Array is not { } array)
                throw new ArgumentException("The test WebSocket send buffer requires a backing array.", nameof(buffer));
            return SendAsync(
                array.AsMemory(buffer.Offset, buffer.Count),
                messageType,
                endOfMessage,
                cancellationToken).AsTask();
        }

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (messageType != WebSocketMessageType.Text || !endOfMessage)
                throw new InvalidOperationException("The test socket only accepts complete text messages.");
            var message = System.Text.Encoding.UTF8.GetString(buffer.Span);
            CountSkillInvoke(message);
            return sent.Writer.WriteAsync(message, cancellationToken);
        }

        /// <summary>Counts one Skills host.invoke envelope per owning plugin; the send
        /// path is serialized through the kernel's outbound queue, so no lock is
        /// needed.</summary>
        private void CountSkillInvoke(string message)
        {
            if (!message.Contains("\"host.invoke\"", StringComparison.Ordinal) ||
                !message.Contains("\"Skills\"", StringComparison.Ordinal))
                return;
            try
            {
                var pluginId = PluginIdOf(message);
                if (pluginId.Length == 0) return;
                skillInvokes[pluginId] = skillInvokes.GetValueOrDefault(pluginId, 0) + 1;
            }
            catch (JsonException)
            {
                // Not a parsable invoke envelope; nothing to count.
            }
        }

        public override void Abort() => Dispose();

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        {
            state = WebSocketState.Closed;
            sent.Writer.TryComplete();
            closed.TrySetResult();
        }
    }
}
