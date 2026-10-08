using System.Text.Json;
using FluentAssertions;
using Maieutics.Mcp;
using Maieutics.Permissions;
using Maieutics.Plugins;
using Maieutics.Plugins.Contributions;
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

public sealed class PluginManifestTests
{
    [Fact]
    public void LoadsEntrypointsAndPositivePermissions()
    {
        var descriptor = LoadPlugin(
            """
            {
              "name": "@maieutics/example",
              "version": "0.1.0",
              "exports": { ".": "./mod.ts", "./mcp": "./src/mcp.ts" },
              "permissions": {
                "default": {
                  "read": ["./"],
                  "net": [],
                  "write": true
                }
              }
            }
            """,
            """
            {
              "isolation": "auto",
              "dependencies": ["base"],
              "entrypoints": {
                "worker": {
                  "main": ["./mod.ts", "./helper.ts"],
                  "echo": ["./src/echo.ts"]
                }
              }
            }
            """);

        descriptor.Name.Should().Be("@maieutics/example");
        descriptor.Isolation.Should().Be("auto");
        descriptor.Dependencies.Should().Equal("base");
        // Two entrypoints → two workers; the exports "./mcp" does not create a
        // worker on its own (exports only expose the API surface).
        descriptor.Workers.Should().HaveCount(2);
        descriptor.Workers[0].ExportName.Should().Be("main");
        descriptor.Workers[0].EntryUrl.Should().StartWith("file://");
        descriptor.Workers[0].EntryUrl.Should().EndWith("/mod.ts");
        descriptor.Workers[1].ExportName.Should().Be("echo");
        descriptor.Workers[1].EntryUrl.Should().EndWith("/src/echo.ts");
        descriptor.Permissions.Read.AllowAll.Should().BeFalse();
        descriptor.Permissions.Read.Values.Should().Equal("./");
        descriptor.Permissions.Net.Values.Should().BeEmpty();
        descriptor.Permissions.Write.AllowAll.Should().BeTrue();
    }

    [Fact]
    public void RejectsDirectoriesWithoutMaieuticsJson()
    {
        var directory = CreatePluginDirectory(
            """
            {
              "name": "@plain/package",
              "version": "0.1.0",
              "exports": "./mod.ts"
            }
            """,
            null);

        PluginManifest.TryLoad(directory, out _, out var error).Should().BeFalse();
        error.Should().Contain("maieutics.json");
    }

    [Fact]
    public void MissingEntrypointsYieldsNoWorkers()
    {
        var descriptor = LoadPlugin(
            """
            {
              "name": "@maieutics/single",
              "version": "0.1.0",
              "exports": "./mod.ts"
            }
            """,
            """
            { "entrypoints": {} }
            """);

        descriptor.Workers.Should().BeEmpty();
    }

    [Fact]
    public void ReportsInvalidMaieuticsJsonAsFailure()
    {
        var directory = CreatePluginDirectory("{ not json", "{ not json");

        PluginManifest.TryLoad(directory, out _, out var error).Should().BeFalse();
        error.Should().Contain("maieutics.json");
    }

    [Fact]
    public void IgnoresUnknownPermissionElementsAndShapes()
    {
        var descriptor = LoadPlugin(
            """
            {
              "name": "@maieutics/unknown",
              "tasks": { "start": "deno run mod.ts" },
              "permissions": {
                "default": {
                  "read": ["./", 42, null, { "path": "/tmp" }, ""],
                  "net": "https://example.com",
                  "notify": true,
                  "allow-all": true
                },
                "dev": { "read": true }
              }
            }
            """,
            """
            { "entrypoints": { "worker": { "main": ["./mod.ts"] } } }
            """);

        descriptor.Permissions.Read.AllowAll.Should().BeFalse();
        descriptor.Permissions.Read.Values.Should().Equal("./");
        descriptor.Permissions.Net.AllowAll.Should().BeFalse();
        descriptor.Permissions.Net.Values.Should().BeEmpty();
        descriptor.Permissions.Env.AllowAll.Should().BeFalse();
        descriptor.Permissions.Env.Values.Should().BeEmpty();
    }

    [Fact]
    public void SkipsEntrypointsThatEscapeThePluginDirectory()
    {
        var descriptor = LoadPlugin(
            """
            { "name": "@maieutics/escape" }
            """,
            """
            {
              "entrypoints": {
                "worker": {
                  "ok": ["./mod.ts"],
                  "escape": ["../outside.ts"],
                  "abs": ["/etc/passwd"]
                }
              }
            }
            """);

        descriptor.Workers.Should().HaveCount(1);
        descriptor.Workers[0].ExportName.Should().Be("ok");
    }

