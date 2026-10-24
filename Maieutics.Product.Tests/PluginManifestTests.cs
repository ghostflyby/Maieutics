using System.Text.Json;
using Maieutics.Mcp;
using Maieutics.Permissions;
using Maieutics.Plugins;
using Maieutics.Plugins.Contributions;

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

        Assert.Equal("@maieutics/example", descriptor.Name);
        Assert.Equal("auto", descriptor.Isolation);
        Assert.Equal(["base"], descriptor.Dependencies);
        // Two entrypoints → two workers; the exports "./mcp" does not create a
        // worker on its own (exports only expose the API surface).
        Assert.Equal(2, descriptor.Workers.Count());
        Assert.Equal("main", descriptor.Workers[0].ExportName);
        Assert.StartsWith("file://", descriptor.Workers[0].EntryUrl);
        Assert.EndsWith("/mod.ts", descriptor.Workers[0].EntryUrl);
        Assert.Equal("echo", descriptor.Workers[1].ExportName);
        Assert.EndsWith("/src/echo.ts", descriptor.Workers[1].EntryUrl);
        Assert.False(descriptor.Permissions.Read.AllowAll);
        Assert.Equal(["./"], descriptor.Permissions.Read.Values);
        Assert.Empty(descriptor.Permissions.Net.Values);
        Assert.True(descriptor.Permissions.Write.AllowAll);
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

        Assert.False(PluginManifest.TryLoad(directory, out _, out var error));
        Assert.Contains("maieutics.json", error);
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

        Assert.Empty(descriptor.Workers);
    }

    [Fact]
    public void ReportsInvalidMaieuticsJsonAsFailure()
    {
        var directory = CreatePluginDirectory("{ not json", "{ not json");

        Assert.False(PluginManifest.TryLoad(directory, out _, out var error));
        Assert.Contains("maieutics.json", error);
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

        Assert.False(descriptor.Permissions.Read.AllowAll);
        Assert.Equal(["./"], descriptor.Permissions.Read.Values);
        Assert.False(descriptor.Permissions.Net.AllowAll);
        Assert.Empty(descriptor.Permissions.Net.Values);
        Assert.False(descriptor.Permissions.Env.AllowAll);
        Assert.Empty(descriptor.Permissions.Env.Values);
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

        var item = Assert.Single(descriptor.Workers);
        Assert.Equal("ok", item.ExportName);
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
        var item = Assert.Single(targets);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "local-dep", "mod.ts")), item);
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

        var item = Assert.Single(descriptor.Extensions);
        Assert.Equal("McpDiscover", item.Kind);
        Assert.Equal("npm:@maieutics/probe-server", descriptor.Extensions[0].Data.GetProperty("module")
            .GetString());
        Assert.Empty(descriptor.ExtensionDiagnostics);
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
        Assert.Equal("McpDiscover", Assert.Single(descriptor.Extensions).Kind);
        Assert.Contains("FutureKind", Assert.Single(descriptor.ExtensionDiagnostics));
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
        Assert.False(PluginManifest.TryLoad(directory, out _, out var error));
        Assert.Contains("extensions", error);

        var directory2 = CreatePluginDirectory(
            """
            { "name": "@maieutics/broken", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "extensions": { "McpDiscover": ["not-an-object"] } }
            """);
        Assert.False(PluginManifest.TryLoad(directory2, out _, out var error2));
        Assert.Contains("entries must be objects", error2);
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
        Assert.False(PluginManifest.TryLoad(directory, out _, out var error));
        Assert.Contains("reserved", error);
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
        Assert.Empty(descriptor.Workers);

        var entry = Assert.Single(descriptor.DataEntries);
        Assert.Equal("mcp", entry.Name);
        Assert.Null(entry.Error);
        Assert.NotNull(entry.Data);

        var servers = descriptor.McpServers;
        Assert.Equal(2, servers.Count());
        var stdio = Assert.Single(servers, s => s.Id == $"plugin:{Path.GetFileName(directory)}::probe");
        Assert.IsType<StdioMcpTransportDefinition>(stdio.Transport);
        Assert.True(stdio.RootsEnabled);
        var http = Assert.Single(servers, s => s.Id.EndsWith("::remote"));
        Assert.IsType<HttpMcpTransportDefinition>(http.Transport);
        Assert.False(http.RootsEnabled);
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
        Assert.False(PluginManifest.TryLoad(directory, out _, out var error));
        Assert.Contains("Unknown entrypoint section 'main'", error);
        Assert.Contains("'worker'", error);
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
        Assert.False(PluginManifest.TryLoad(directory, out _, out var error));
        Assert.Contains("worker 'main'", error);
        Assert.Contains("script array", error);
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
        Assert.Empty(descriptor.DataEntries);
        Assert.Empty(descriptor.McpServers);
        Assert.Null(descriptor.McpServersError);
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
        Assert.False(PluginManifest.TryLoad(directory, out _, out var error));
        Assert.Contains("catalogued data entry", error);
        Assert.Contains("string path", error);
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
        Assert.Empty(descriptor.McpServers);
        Assert.Contains("outside the plugin root", descriptor.McpServersError);
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
        Assert.Contains("does not exist", descriptor.McpServersError);
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
        var entry = Assert.Single(descriptor.DataEntries);
        Assert.Equal("catalog", entry.Name);
        Assert.NotNull(entry.Data);
        Assert.Contains(descriptor.ExtensionDiagnostics, d => d.Contains("data entry 'catalog'") && d.Contains("inert"));
        Assert.Empty(descriptor.McpServers);
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
        Assert.EndsWith("::declared", Assert.Single(descriptor.McpServers).Id);
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
        Assert.EndsWith("::on", Assert.Single(descriptor.McpServers).Id);
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
        Assert.Empty(descriptor.McpServers);
        Assert.Contains("Invalid JSON in 'servers.json'", descriptor.McpServersError);
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
                    new VariableTable(new ManifestVariableSource()),
                    out var descriptor,
                    out var error))
                throw new InvalidOperationException($"Failed to load plugin manifest: {error}");

            Assert.Equal(3, descriptor.Triggers.Count());

            var watch = Assert.Single(descriptor.Triggers, t => t.Name == "idea-port");
            Assert.Equal("watch", watch.Kind);
            Assert.Equal(PluginTriggerAction.Rediscover, watch.Action);
            // Watch paths are kernel-expanded through the permission variable table.
            Assert.Equal(["/Users/tester/Library/Caches/JetBrains/**"], watch.WatchPaths);
            Assert.Equal(2, watch.Depth);

            var cron = Assert.Single(descriptor.Triggers, t => t.Name == "nightly");
            Assert.Equal("cron", cron.Kind);
            Assert.Equal(PluginTriggerAction.Event, cron.Action);
            Assert.Equal("0 3 * * *", cron.CronExpression);

            var interval = Assert.Single(descriptor.Triggers, t => t.Name == "poll");
            Assert.Equal("interval", interval.Kind);
            Assert.Equal(PluginTriggerAction.Event, interval.Action);
            Assert.Equal(300, interval.IntervalSeconds);
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
        Assert.False(PluginManifest.TryLoad(directory, out _, out var error));
        Assert.Contains("unknown kind", error);

        var directory2 = CreatePluginDirectory(
            """
            { "name": "@maieutics/bad-cron", "permissions": { "default": { "read": ["./"] } } }
            """,
            """
            { "triggers": [{ "name": "bad", "kind": "cron", "expression": "0 3 * *" }] }
            """);
        Assert.False(PluginManifest.TryLoad(directory2, out _, out var error2));
        Assert.Contains("5 fields", error2);
    }

    private sealed class ManifestVariableSource : Execution.IPermissionVariableSource
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
        Assert.Empty(descriptor.McpServers);
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
        Assert.Equal([SkillsContributionKind.ExtensionKind, McpDiscoverContributionKind.ExtensionKind], descriptor.Extensions.Select(static entry => entry.Kind));
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

        Assert.True(PluginManifest.TryLoad(directory, out var descriptor, out var error), error);
        var loaded = descriptor ?? throw new InvalidOperationException(error);

        // Data-entry names route exactly and record the declared spelling verbatim
        // (ADR 0040 decision 7: canonicalizing a misspelled known name would flip its
        // data-domain fingerprint bytes — revoking an existing approval — while
        // silently activating its formerly inert interpretation). A misspelled known
        // name is unknown to the grammar: collected but inert, with the catalog
        // diagnostic, and it interprets nothing.
        Assert.Equal(["MCP", "UI"], loaded.DataEntries.Select(static entry => entry.Name));
        Assert.Empty(loaded.McpServers);
        Assert.Null(loaded.UiForm);
        Assert.Contains(loaded.ExtensionDiagnostics, static diagnostic =>
            diagnostic.Contains("data entry 'MCP'", StringComparison.Ordinal) &&
            diagnostic.Contains(KnownDataEntryNamesText()));
        Assert.Contains(loaded.ExtensionDiagnostics, static diagnostic =>
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
        Assert.Contains(descriptor.ExtensionDiagnostics, diagnostic =>
            diagnostic.Contains("data entry 'notices'") &&
            diagnostic.Contains(KnownDataEntryNamesText()));
        Assert.Equal("mcp, ui", KnownDataEntryNamesText());
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

        var diagnostic = Assert.Single(descriptor.ExtensionDiagnostics);
        Assert.Contains("FutureKind", diagnostic);
        Assert.Contains($"known kinds: {McpDiscoverContributionKind.ExtensionKind}, {SkillsContributionKind.ExtensionKind}", diagnostic);
        Assert.DoesNotContain("recommended", diagnostic);

        // The known-kinds list is the extensions grammar's participation set, ending
        // exactly at it: data-entry-only kinds (whose names are not extensions kinds)
        // never join, while any newly catalogued extensions kind joins by claiming its
        // name — with or without a compute form.
        Assert.EndsWith($"(known kinds: {McpDiscoverContributionKind.ExtensionKind}, {SkillsContributionKind.ExtensionKind}).", diagnostic);
    }

    [Fact]
    public void TheKindCatalogRoutesLookupsByTheirDeclaredNames()
    {
        Assert.Equal(SkillsContributionKind.Instance, ContributionKindCatalog.ByExtensionKind("skills"));
        Assert.Equal(McpDiscoverContributionKind.Instance, ContributionKindCatalog.ByExtensionKind("MCPDISCOVER"));
        Assert.Null(ContributionKindCatalog.ByExtensionKind("FutureKind"));

        // Data-entry names are exact-match (the extensions kinds' case-insensitivity
        // is not shared): a misspelled known name is unknown, keeping its recorded
        // spelling — and its fingerprint bytes — exactly as declared.
        Assert.Null(ContributionKindCatalog.ByDataEntryName("MCP"));
        Assert.Equal(McpDiscoverContributionKind.Instance, ContributionKindCatalog.ByDataEntryName("mcp"));
        Assert.Equal(UiContributionKind.Instance, ContributionKindCatalog.ByDataEntryName("ui"));
        Assert.Null(ContributionKindCatalog.ByDataEntryName("notices"));
        Assert.Equal(SkillsContributionKind.Instance, ContributionKindCatalog.ByPublishCapability("skills.publish"));
        Assert.Null(ContributionKindCatalog.ByPublishCapability("ui.models"));
        Assert.Equal(SkillsContributionKind.ExtensionKind, ContributionKindCatalog.CanonicalExtensionKind("SKILLS"));
        Assert.Null(ContributionKindCatalog.CanonicalDataEntryName("MCP"));
        Assert.Equal(McpDiscoverContributionKind.DataEntry, ContributionKindCatalog.CanonicalDataEntryName("mcp"));
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

        Assert.True(PluginManifest.TryLoad(directory, variables, out var descriptor, out var error), error);
        var loaded = descriptor ?? throw new InvalidOperationException(error);
        Assert.Single(loaded.McpServers);
        Assert.Null(loaded.McpServersError);
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
        Assert.True(PluginManifest.TryLoad(directory, variables, out var descriptor, out var error), error);
        var loaded = descriptor ?? throw new InvalidOperationException(error);
        var entry = Assert.Single(loaded.DataEntries);
        Assert.NotNull(entry.Error);
        Assert.Contains("resolves outside the plugin root", entry.Error);
        Assert.Empty(loaded.McpServers);
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

        Assert.True(PluginManifest.TryLoad(directory, TestVariables(), out var descriptor, out var error), error);
        var loaded = descriptor ?? throw new InvalidOperationException(error);
        var entry = Assert.Single(loaded.DataEntries);
        Assert.NotNull(entry.Error);
        Assert.Contains("cannot be expanded", entry.Error);
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

        Assert.True(McpDiscoverContributionKind.TryToDefinition("plugin-a", entry, TestVariables(), out var definition));

        // The default path equals the former hardcoded values exactly: the metadata
        // defaults hash byte-for-byte identically (framework §5 timeout parity).
        var stdio = new StdioMcpTransportDefinition("deno", ["serve"]);
        var expectedKey = McpServerDefinition.CreateGenerationKey(
            stdio,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            true,
            true);
        Assert.NotNull(definition);
        Assert.Equal("plugin:plugin-a::npm:@maieutics/probe-server", definition.Id);
        Assert.Equal(expectedKey, definition.GenerationKey);
        Assert.Equal(TimeSpan.FromSeconds(30), definition.InitializationTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), definition.RequestTimeout);
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

        Assert.True(McpDiscoverContributionKind.TryToDefinition("plugin-a", entry, variables, out var definition));

        Assert.NotNull(definition);
        var stdio = Assert.IsType<StdioMcpTransportDefinition>(definition.Transport);
        Assert.Equal("tools/deno", stdio.Command);
        Assert.Equal("plugin:plugin-a::${env.MAIEUTICS_TEST_MODULE}", definition.Id);

        // The expanded command enters the generation key: a different environment
        // yields a different key and the coordinator rebuilds the generation (the
        // rebuild itself is the existing differential behavior covered by
        // PluginMcpCoordinatorTests).
        var expandedKey = McpServerDefinition.CreateGenerationKey(
            new StdioMcpTransportDefinition("tools/deno"),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            true,
            true);
        Assert.Equal(expandedKey, definition.GenerationKey);
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

        Assert.False(McpDiscoverContributionKind.TryToDefinition("plugin-a", entry, TestVariables(), out var definition), "an unresolvable variable fails the entry and the discovery branch degrades to its sticky path");
        Assert.Null(definition);
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

        Assert.True(McpDiscoverContributionKind.TryToDefinition("p", baseEntry, null, out var baseDefinition));
        Assert.True(McpDiscoverContributionKind.TryToDefinition("p", overriddenEntry, null, out var overridden));
        var baseLoaded = baseDefinition ?? throw new InvalidOperationException("the base entry must interpret");
        var overrideLoaded = overridden ?? throw new InvalidOperationException("the overridden entry must interpret");

        // Same server id, different generation key: exactly the input the
        // coordinator's generation-replacement check compares.
        Assert.Equal(baseLoaded.Id, overrideLoaded.Id);
        Assert.NotEqual(baseLoaded.GenerationKey, overrideLoaded.GenerationKey);
        Assert.Equal(TimeSpan.FromMinutes(5), overrideLoaded.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), overrideLoaded.InitializationTimeout);

        Assert.False(McpDiscoverContributionKind.TryToDefinition("p", invalidEntry, null, out _), "an unknown timeout name is a strict-grammar violation");
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
        Assert.False(McpDiscoverContributionKind.TryToDefinition("p", stringEntry, null, out _), "a string timeouts member is a strict-grammar violation");
        Assert.False(McpDiscoverContributionKind.TryToDefinition("p", arrayEntry, null, out _), "an array timeouts member is a strict-grammar violation");
    }

    [Fact]
    public void TheKindMetadataTablesCarryTheParityKnobs()
    {
        var skills = SkillsContributionKind.Instance.Metadata;
        Assert.Equal(32, skills.MaxDeclaredEntriesPerDeclaration);
        Assert.Equal(256, skills.MaxComputedEntries);
        Assert.True(skills.StickyPerSourceKey);
        Assert.True(skills.UsesKernelRetrySet, "never-succeeded-stays-new rides the kernel retry set");
        Assert.Null(skills.DefaultTimeouts);
        Assert.True(skills.DeclarationInterpolation, "skills roots expand through the variable table");

        var mcp = McpDiscoverContributionKind.Instance.Metadata;
        Assert.Null(mcp.MaxDeclaredEntriesPerDeclaration);
        Assert.Null(mcp.MaxComputedEntries);
        Assert.True(mcp.StickyPerSourceKey);
        Assert.False(mcp.UsesKernelRetrySet, "MCP retries inside its revision engine");
        // The defaults are the former fixed values byte-for-byte.
        Assert.Equal([
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30),
        ], mcp.DefaultTimeouts);
        Assert.True(mcp.DeclarationInterpolation, "the data path joins the interpolation parity");
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
