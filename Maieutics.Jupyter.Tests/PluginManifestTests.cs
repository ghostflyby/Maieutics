using FluentAssertions;
using Maieutics.Plugins;

namespace Maieutics.Jupyter.Tests;

public sealed class PluginManifestTests
{
    [Fact]
    public void LoadsSubpathExportsAndPositivePermissions()
    {
        var directory = CreatePluginDirectory("""
            {
              "name": "@maieutics/example",
              "version": "0.1.0",
              "exports": {
                ".": "./mod.ts",
                "./mcp": "./src/mcp.ts"
              },
              "permissions": {
                "default": {
                  "read": ["./"],
                  "net": [],
                  "write": true
                }
              },
              "maieutics": {
                "isolation": "auto"
              }
            }
            """);

        PluginManifest.TryLoad(directory, out var descriptor, out var error).Should().BeTrue(error);
        descriptor!.Name.Should().Be("@maieutics/example");
        descriptor.Workers.Should().HaveCount(1);
        descriptor.Workers[0].ExportName.Should().Be("./mcp");
        descriptor.Workers[0].EntryUrl.Should().StartWith("file://");
        descriptor.Permissions.Read.AllowAll.Should().BeFalse();
        descriptor.Permissions.Read.Values.Should().Equal("./");
        descriptor.Permissions.Net.Values.Should().BeEmpty();
        descriptor.Permissions.Write.AllowAll.Should().BeTrue();
        descriptor.Isolation.Should().Be("auto");
    }

    [Fact]
    public void RejectsDirectoriesWithoutTheMaieuticsMarker()
    {
        var directory = CreatePluginDirectory("""
            {
              "name": "@plain/package",
              "version": "0.1.0",
              "exports": "./mod.ts"
            }
            """);

        PluginManifest.TryLoad(directory, out _, out var error).Should().BeFalse();
        error.Should().Contain("maieutics");
    }

    [Fact]
    public void TreatsStringExportsAsHavingNoCarrierWorkers()
    {
        var directory = CreatePluginDirectory("""
            {
              "name": "@maieutics/single",
              "version": "0.1.0",
              "exports": "./mod.ts",
              "maieutics": {}
            }
            """);

        PluginManifest.TryLoad(directory, out var descriptor, out var error).Should().BeTrue(error);
        descriptor!.Workers.Should().BeEmpty();
    }

    [Fact]
    public void ReportsInvalidJsonAsFailure()
    {
        var directory = CreatePluginDirectory("{ not json");

        PluginManifest.TryLoad(directory, out _, out var error).Should().BeFalse();
        error.Should().Contain("deno.json");
    }

    [Fact]
    public void IgnoresUnknownPermissionElementsAndShapes()
    {
        var directory = CreatePluginDirectory("""
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
              },
              "maieutics": {}
            }
            """);

        PluginManifest.TryLoad(directory, out var descriptor, out var error).Should().BeTrue(error);
        descriptor!.Permissions.Read.AllowAll.Should().BeFalse();
        descriptor.Permissions.Read.Values.Should().Equal("./");
        descriptor.Permissions.Net.AllowAll.Should().BeFalse();
        descriptor.Permissions.Net.Values.Should().BeEmpty();
        descriptor.Permissions.Env.AllowAll.Should().BeFalse();
        descriptor.Permissions.Env.Values.Should().BeEmpty();
    }

    private static string CreatePluginDirectory(string denoJson)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mc-plugin-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "deno.json"), denoJson);
        return directory;
    }
}
