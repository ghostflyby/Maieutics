using System.Text.Json;
using FluentAssertions;
using Maieutics.Permissions;
using Maieutics.Plugins;
using Maieutics.Plugins.Contributions;

namespace Maieutics.Product.Tests;

/// <summary>Golden fingerprint fixtures (plugin-contribution framework §6-A): the
/// SHA-256 of a representative declaration surface, pinned byte-for-byte before the
/// framework migration touches any parsing path. The manifest deliberately exercises
/// every <see cref="PluginDeclarationFingerprint"/> domain and the two collection
/// invariants the domain enumeration depends on (§3.4 rule 4): an unknown extension
/// kind is dropped from <c>descriptor.Extensions</c>, while an unknown data-entry name
/// is still collected into <c>descriptor.DataEntries</c>. If any of these hashes move,
/// every persisted plugin approval is revoked (ADR 0037 decision 5) — the change is
/// wrong unless the migration explicitly argued and accepted the revocation. One
/// input is machine-local and projected out before hashing (see
/// <see cref="WithStableWorkerUrls"/>): a worker's absolute EntryUrl.</summary>
public sealed class PluginDeclarationFingerprintGoldenTests
{
    /// <summary>The full surface WITHOUT a <c>${...}</c> data path: byte-stable across
    /// the C 期 interpolation change (paths without tokens collect identically). The
    /// workers domain hashes the projected (root-sentinel) URLs.</summary>
    private const string FullSurfaceFingerprint =
        "84C670D20605B2E42D160FCEFF403F1FD98D6E1A3AB51FE0E77E097146E4EF5C";

    /// <summary>The same surface with a literal <c>${...}</c> data path, post-C: the
    /// expansion fails (the variable is unknown) and the data domain hashes the new
    /// expansion-failure error text instead of the former does-not-exist text.</summary>
    private const string LiteralVariablePathFingerprint =
        "C8AF92857DDB093EA2542D14FD0329E380AD5E246CD4268EF1BCB086BD19EAF3";

    /// <summary>The pre-C value of the with-<c>${...}</c> surface: exactly the
    /// fingerprint whose data domain hashed the former <c>error:</c> text ("The data
    /// entry file '…' does not exist."). Pinned so the flip's previous side is a
    /// provable hash, not a story (framework §5 fixture requirement).</summary>
    private const string PreCLiteralVariablePathFingerprint =
        "293DDAFFA243CD06321ED44450AE2D483BF14FEECB6A68FF1697D5AA0159D6E2";

    private const string MinimalSurfaceFingerprint =
        "C4E3E123B8397A74FFD8999B9055F582EB694887A2D41450F9F4631B2C9844F0";

    /// <summary>The misspelled-known-data-name surface (ADR 0040 decision 7): a
    /// declaration like <c>"Mcp"</c> is unknown to the exact-match data grammar —
    /// recorded verbatim and inert, hashing exactly the bytes its approval was
    /// persisted under. The catalog must never canonicalize the recorded name: that
    /// would flip the data-domain input (revoking the approval) and silently activate
    /// the formerly lazy interpretation (the mcp domain would grow Id+GenerationKey).
    /// Pinned as its own surface so this flip is detectable at the fingerprint gate —
    /// the representative fixtures carry no misspelled sample.</summary>
    private const string MisspelledDataNameFingerprint =
        "D1FF32C5296473D4530F4748C0C57E38175F45CAB5F18CCCFD1C21928B536DD3";

