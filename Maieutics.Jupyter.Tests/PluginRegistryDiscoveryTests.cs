using FluentAssertions;
using Maieutics.Plugins;

namespace Maieutics.Jupyter.Tests;

public sealed class PluginRegistryDiscoveryTests
{
    private const string DenoJson = """
        {
          "name": "@acme/widget",
          "version": "1.0.0",
          "exports": { "./main": "./mod.ts" },
          "permissions": { "default": { "read": ["./"] } },
          "imports": { "@std/bytes": "jsr:@std/bytes@1" }
        }
        """;

    private const string MaieuticsJson = """
        {
          "isolation": "auto",
          "dependencies": ["base"],
          "entrypoints": { "main": ["./mod.ts"] }
        }
        """;

    [Fact]
    public void BuildsDescriptorFromPublishedSiblingManifests()
    {
        var denoJson = Path.Combine(Path.GetTempPath(), $"mc-reg-{Guid.NewGuid():N}");
        var maieutics = $"{denoJson}-m";
        File.WriteAllText(denoJson, DenoJson);
        File.WriteAllText(maieutics, MaieuticsJson);

        var descriptor = PluginRegistryDiscovery.TryLoadJsr(
            "@acme/widget",
            "jsr:@acme/widget@1.0.0",
            "deno",
            url => url.EndsWith("deno.json") ? denoJson : maieutics,
            []);

        descriptor.Should().NotBeNull();
        descriptor!.Id.Should().Be("@acme/widget");
        descriptor.Name.Should().Be("@acme/widget");
        descriptor.Workers.Should().ContainSingle();
        descriptor.Workers[0].EntryUrl.Should().Be("https://jsr.io/@acme/widget/1.0.0/mod.ts");
        descriptor.Imports.Should().ContainSingle().Which.Value.Should().Be("jsr:@std/bytes@1");
        descriptor.Permissions.Read.Values.Should().Equal("./");
    }

    [Fact]
    public void BuildsDescriptorFromExtractedNpmPackageDirectory()
    {
        var packageDir = Path.Combine(Path.GetTempPath(), $"mc-npm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "deno.json"), DenoJson);
        File.WriteAllText(Path.Combine(packageDir, "maieutics.json"), MaieuticsJson);
        File.WriteAllText(Path.Combine(packageDir, "mod.ts"), "export const x = 1;\n");

        var descriptor = PluginRegistryDiscovery.TryLoadFromPackageDir(
            "@acme/widget", "npm:@acme/widget@1.0.0", packageDir, []);

        descriptor.Should().NotBeNull();
        descriptor!.Id.Should().Be("@acme/widget");
        descriptor.RootDirectory.Should().Be(packageDir);
        descriptor.Workers[0].EntryUrl.Should().StartWith("file://");
        descriptor.Workers[0].EntryUrl.Should().EndWith("/mod.ts");
    }

    [Fact]
    public async Task TryLoadNpmResolvesTheExtractedPackageAndReportsMissingManifests()
    {
        var diagnostics = new List<string>();
        // chalk is a real npm package without a maieutics.json: the toolchain
        // mediation must resolve and extract it, then the loader reports the
        // missing plugin declaration rather than a resolution failure.
        var descriptor = PluginRegistryDiscovery.TryLoadNpm(
            "chalk", "npm:chalk@5", "deno", diagnostics);

        descriptor.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Should().Contain("maieutics.json");
        await Task.CompletedTask;
    }

    [Fact]
    public void RejectsVersionRangesWithADiagnostic()
    {
        var diagnostics = new List<string>();
        var descriptor = PluginRegistryDiscovery.TryLoadJsr(
            "@acme/widget",
            "jsr:@acme/widget@^1",
            "deno",
            _ => null,
            diagnostics);

        descriptor.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Should().Contain("exact version");
    }

    [Fact]
    public void ReturnsNullWhenThePackagePublishesNoMaieuticsJson()
    {
        var denoJson = Path.Combine(Path.GetTempPath(), $"mc-reg-{Guid.NewGuid():N}");
        File.WriteAllText(denoJson, DenoJson);

        var diagnostics = new List<string>();
        var descriptor = PluginRegistryDiscovery.TryLoadJsr(
            "@acme/widget",
            "jsr:@acme/widget@1.0.0",
            "deno",
            url => url.EndsWith("deno.json") ? denoJson : null,
            diagnostics);

        descriptor.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Should().Contain("maieutics.json");
    }

    [Fact]
    public void ReturnsNullForNonJsrKinds()
    {
        var diagnostics = new List<string>();
        var descriptor = PluginRegistryDiscovery.TryLoadJsr(
            "chalk", "npm:chalk@5", "deno", _ => null, diagnostics);

        descriptor.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Should().Contain("only jsr:");
    }
}
