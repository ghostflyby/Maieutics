using System.Text.Json;
using FluentAssertions;
using Maieutics.Permissions;
using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

/// <summary>Golden fingerprint fixtures (plugin-contribution framework §6-A): the
/// SHA-256 of a representative declaration surface, pinned byte-for-byte before the
/// framework migration touches any parsing path. The manifest deliberately exercises
/// every <see cref="PluginDeclarationFingerprint"/> domain and the two collection
/// invariants the domain enumeration depends on (§3.4 rule 4): an unknown extension
/// kind is dropped from <c>descriptor.Extensions</c>, while an unknown data-entry name
/// is still collected into <c>descriptor.DataEntries</c>. If any of these hashes move,
/// every persisted plugin approval is revoked (ADR 0037 decision 5) — the change is
/// wrong unless the migration explicitly argued and accepted the revocation.</summary>
public sealed class PluginDeclarationFingerprintGoldenTests
{
    private const string FullSurfaceFingerprint =
        "16AD4F2CE7A51C83ACFC27282BEC386FD19F301DE7A0B31BFCBD52CDC3CD6DA4";

    private const string MinimalSurfaceFingerprint =
        "C4E3E123B8397A74FFD8999B9055F582EB694887A2D41450F9F4631B2C9844F0";

    [Fact]
    public void RepresentativeDeclarationSurfaceHashesByteStable()
    {
        var root = CreateGoldenPluginRoot();
        try
        {
            PluginManifest.TryLoad(root, GoldenVariables(), out var descriptor, out var error)
                .Should().BeTrue(error);
            var loaded = descriptor ?? throw new InvalidOperationException(error);

            // The collection invariants beneath the extensions/data fingerprint
            // domains, pinned structurally so a silent inclusion flip cannot hide
            // behind an unchanged-looking hash.
            descriptor.Extensions.Select(static entry => entry.Kind).Should().Equal(
                PluginExtensionKind.McpDiscover, PluginExtensionKind.Skills);
            descriptor.ExtensionDiagnostics.Should().Contain(diagnostic =>
                diagnostic.Contains("unknown-extension", StringComparison.Ordinal));
            descriptor.DataEntries.Select(static entry => entry.Name).Should().BeEquivalentTo(
                ["mcp", "notices", "broken", "unresolved"],
                static options => options.WithoutStrictOrdering());
            descriptor.DataEntries.Single(static entry => entry.Name == "notices").Error
                .Should().BeNull("an unknown data name is still collected");
            descriptor.DataEntries.Single(static entry => entry.Name == "broken").Error
                .Should().NotBeNull();
            descriptor.DataEntries.Single(static entry => entry.Name == "unresolved").Error
                .Should().NotBeNull("a literal ${...} path resolves to no file today");
            descriptor.McpServersError.Should().BeNull();
            descriptor.McpServers.Should().ContainSingle();
            descriptor.Triggers.Should().HaveCount(3);
            descriptor.Triggers.Single(static trigger => trigger.Kind == "watch").WatchPaths
                .Single().Should().Contain("watched", "watch paths enter the fingerprint expanded");

            PluginDeclarationFingerprint.Compute(loaded).Should().Be(FullSurfaceFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FormattingOnlyManifestEditsKeepTheGoldenHash()
    {
        var root = CreateGoldenPluginRoot();
        try
        {
            PluginManifest.TryLoad(root, GoldenVariables(), out var descriptor, out var error)
                .Should().BeTrue(error);
            var golden = PluginDeclarationFingerprint.Compute(
                descriptor ?? throw new InvalidOperationException(error));

            // Reformat: different whitespace, reordered members, re-spelled numbers —
            // canonicalization must absorb every one of them.
            File.WriteAllText(
                Path.Combine(root, "maieutics.json"),
                """
                {"isolation":"auto",
                  "dependencies":["jsr:@maieutics/dep@1.2.0"],
                  "capabilities":["tools.invoke","content.read"],
                  "entrypoints":{
                    "unresolved":"${env.MAIEUTICS_NO_SUCH_VAR}/mcp-unresolved.json",
                    "worker":{"main":["./main.ts"],"side":["./side.ts"]},
                    "broken":"./no-such-file.json",
                    "notices":"./notices.json",
                    "mcp":"./mcp.json"},
                  "extensions":{
                    "unknown-extension":[{"whatever":1}],
                    "Skills":[{"roots":["./skills"]}],
                    "mcpdiscover":[{"module":"npm:@maieutics/probe-server","transport":{"type":"stdio","command":"deno"}}]},
                  "inspections":{"contentReadAll":true},
                  "triggers":[
                    {"name":"tick","kind":"interval","seconds":30,"action":"event"},
                    {"name":"on-change","kind":"watch","paths":["${var.plugins}/watched"],"depth":2,"action":"rediscover"},
                    {"name":"nightly","kind":"cron","expression":"0 3 * * *","action":"event"}]}
                """);

            PluginManifest.TryLoad(root, GoldenVariables(), out var reformatted, out error)
                .Should().BeTrue(error);
            PluginDeclarationFingerprint.Compute(
                reformatted ?? throw new InvalidOperationException(error)).Should().Be(golden);
            golden.Should().Be(FullSurfaceFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MinimalSkeletonHashesByteStable()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"maieutics-fingerprint-golden-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "maieutics.json"),
                """{ "isolation": "auto", "entrypoints": {} }""");

            PluginManifest.TryLoad(root, GoldenVariables(), out var descriptor, out var error)
                .Should().BeTrue(error);
            var minimal = descriptor ?? throw new InvalidOperationException(error);
            PluginDeclarationFingerprint.IsApprovalExempt(minimal).Should().BeTrue();
            PluginDeclarationFingerprint.Compute(minimal).Should().Be(MinimalSurfaceFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The representative plugin: permissions across grant kinds, two workers,
    /// a dependency, catalogued capabilities, all three declared extension kinds (one
    /// unknown), four data entries (mcp file, unknown name, missing file, literal
    /// <c>${...}</c> path), all three trigger kinds, and content observation.</summary>
    /// <summary>The plugin id rides the fingerprint (it stamps the MCP server ids), so
    /// the root's leaf name is fixed — no per-run Guid — and a leftover from a previous
    /// run is cleared first. Tests of this class run sequentially (xUnit), so the fixed
    /// name cannot self-collide.</summary>
    private static string CreateEmptyGoldenRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "maieutics-fingerprint-golden");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateGoldenPluginRoot()
    {
        var root = CreateEmptyGoldenRoot();
        Directory.CreateDirectory(Path.Combine(root, "skills", "alpha"));
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/golden-plugin",
              "permissions": {
                "default": {
                  "read": ["./", "./skills"],
                  "write": ["./data"],
                  "env": ["MAIEUTICS_GOLDEN_VAR"],
                  "net": ["example.com:443"]
                }
              }
            }
            """);
        File.WriteAllText(Path.Combine(root, "main.ts"), "export const main = 1;\n");
        File.WriteAllText(Path.Combine(root, "side.ts"), "export const side = 2;\n");
        File.WriteAllText(
            Path.Combine(root, "mcp.json"),
            """
            {
              "mcpServers": {
                "probe": {
                  "command": "deno",
                  "args": ["serve"],
                  "roots": true,
                  "elicitation": false
                }
              }
            }
            """);
        File.WriteAllText(Path.Combine(root, "notices.json"), """{"panel":"golden"}""");
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "isolation": "auto",
              "dependencies": ["jsr:@maieutics/dep@1.2.0"],
              "capabilities": ["tools.invoke", "content.read"],
              "entrypoints": {
                "worker": { "main": ["./main.ts"], "side": ["./side.ts"] },
                "mcp": "./mcp.json",
                "notices": "./notices.json",
                "broken": "./no-such-file.json",
                "unresolved": "${env.MAIEUTICS_NO_SUCH_VAR}/mcp-unresolved.json"
              },
              "extensions": {
                "McpDiscover": [
                  { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } }
                ],
                "Skills": [ { "roots": ["./skills"] } ],
                "unknown-extension": [ { "whatever": 1 } ]
              },
              "inspections": { "contentReadAll": true },
              "triggers": [
                { "name": "nightly", "kind": "cron", "expression": "0 3 * * *", "action": "event" },
                { "name": "on-change", "kind": "watch", "paths": ["${var.plugins}/watched"], "depth": 2, "action": "rediscover" },
                { "name": "tick", "kind": "interval", "seconds": 30, "action": "event" }
              ]
            }
            """);
        return root;
    }

    private static VariableTable GoldenVariables() =>
        new(
            new EmptyVariableSource(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["plugins"] = "/maieutics/plugins" },
            name => name == "MAIEUTICS_GOLDEN_VAR" ? "golden" : null);

    private sealed class EmptyVariableSource : Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name) => null;
    }
}