    private const string LiteralVariablePath = "${env.MAIEUTICS_NO_SUCH_VAR}/mcp-unresolved.json";

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
                McpDiscoverContributionKind.ExtensionKind, SkillsContributionKind.ExtensionKind);
            descriptor.ExtensionDiagnostics.Should().Contain(diagnostic =>
                diagnostic.Contains("unknown-extension", StringComparison.Ordinal));
            descriptor.DataEntries.Select(static entry => entry.Name).Should().BeEquivalentTo(
                ["mcp", "notices", "broken"],
                static options => options.WithoutStrictOrdering());
            descriptor.DataEntries.Single(static entry => entry.Name == "notices").Error
                .Should().BeNull("an unknown data name is still collected");
            descriptor.DataEntries.Single(static entry => entry.Name == "broken").Error
                .Should().NotBeNull();
            descriptor.McpServersError.Should().BeNull();
            descriptor.McpServers.Should().ContainSingle();
            descriptor.Triggers.Should().HaveCount(3);
            descriptor.Triggers.Single(static trigger => trigger.Kind == "watch").WatchPaths
                .Single().Should().Contain("watched", "watch paths enter the fingerprint expanded");

            PluginDeclarationFingerprint.Compute(WithStableWorkerUrls(loaded, root))
                .Should().Be(FullSurfaceFingerprint);
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
            var golden = PluginDeclarationFingerprint.Compute(WithStableWorkerUrls(
                descriptor ?? throw new InvalidOperationException(error), root));

            // Reformat: different whitespace, reordered members, re-spelled numbers —
            // canonicalization must absorb every one of them.
            File.WriteAllText(
                Path.Combine(root, "maieutics.json"),
                """
                {"isolation":"auto",
                  "dependencies":["jsr:@maieutics/dep@1.2.0"],
                  "capabilities":["tools.invoke","content.read"],
                  "entrypoints":{
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
            PluginDeclarationFingerprint.Compute(WithStableWorkerUrls(
                reformatted ?? throw new InvalidOperationException(error), root)).Should().Be(golden);
            golden.Should().Be(FullSurfaceFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The C 期 boundary (framework §5): a data path containing a literal
    /// <c>${...}</c> token could not collect before (the literal file does not exist,
    /// so the data domain hashed the <c>error:</c> text) and collects-or-fails through
    /// interpolation now. The test pins both sides: the previous value is provably the
    /// error-text hash, and the new value differs — the acknowledged, fail-closed flip
    /// that revokes such plugins until re-approval (ADR 0037). Manifests whose paths
    /// carry no tokens are untouched (the unchanged golden above).</summary>
    [Fact]
    public void ALiteralVariableDataPathFlipsAndItsPreviousValueIsTheErrorTextHash()
    {
        var root = CreateGoldenPluginRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "maieutics.json"),
                $$"""
                {
                  "isolation": "auto",
                  "dependencies": ["jsr:@maieutics/dep@1.2.0"],
                  "capabilities": ["tools.invoke", "content.read"],
                  "entrypoints": {
                    "worker": { "main": ["./main.ts"], "side": ["./side.ts"] },
                    "mcp": "./mcp.json",
                    "notices": "./notices.json",
                    "broken": "./no-such-file.json",
                    "unresolved": "{{LiteralVariablePath}}"
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

            PluginManifest.TryLoad(root, GoldenVariables(), out var descriptor, out var error)
                .Should().BeTrue(error);
            var loaded = descriptor ?? throw new InvalidOperationException(error);

            // Post-C: the expansion fails (unknown variable) with the expansion error
            // text — not the former does-not-exist text.
            var unresolved = loaded.DataEntries.Single(static entry => entry.Name == "unresolved");
            unresolved.Error.Should().NotBeNull().And.Contain("cannot be expanded");
            PluginDeclarationFingerprint.Compute(WithStableWorkerUrls(loaded, root))
                .Should().Be(LiteralVariablePathFingerprint, "the data domain hashes the new expansion error text");

            // Pre-C reconstruction: the same surface with the former collection
            // outcome (the literal path probed as a file). Its fingerprint must equal
            // the pinned pre-C golden — the flip's previous side is exactly the
            // error-text hash.
            var preC = loaded with
            {
                DataEntries = [.. loaded.DataEntries.Select(entry =>
                    entry.Name == "unresolved"
                        ? new PluginDataEntry(entry.Name, null,
                            $"The data entry file '{LiteralVariablePath}' does not exist.")
                        : entry)]
            };
            PluginDeclarationFingerprint.Compute(WithStableWorkerUrls(preC, root))
                .Should().Be(PreCLiteralVariablePathFingerprint);
            PluginDeclarationFingerprint.Compute(WithStableWorkerUrls(loaded, root))
                .Should().NotBe(PreCLiteralVariablePathFingerprint, "the flip is real, both sides pinned");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AMisspelledKnownDataNameStaysDeclaredAndInertByteStable()
    {
        var root = CreateEmptyGoldenRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "deno.json"),
                """{ "name": "@maieutics/golden-misspelled", "permissions": { "default": { "read": ["./"] } } }""");
            File.WriteAllText(
                Path.Combine(root, "maieutics.json"),
                """{ "entrypoints": { "Mcp": "./mcp.json" } }""");
            File.WriteAllText(
                Path.Combine(root, "mcp.json"),
                """{ "mcpServers": { "probe": { "command": "deno", "args": ["info"] } } }""");

            PluginManifest.TryLoad(root, out var descriptor, out var error)
                .Should().BeTrue(error);
            var loaded = descriptor ?? throw new InvalidOperationException(error);

            // Verbatim, collected, inert: the data domain hashes the declared spelling
            // and the file's canonical JSON, and no mcp-domain servers appear — the
            // declaration is unknown to the exact-match grammar, not re-spelled into it.
            var entry = loaded.DataEntries.Should().ContainSingle().Which;
            entry.Name.Should().Be("Mcp", "the declared spelling is recorded verbatim");
            entry.Error.Should().BeNull("the file is collected normally");
            loaded.McpServers.Should().BeEmpty("a misspelled known name is inert");
            loaded.ExtensionDiagnostics.Should().Contain(static diagnostic =>
                diagnostic.Contains("data entry 'Mcp'", StringComparison.Ordinal));

            PluginDeclarationFingerprint.Compute(loaded).Should().Be(MisspelledDataNameFingerprint);
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

    /// <summary>Projects the one machine-local fingerprint input out of a loaded
    /// descriptor: a worker's EntryUrl embeds the absolute plugin root (it is the URL
    /// the kernel launches the worker through), so the fixture's root prefix is
    /// replaced with a fixed sentinel before hashing. The absolute prefix is machine
    /// semantics — approvals are per-machine local, so production fingerprints are
    /// free to carry it — and no cross-machine pin can hold it; everything pinable
    /// still hashes exactly as production computes it: the domain order, the export
    /// names, the URL's file:/// shape beyond the prefix, and every remaining domain
    /// byte-exact. The first pins held only on the pinning machine's temp path —
    /// every other machine (all three CI runners) hashed different bytes, which is
    /// what the byte-stability gate is supposed to prevent.</summary>
    private static PluginDescriptor WithStableWorkerUrls(PluginDescriptor descriptor, string root)
    {
        var rootUrl = new Uri(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .AbsoluteUri.TrimEnd('/');
        return descriptor with
        {
            Workers = [.. descriptor.Workers.Select(worker => worker with
            {
                EntryUrl = worker.EntryUrl.StartsWith(rootUrl, StringComparison.Ordinal)
                    ? StableRootUrl + worker.EntryUrl[rootUrl.Length..]
                    : worker.EntryUrl
            })]
        };
    }

    /// <summary>The fixed root prefix that replaces the fixture's machine-local one.</summary>
    private const string StableRootUrl = "file:///golden-root";

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
                "broken": "./no-such-file.json"
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