    [Fact]
    public void ResolvesLocalImportTargetsAndSkipsRemoteOnes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mc-plugin-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "local-dep"));
        File.WriteAllText(
            Path.Combine(directory, "deno.json"),
            """
            {
              "imports": {
                "@local": "./local-dep/mod.ts",
                "@std": "jsr:@std/assert@1",
                "@npm": "npm:left-pad@1",
                "@http": "https://example.com/x.ts"
              }
            }
            """);

        var targets = PluginManifest.ReadLocalImportTargets(directory).ToArray();
        targets.Should().HaveCount(1);
        targets[0].Should().Be(Path.GetFullPath(Path.Combine(directory, "local-dep", "mod.ts")));
    }

    [Fact]
    public void LoadsDeclarativeExtensionEntriesForKnownKinds()
    {
        var descriptor = LoadPlugin(
            """
            {
              "name": "@maieutics/declarative",
              "permissions": { "default": { "read": ["./"] } }
            }
            """,
            """
            {
              "extensions": {
                "McpDiscover": [
                  { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } }
                ]
              }
            }
            """);

        descriptor.Extensions.Should().ContainSingle();
        descriptor.Extensions[0].Kind.Should().Be("McpDiscover");
        descriptor.Extensions[0].Data.GetProperty("module")
            .GetString().Should().Be("npm:@maieutics/probe-server");
        descriptor.ExtensionDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public void UnknownExtensionKindsBecomeDiagnosticsAndAreDropped()
    {
        var descriptor = LoadPlugin(
            """
            {
              "name": "@maieutics/declarative",
              "permissions": { "default": { "read": ["./"] } }
            }
            """,
            """
            {
              "extensions": {
                "McpDiscover": [
                  { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } }
                ],
                "FutureKind": [{ "opaque": true }]
              }
            }
            """);

        // Only catalogued kinds survive; the unknown kind degrades to a visible
        // diagnostic instead of being silently honored or failing the plugin.
        descriptor.Extensions.Should().ContainSingle().Which.Kind.Should().Be("McpDiscover");
        descriptor.ExtensionDiagnostics.Should().ContainSingle()
            .Which.Should().Contain("FutureKind");
    }

    [Fact]
    public void RejectsMalformedExtensionsSections()
    {
        // A non-object section is structural corruption, not inert data: the plugin
        // fails to load loudly (maieutics.json strictness).
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/broken", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "extensions": ["not", "an", "object"] }
            """);
        PluginManifest.TryLoad(directory, out _, out var error).Should().BeFalse();
        error.Should().Contain("extensions");

        var directory2 = CreatePluginDirectory(
            """
            { "name": "@maieutics/broken", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "extensions": { "McpDiscover": ["not-an-object"] } }
            """);
        PluginManifest.TryLoad(directory2, out _, out var error2).Should().BeFalse();
        error2.Should().Contain("entries must be objects");
    }

    [Fact]
    public void RejectsTheReservedManifestExportNameAsALoadFailure()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/shadow", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            {
              "entrypoints": { "worker": { "maieutics.json": ["./mod.ts"] } }
            }
            """);
        PluginManifest.TryLoad(directory, out _, out var error).Should().BeFalse();
        error.Should().Contain("reserved");
    }

    [Fact]
    public void AnMcpDataEntrypointWithACustomFileNameIsCollected()
    {
        // The string form of an entrypoint (ADR 0033) is a data entry point: the file
        // name and location are the plugin's choice, and the kernel collects it instead
        // of launching anything.
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/mcp-data", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "mcp": "config/servers.json" } }
            """);
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        File.WriteAllText(
            Path.Combine(directory, "config", "servers.json"),
            """
            {
              "mcpServers": {
                "probe": { "command": "deno", "args": ["run", "server.ts"] },
                "remote": { "type": "http", "url": "https://example.test/mcp" }
              }
            }
            """);

        if (!PluginManifest.TryLoad(directory, out var descriptor, out var error))
            throw new InvalidOperationException($"Failed to load plugin manifest: {error}");
        descriptor.Workers.Should().BeEmpty();

        var entry = descriptor.DataEntries.Should().ContainSingle().Which;
        entry.Name.Should().Be("mcp");
        entry.Error.Should().BeNull();
        entry.Data.Should().NotBeNull();

        var servers = descriptor.McpServers;
        servers.Should().HaveCount(2);
        var stdio = servers.Should().ContainSingle(s => s.Id == $"plugin:{Path.GetFileName(directory)}::probe").Subject;
        stdio.Transport.Should().BeOfType<StdioMcpTransportDefinition>();
        stdio.RootsEnabled.Should().BeTrue();
        var http = servers.Should().ContainSingle(s => s.Id.EndsWith("::remote")).Subject;
        http.Transport.Should().BeOfType<HttpMcpTransportDefinition>();
        http.RootsEnabled.Should().BeFalse();
    }

    [Fact]
    public void AFlatWorkerDeclarationFailsWithAMigrationHint()
    {
        // Pre-hierarchy manifests declared workers directly under entrypoints; the
        // failure must say exactly where worker declarations live now.
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/flat", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "main": ["./mod.ts"] } }
            """);
        PluginManifest.TryLoad(directory, out _, out var error).Should().BeFalse();
        error.Should().Contain("Unknown entrypoint section 'main'").And.Contain("'worker'");
    }

    [Fact]
    public void AWorkerEntrypointMustBeAScriptArray()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/worker-string", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "worker": { "main": "./mod.ts" } } }
            """);
        PluginManifest.TryLoad(directory, out _, out var error).Should().BeFalse();
        error.Should().Contain("worker 'main'").And.Contain("script array");
    }

    [Fact]
    public void AnUndeclaredMcpJsonFileIsIgnored()
    {
        // There is no implicit file pickup (ADR 0033): a plugin must declare its data
        // entrypoint. A stray mcp.json without a declaration is dead weight, even a
        // broken one — it contributes nothing and marks nothing.
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/mcp-data", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "capabilities": ["tools.invoke"] }
            """);
        File.WriteAllText(Path.Combine(directory, "mcp.json"), "{ not even json");

        if (!PluginManifest.TryLoad(directory, out var descriptor, out var error))
            throw new InvalidOperationException($"Failed to load plugin manifest: {error}");
        descriptor.DataEntries.Should().BeEmpty();
        descriptor.McpServers.Should().BeEmpty();
        descriptor.McpServersError.Should().BeNull();
    }

    [Fact]
    public void AnArrayValueForACataloguedDataNameFailsTheManifest()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/mcp-array", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "mcp": ["deno"] } }
            """);
        PluginManifest.TryLoad(directory, out _, out var error).Should().BeFalse();
        error.Should().Contain("catalogued data entry").And.Contain("string path");
    }

    [Fact]
    public void ADataEntrypointOutsideTheRootCarriesTheError()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/mcp-escape", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "mcp": "../escape.json" } }
            """);

        if (!PluginManifest.TryLoad(directory, out var descriptor, out var error))
            throw new InvalidOperationException($"Failed to load plugin manifest: {error}");
        descriptor.McpServers.Should().BeEmpty();
        descriptor.McpServersError.Should().Contain("outside the plugin root");
    }

    [Fact]
    public void AMissingDataEntrypointFileCarriesTheError()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/mcp-missing", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "mcp": "missing.json" } }
            """);

        if (!PluginManifest.TryLoad(directory, out var descriptor, out var error))
            throw new InvalidOperationException($"Failed to load plugin manifest: {error}");
        descriptor.McpServersError.Should().Contain("does not exist");
    }

    [Fact]
    public void AnUnknownDataEntrypointNameIsCollectedButInert()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/data-catalog", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "catalog": "./data/catalog.json" } }
            """);
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        File.WriteAllText(
            Path.Combine(directory, "data", "catalog.json"),
            """{ "items": [1, 2, 3] }""");

        if (!PluginManifest.TryLoad(directory, out var descriptor, out var error))
            throw new InvalidOperationException($"Failed to load plugin manifest: {error}");
        var entry = descriptor.DataEntries.Should().ContainSingle().Which;
        entry.Name.Should().Be("catalog");
        entry.Data.Should().NotBeNull();
        descriptor.ExtensionDiagnostics.Should()
            .Contain(d => d.Contains("data entry 'catalog'") && d.Contains("inert"));
        descriptor.McpServers.Should().BeEmpty();
    }

    [Fact]
    public void AStrayConventionalFileNextToADeclarationIsIgnored()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/mcp-both", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "mcp": "servers.json" } }
            """);
        File.WriteAllText(
            Path.Combine(directory, "servers.json"),
            """{ "mcpServers": { "declared": { "command": "deno" } } }""");
        File.WriteAllText(
            Path.Combine(directory, "mcp.json"),
            """{ "mcpServers": { "conventional": { "command": "deno" } } }""");

        if (!PluginManifest.TryLoad(directory, out var descriptor, out var error))
            throw new InvalidOperationException($"Failed to load plugin manifest: {error}");
        descriptor.McpServers.Should().ContainSingle().Which.Id.Should().EndWith("::declared");
    }

    [Fact]
    public void DisabledDataFileServersAreSkipped()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/mcp-off", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "mcp": "servers.json" } }
            """);
        File.WriteAllText(
            Path.Combine(directory, "servers.json"),
            """
            {
              "mcpServers": {
                "off": { "enabled": false, "command": "deno" },
                "on": { "command": "deno" }
              }
            }
            """);

        if (!PluginManifest.TryLoad(directory, out var descriptor, out var error))
            throw new InvalidOperationException($"Failed to load plugin manifest: {error}");
        descriptor.McpServers.Should().ContainSingle().Which.Id.Should().EndWith("::on");
    }

    [Fact]
    public void AnInvalidDataEntrypointFileCarriesTheError()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/mcp-broken", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "mcp": "servers.json" } }
            """);
        File.WriteAllText(Path.Combine(directory, "servers.json"), "{");

        // The broken data file must not take the manifest's own declarations down:
        // the plugin loads, carries no servers, and reports the parse failure.
        if (!PluginManifest.TryLoad(directory, out var descriptor, out var error))
            throw new InvalidOperationException($"Failed to load plugin manifest: {error}");
        descriptor.McpServers.Should().BeEmpty();
        descriptor.McpServersError.Should().Contain("Invalid JSON in 'servers.json'");
    }

    [Fact]
    public void ManifestTriggersJoinTheDescriptor()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/triggers", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            {
              "entrypoints": { "worker": { "main": ["./mod.ts"] } },
              "triggers": [
                { "name": "idea-port", "kind": "watch",
                  "paths": ["${env.MAIEUTICS_TEST_HOME}/Library/Caches/JetBrains/**"],
                  "depth": 2, "action": { "type": "rediscover" } },
                { "name": "nightly", "kind": "cron", "expression": "0 3 * * *", "action": "event" },
                { "name": "poll", "kind": "interval", "seconds": 300 }
              ]
            }
            """);
        Environment.SetEnvironmentVariable("MAIEUTICS_TEST_HOME", "/Users/tester");
        try
        {
            if (!PluginManifest.TryLoad(
                    directory,
                    new Maieutics.Permissions.VariableTable(new ManifestVariableSource()),
                    out var descriptor,
                    out var error))
                throw new InvalidOperationException($"Failed to load plugin manifest: {error}");

            descriptor.Triggers.Should().HaveCount(3);

            var watch = descriptor.Triggers.Should().ContainSingle(t => t.Name == "idea-port").Subject;
            watch.Kind.Should().Be("watch");
            watch.Action.Should().Be(PluginTriggerAction.Rediscover);
            // Watch paths are kernel-expanded through the permission variable table.
            watch.WatchPaths.Should().Equal("/Users/tester/Library/Caches/JetBrains/**");
            watch.Depth.Should().Be(2);

            var cron = descriptor.Triggers.Should().ContainSingle(t => t.Name == "nightly").Subject;
            cron.Kind.Should().Be("cron");
            cron.Action.Should().Be(PluginTriggerAction.Event);
            cron.CronExpression.Should().Be("0 3 * * *");

            var interval = descriptor.Triggers.Should().ContainSingle(t => t.Name == "poll").Subject;
            interval.Kind.Should().Be("interval");
            interval.Action.Should().Be(PluginTriggerAction.Event);
            interval.IntervalSeconds.Should().Be(300);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIEUTICS_TEST_HOME", null);
        }
    }

    [Fact]
    public void MalformedTriggersFailThePluginLoad()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/broken-triggers", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "triggers": [{ "name": "bad", "kind": " Psychic", "action": "event" }] }
            """);
        PluginManifest.TryLoad(directory, out _, out var error).Should().BeFalse();
        error.Should().Contain("unknown kind");

        var directory2 = CreatePluginDirectory(
            """
            { "name": "@maieutics/bad-cron", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "triggers": [{ "name": "bad", "kind": "cron", "expression": "0 3 * *" }] }
            """);
        PluginManifest.TryLoad(directory2, out _, out var error2).Should().BeFalse();
        error2.Should().Contain("5 fields");
    }

    private sealed class ManifestVariableSource : Maieutics.Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name) =>
            name == "env.MAIEUTICS_TEST_HOME" ? "/Users/tester" : null;
    }

    [Fact]
    public void APluginWithoutAnMcpDataFileCarriesNoServers()
    {
        var descriptor = LoadPlugin(
            """
            { "name": "@maieutics/no-data", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "capabilities": ["tools.invoke"] }
            """);
        descriptor.McpServers.Should().BeEmpty();
    }

    [Fact]
    public void ExtensionKindRoutingIsCatalogDrivenAndRecordsCanonicalSpellings()
    {
        var descriptor = LoadPlugin(
            """
            { "name": "@maieutics/case-kinds", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            {
              "extensions": {
                "SKILLS": [ { "roots": ["./skills"] } ],
                "mcpdiscover": [
                  { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } }
                ]
              }
            }
            """);

        // Any spelling routes through the kind catalog; entries record the canonical
        // spelling every downstream comparison expects (manifest section order is kept).
        descriptor.Extensions.Select(static entry => entry.Kind).Should().Equal(
            SkillsContributionKind.ExtensionKind, McpDiscoverContributionKind.ExtensionKind);
    }

    [Fact]
    public void MisspelledDataEntryNamesStayDeclaredVerbatimAndInert()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/case-data", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            {
              "capabilities": ["tools.invoke"],
              "entrypoints": { "MCP": "./mcp.json", "UI": "./ui-form.json" }
            }
            """);
        File.WriteAllText(
            Path.Combine(directory, "mcp.json"),
            """{ "mcpServers": { "probe": { "command": "deno", "args": ["info"] } } }""");
        File.WriteAllText(Path.Combine(directory, "ui-form.json"), """{ "fields": [] }""");

        PluginManifest.TryLoad(directory, out var descriptor, out var error)
            .Should().BeTrue(error);
        var loaded = descriptor ?? throw new InvalidOperationException(error);

        // Data-entry names route exactly and record the declared spelling verbatim
        // (ADR 0040 decision 7: canonicalizing a misspelled known name would flip its
        // data-domain fingerprint bytes — revoking an existing approval — while
        // silently activating its formerly inert interpretation). A misspelled known
        // name is unknown to the grammar: collected but inert, with the catalog
        // diagnostic, and it interprets nothing.
        loaded.DataEntries.Select(static entry => entry.Name).Should().Equal("MCP", "UI");
        loaded.McpServers.Should().BeEmpty();
        loaded.UiForm.Should().BeNull();
        loaded.ExtensionDiagnostics.Should().Contain(static diagnostic =>
            diagnostic.Contains("data entry 'MCP'", StringComparison.Ordinal) &&
            diagnostic.Contains(KnownDataEntryNamesText()));
        loaded.ExtensionDiagnostics.Should().Contain(static diagnostic =>
            diagnostic.Contains("data entry 'UI'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownDataEntryDiagnosticsEnumerateEveryCataloguedName()
    {
        var descriptor = LoadPlugin(
            """
            { "name": "@maieutics/unknown-data", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "notices": "./notices.json" } }
            """);

        // The catalog generates the name list, so a catalogued kind can no longer be
        // missing from it (the former text listed only 'mcp' while 'ui' existed).
        descriptor.ExtensionDiagnostics.Should().Contain(diagnostic =>
            diagnostic.Contains("data entry 'notices'") &&
            diagnostic.Contains(KnownDataEntryNamesText()));
        KnownDataEntryNamesText().Should().Be("mcp, ui");
    }

    [Fact]
    public void UnknownExtensionKindDiagnosticsAreCatalogGenerated()
    {
        var descriptor = LoadPlugin(
            """
            { "name": "@maieutics/unknown-kind", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "extensions": { "FutureKind": [{ "opaque": true }] } }
            """);

        var diagnostic = descriptor.ExtensionDiagnostics.Should().ContainSingle().Which;
        diagnostic.Should().Contain("FutureKind")
            .And.Contain($"known kinds: {McpDiscoverContributionKind.ExtensionKind}, {SkillsContributionKind.ExtensionKind}");
        diagnostic.Should().NotContain("recommended", "the catalog text drops the former hand-written case-advice sentence");

        // The known-kinds list is the extensions grammar's participation set, ending
        // exactly at it: data-entry-only kinds (whose names are not extensions kinds)
        // never join, while any newly catalogued extensions kind joins by claiming its
        // name — with or without a compute form.
        diagnostic.Should().EndWith(
            $"(known kinds: {McpDiscoverContributionKind.ExtensionKind}, {SkillsContributionKind.ExtensionKind}).");
    }

    [Fact]
    public void TheKindCatalogRoutesLookupsByTheirDeclaredNames()
    {
        ContributionKindCatalog.ByExtensionKind("skills").Should().Be(SkillsContributionKind.Instance);
        ContributionKindCatalog.ByExtensionKind("MCPDISCOVER").Should().Be(McpDiscoverContributionKind.Instance);
        ContributionKindCatalog.ByExtensionKind("FutureKind").Should().BeNull();

        // Data-entry names are exact-match (the extensions kinds' case-insensitivity
        // is not shared): a misspelled known name is unknown, keeping its recorded
        // spelling — and its fingerprint bytes — exactly as declared.
        ContributionKindCatalog.ByDataEntryName("MCP").Should().BeNull();
        ContributionKindCatalog.ByDataEntryName("mcp").Should().Be(McpDiscoverContributionKind.Instance);
        ContributionKindCatalog.ByDataEntryName("ui").Should().Be(UiContributionKind.Instance);
        ContributionKindCatalog.ByDataEntryName("notices").Should().BeNull();
        ContributionKindCatalog.ByPublishCapability("skills.publish").Should().Be(SkillsContributionKind.Instance);
        ContributionKindCatalog.ByPublishCapability("ui.models").Should().BeNull(
            "the ui kind is a text-only catalog registration in B; its publish flow is untouched");
        ContributionKindCatalog.CanonicalExtensionKind("SKILLS").Should().Be(SkillsContributionKind.ExtensionKind);
        ContributionKindCatalog.CanonicalDataEntryName("MCP").Should().BeNull();
        ContributionKindCatalog.CanonicalDataEntryName("mcp").Should().Be(McpDiscoverContributionKind.DataEntry);
    }

    private static string KnownDataEntryNamesText() =>
        string.Join(", ", McpDiscoverContributionKind.DataEntry, UiContributionKind.DataEntry);

    // —— C 期可配置性对等（framework §5）——

    private static VariableTable TestVariables() =>
        new(
            new ManifestVariableSource(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            name => name == "MAIEUTICS_TEST_SUB" ? "config" : null);

    [Fact]
    public void ADataPathInterpolatesThroughTheVariableTable()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/interp-data", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            {
              "capabilities": ["tools.invoke"],
              "entrypoints": { "mcp": "${env.MAIEUTICS_TEST_SUB}/mcp.json" }
            }
            """);
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        File.WriteAllText(
            Path.Combine(directory, "config", "mcp.json"),
            """{ "mcpServers": { "probe": { "command": "deno", "args": ["info"] } } }""");
        var variables = new VariableTable(
            new ManifestVariableSource(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            name => name == "MAIEUTICS_TEST_SUB" ? "config" : null);

        PluginManifest.TryLoad(directory, variables, out var descriptor, out var error)
            .Should().BeTrue(error);
        var loaded = descriptor ?? throw new InvalidOperationException(error);
        loaded.McpServers.Should().ContainSingle("the expanded path collects from inside the plugin root");
        loaded.McpServersError.Should().BeNull();
    }

    [Fact]
    public void AnExpandingDataPathEscapingTheRootDegradesToTheErrorMarker()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/interp-escape", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "mcp": "${var.plugins}/outside.json" } }
            """);

        // The expanded path lands outside the plugin root: the containment check
        // runs after interpolation and degrades to the sticky error marker (the
        // coordinator's last-good path), never a load failure.
        var variables = new VariableTable(
            new ManifestVariableSource(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["plugins"] = "/maieutics/plugins" });
        PluginManifest.TryLoad(directory, variables, out var descriptor, out var error)
            .Should().BeTrue(error);
        var loaded = descriptor ?? throw new InvalidOperationException(error);
        var entry = loaded.DataEntries.Should().ContainSingle().Which;
        entry.Error.Should().NotBeNull().And.Contain("resolves outside the plugin root");
        loaded.McpServers.Should().BeEmpty();
    }

    [Fact]
    public void AnUnresolvableDataPathVariableDegradesToTheErrorMarker()
    {
        var directory = CreatePluginDirectory(
            """
            { "name": "@maieutics/interp-unknown", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "entrypoints": { "mcp": "${env.MAIEUTICS_NO_SUCH_VAR}/mcp.json" } }
            """);

        PluginManifest.TryLoad(directory, TestVariables(), out var descriptor, out var error)
            .Should().BeTrue(error);
        var loaded = descriptor ?? throw new InvalidOperationException(error);
        var entry = loaded.DataEntries.Should().ContainSingle().Which;
        entry.Error.Should().NotBeNull().And.Contain("cannot be expanded");
    }

    [Fact]
    public void AnEntryWithoutOptInMembersInterpretsByteIdenticallyToTheFixedValueEra()
    {
        var entry = JsonDocument.Parse(
            """
            {
              "module": "npm:@maieutics/probe-server",
              "transport": { "type": "stdio", "command": "deno", "args": ["serve"] }
            }
            """).RootElement;

        McpDiscoverContributionKind.TryToDefinition("plugin-a", entry, TestVariables(), out var definition)
            .Should().BeTrue();

        // The default path equals the former hardcoded values exactly: the metadata
        // defaults hash byte-for-byte identically (framework §5 timeout parity).
        var stdio = new StdioMcpTransportDefinition("deno", ["serve"], null, null);
        var expectedKey = McpServerDefinition.CreateGenerationKey(
            stdio,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            true,
            true);
        definition.Should().NotBeNull();
        definition.Id.Should().Be("plugin:plugin-a::npm:@maieutics/probe-server");
        definition.GenerationKey.Should().Be(expectedKey);
        definition.InitializationTimeout.Should().Be(TimeSpan.FromSeconds(30));
        definition.RequestTimeout.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void AnOptInInterpolatedEntryExpandsItsTransportThroughTheVariableTable()
    {
        var entry = JsonDocument.Parse(
            """
            {
              "module": "${env.MAIEUTICS_TEST_MODULE}",
              "transport": { "type": "stdio", "command": "${env.MAIEUTICS_TEST_SUB}/deno" },
              "interpolate": true
            }
            """).RootElement;
        var variables = new VariableTable(
            new ManifestVariableSource(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            name => name == "MAIEUTICS_TEST_SUB" ? "tools" : null);

        McpDiscoverContributionKind.TryToDefinition("plugin-a", entry, variables, out var definition)
            .Should().BeTrue();

        definition.Should().NotBeNull();
        var stdio = definition.Transport.Should().BeOfType<StdioMcpTransportDefinition>().Which;
        stdio.Command.Should().Be("tools/deno", "the transport's strings expand through the unified table");
        definition.Id.Should().Be("plugin:plugin-a::${env.MAIEUTICS_TEST_MODULE}",
            "the module slug is never interpolated");

        // The expanded command enters the generation key: a different environment
        // yields a different key and the coordinator rebuilds the generation (the
        // rebuild itself is the existing differential behavior covered by
        // PluginMcpCoordinatorTests).
        var expandedKey = McpServerDefinition.CreateGenerationKey(
            new StdioMcpTransportDefinition("tools/deno", null, null, null),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            true,
            true);
        definition.GenerationKey.Should().Be(expandedKey);
    }

    [Fact]
    public void AnUnresolvableInterpolatedEntryFailsTheEntry()
    {
        var entry = JsonDocument.Parse(
            """
            {
              "module": "probe",
              "transport": { "type": "stdio", "command": "${env.MAIEUTICS_NO_SUCH_VAR}/deno" },
              "interpolate": true
            }
            """).RootElement;

        McpDiscoverContributionKind.TryToDefinition("plugin-a", entry, TestVariables(), out var definition)
            .Should().BeFalse("an unresolvable variable fails the entry and the discovery branch degrades to its sticky path");
        definition.Should().BeNull();
    }

    [Fact]
    public void TimeoutOverridesChangeTheGenerationKeyWhileDefaultsKeepIt()
    {
        var baseEntry = JsonDocument.Parse(
            """
            { "module": "probe", "transport": { "type": "stdio", "command": "deno" } }
            """).RootElement;
        var overriddenEntry = JsonDocument.Parse(
            """
            {
              "module": "probe",
              "transport": { "type": "stdio", "command": "deno" },
              "timeouts": { "request": "00:05:00" }
            }
            """).RootElement;
        var invalidEntry = JsonDocument.Parse(
            """
            {
              "module": "probe",
              "transport": { "type": "stdio", "command": "deno" },
              "timeouts": { "unknown": "00:05:00" }
            }
            """).RootElement;

        McpDiscoverContributionKind.TryToDefinition("p", baseEntry, null, out var baseDefinition).Should().BeTrue();
        McpDiscoverContributionKind.TryToDefinition("p", overriddenEntry, null, out var overridden).Should().BeTrue();
        var baseLoaded = baseDefinition ?? throw new InvalidOperationException("the base entry must interpret");
        var overrideLoaded = overridden ?? throw new InvalidOperationException("the overridden entry must interpret");

        // Same server id, different generation key: exactly the input the
        // coordinator's generation-replacement check compares.
        overrideLoaded.Id.Should().Be(baseLoaded.Id);
        overrideLoaded.GenerationKey.Should().NotBe(baseLoaded.GenerationKey);
        overrideLoaded.RequestTimeout.Should().Be(TimeSpan.FromMinutes(5));
        overrideLoaded.InitializationTimeout.Should().Be(
            TimeSpan.FromSeconds(30), "untouched members keep the metadata defaults");

        McpDiscoverContributionKind.TryToDefinition("p", invalidEntry, null, out _)
            .Should().BeFalse("an unknown timeout name is a strict-grammar violation");
    }

    [Fact]
    public void ANonObjectTimeoutsMemberFailsTheEntryLikeItsInnerViolations()
    {
        var stringEntry = JsonDocument.Parse(
            """
            {
              "module": "probe",
              "transport": { "type": "stdio", "command": "deno" },
              "timeouts": "00:05:00"
            }
            """).RootElement;
        var arrayEntry = JsonDocument.Parse(
            """
            {
              "module": "probe",
              "transport": { "type": "stdio", "command": "deno" },
              "timeouts": [{ "request": "00:05:00" }]
            }
            """).RootElement;

        // The member's strict grammar covers its own shape: a present-but-non-object
        // member fails the entry instead of silently interpreting it on the default
        // timeouts (which would hide the declaration's real intent).
        McpDiscoverContributionKind.TryToDefinition("p", stringEntry, null, out _)
            .Should().BeFalse("a string timeouts member is a strict-grammar violation");
        McpDiscoverContributionKind.TryToDefinition("p", arrayEntry, null, out _)
            .Should().BeFalse("an array timeouts member is a strict-grammar violation");
    }

    [Fact]
    public void TheKindMetadataTablesCarryTheParityKnobs()
    {
        var skills = SkillsContributionKind.Instance.Metadata;
        skills.MaxDeclaredEntriesPerDeclaration.Should().Be(32);
        skills.MaxComputedEntries.Should().Be(256);
        skills.StickyPerSourceKey.Should().BeTrue();
        skills.UsesKernelRetrySet.Should().BeTrue("never-succeeded-stays-new rides the kernel retry set");
        skills.DefaultTimeouts.Should().BeNull();
        skills.DeclarationInterpolation.Should().BeTrue("skills roots expand through the variable table");

        var mcp = McpDiscoverContributionKind.Instance.Metadata;
        mcp.MaxDeclaredEntriesPerDeclaration.Should().BeNull("the extensions entry count stays unbounded today");
        mcp.MaxComputedEntries.Should().BeNull("the worker-discovery array stays unbounded today");
        mcp.StickyPerSourceKey.Should().BeTrue();
        mcp.UsesKernelRetrySet.Should().BeFalse("MCP retries inside its revision engine");
        // The defaults are the former fixed values byte-for-byte.
        mcp.DefaultTimeouts.Should().Equal(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30));
        mcp.DeclarationInterpolation.Should().BeTrue("the data path joins the interpolation parity");
    }

    private static PluginDescriptor LoadPlugin(string denoJson, string? maieuticsJson)
    {
        if (PluginManifest.TryLoad(
                CreatePluginDirectory(denoJson, maieuticsJson),
                out var descriptor,
                out var error))
            return descriptor;

        throw new InvalidOperationException($"Failed to load plugin manifest: {error}");
    }

    private static string CreatePluginDirectory(string denoJson, string? maieuticsJson)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mc-plugin-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        if (maieuticsJson is not null)
            File.WriteAllText(Path.Combine(directory, "maieutics.json"), maieuticsJson);
        File.WriteAllText(Path.Combine(directory, "deno.json"), denoJson);
        return directory;
    }
}
