using FluentAssertions;
using Maieutics.Plugins;

namespace Maieutics.Jupyter.Tests;

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
                "main": ["./mod.ts", "./helper.ts"],
                "mcp": ["./src/mcp.ts"]
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
        descriptor.Workers[1].ExportName.Should().Be("mcp");
        descriptor.Workers[1].EntryUrl.Should().EndWith("/src/mcp.ts");
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
            { "entrypoints": { "main": ["./mod.ts"] } }
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
                "ok": ["./mod.ts"],
                "escape": ["../outside.ts"],
                "abs": ["/etc/passwd"]
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
