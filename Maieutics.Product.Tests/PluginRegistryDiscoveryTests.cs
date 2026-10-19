using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

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

    [Fact(Timeout = 60_000)]
    public async Task BuildsDescriptorFromPublishedSiblingManifests()
    {
        var denoJson = Path.Combine(Path.GetTempPath(), $"mc-reg-{Guid.NewGuid():N}");
        var maieutics = $"{denoJson}-m";
        File.WriteAllText(denoJson, DenoJson);
        File.WriteAllText(maieutics, MaieuticsJson);

        var descriptor = await PluginRegistryDiscovery.TryLoadJsrAsync(
            "@acme/widget",
            "jsr:@acme/widget@1.0.0",
            "deno",
            (url, _) => Task.FromResult<string?>(url.EndsWith("deno.json") ? denoJson : maieutics),
            [],
            TestContext.Current.CancellationToken);

        Assert.NotNull(descriptor);
        Assert.Equal("@acme/widget", descriptor.Id);
        Assert.Equal("@acme/widget", descriptor.Name);
        Assert.Single(descriptor.Workers);
        Assert.Equal("https://jsr.io/@acme/widget/1.0.0/mod.ts", descriptor.Workers[0].EntryUrl);
        Assert.Equal("jsr:@std/bytes@1", Assert.Single(descriptor.Imports).Value);
        Assert.Equal(["./"], descriptor.Permissions.Read.Values);
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

        Assert.NotNull(descriptor);
        Assert.Equal("@acme/widget", descriptor.Id);
        Assert.Equal(packageDir, descriptor.RootDirectory);
        Assert.StartsWith("file://", descriptor.Workers[0].EntryUrl);
        Assert.EndsWith("/mod.ts", descriptor.Workers[0].EntryUrl);
    }

    // A cold `deno info npm:chalk@5` performs a real registry fetch, so the budget
    // headroom matches the probe's own 120s subprocess budget under the 180s xUnit
    // Timeout; the CI registry pre-warm step keeps the steady-state pass fast.
    [Fact(Timeout = 180_000)]
    public async Task TryLoadNpmResolvesTheExtractedPackageAndReportsMissingManifests()
    {
        var diagnostics = new List<string>();
        // chalk is a real npm package without a maieutics.json: the toolchain
        // mediation must resolve and extract it, then the loader reports the
        // missing plugin declaration rather than a resolution failure.
        var descriptor = await PluginRegistryDiscovery.TryLoadNpmAsync(
            "chalk", "npm:chalk@5", "deno", diagnostics, TestContext.Current.CancellationToken);

        Assert.Null(descriptor);
        Assert.Contains("maieutics.json", Assert.Single(diagnostics));
    }

    [Fact(Timeout = 60_000)]
    public async Task RejectsVersionRangesWithADiagnostic()
    {
        var diagnostics = new List<string>();
        var descriptor = await PluginRegistryDiscovery.TryLoadJsrAsync(
            "@acme/widget",
            "jsr:@acme/widget@^1",
            "deno",
            (_, _) => Task.FromResult<string?>(null),
            diagnostics,
            TestContext.Current.CancellationToken);

        Assert.Null(descriptor);
        Assert.Contains("exact version", Assert.Single(diagnostics));
    }

    [Fact(Timeout = 60_000)]
    public async Task ReturnsNullWhenThePackagePublishesNoMaieuticsJson()
    {
        var denoJson = Path.Combine(Path.GetTempPath(), $"mc-reg-{Guid.NewGuid():N}");
        File.WriteAllText(denoJson, DenoJson);

        var diagnostics = new List<string>();
        var descriptor = await PluginRegistryDiscovery.TryLoadJsrAsync(
            "@acme/widget",
            "jsr:@acme/widget@1.0.0",
            "deno",
            (url, _) => Task.FromResult<string?>(url.EndsWith("deno.json") ? denoJson : null),
            diagnostics,
            TestContext.Current.CancellationToken);

        Assert.Null(descriptor);
        Assert.Contains("maieutics.json", Assert.Single(diagnostics));
    }

    [Fact(Timeout = 60_000)]
    public async Task ReturnsNullForNonJsrKinds()
    {
        var diagnostics = new List<string>();
        var descriptor = await PluginRegistryDiscovery.TryLoadJsrAsync(
            "chalk", "npm:chalk@5", "deno", (_, _) => Task.FromResult<string?>(null), diagnostics,
            TestContext.Current.CancellationToken);

        Assert.Null(descriptor);
        Assert.Contains("only jsr:", Assert.Single(diagnostics));
    }
}
