using System.Text.RegularExpressions;
using FluentAssertions;
using Maieutics.DenoRepl;
using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

/// <summary>
///     The Deno runtime packages are batch-embedded by glob and materialized by
///     enumerating the assembly manifest (the resource name IS the package-relative
///     path). These guards keep the pipeline omission-proof: every expected package
///     is present in the manifest, and after materialization every relative import
///     inside the materialized tree resolves to a file that was actually written —
///     a new module file that the pipeline somehow misses fails here, not at
///     runtime inside a Deno child.
/// </summary>
public sealed partial class DenoEmbeddedModulesTests
{
    private static readonly string[] ExpectedPackages =
    [
        "maieutics-repl-client",
        "maieutics-deno-repl",
        "maieutics-plugin-sdk",
        "maieutics-plugin-host",
        "maieutics-runtime",
        "shared",
    ];

    [Fact]
    public void EveryRuntimePackageIsEmbedded()
    {
        var names = typeof(DenoReplModule).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Deno/", StringComparison.Ordinal))
            .ToArray();

        foreach (var package in ExpectedPackages)
        {
            names.Should().ContainMatch($"Deno/{package}/*.ts",
                $"the {package} package must be batch-embedded (a miss means the glob exclusion ate it)");
        }

        // The config/lock companions materialize beside their packages.
        names.Should().Contain("Deno/maieutics-deno-repl/deno.json");
        names.Should().Contain("Deno/maieutics-deno-repl/deno.lock");
        names.Should().Contain("Deno/maieutics-plugin-sdk/deno.json");
        // Tests and fixtures are excluded by design; they run in the deno workspace.
        names.Should().NotContainMatch("Deno/*/*_test.ts");
        names.Should().NotContainMatch("Deno/*/test_fixtures/*");
    }

    [Fact]
    public async Task EveryRelativeImportInTheMaterializedTreeResolves()
    {
        var modules = new PluginHostModule();
        try
        {
        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     modules.ModuleDirectory,
                     "*.ts",
                     SearchOption.AllDirectories))
        {
            var source = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            foreach (var match in ImportPattern().EnumerateMatches(source.AsSpan()))
            {
                var specifier = source.Substring(match.Index + 1, match.Length - 2);
                if (!specifier.StartsWith("./", StringComparison.Ordinal) &&
                    !specifier.StartsWith("../", StringComparison.Ordinal))
                {
                    continue;
                }

                var resolved = Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(file)!, specifier));
                if (!File.Exists(resolved))
                    missing.Add($"{Path.GetRelativePath(modules.ModuleDirectory, file)} -> {specifier}");
            }
        }

        missing.Should().BeEmpty(
            "every relative import inside the materialized Deno tree must resolve to a file the " +
            "pipeline embedded and wrote (an unresolvable import means the batch embedding missed a module)");
        }
        finally
        {
            if (Directory.Exists(modules.ModuleDirectory))
                Directory.Delete(modules.ModuleDirectory, true);
        }
    }

    [GeneratedRegex("""from\s+["']([^"']+)["']""")]
    private static partial Regex ImportPattern();
}
