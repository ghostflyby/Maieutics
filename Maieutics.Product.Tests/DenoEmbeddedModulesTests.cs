using System.Text.RegularExpressions;
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
            Assert.Contains(names, item => Regex.IsMatch(item, "^Deno/" + Regex.Escape(package) + "/.*\\.ts$"));
        }

        // The config/lock companions materialize beside their packages.
        Assert.Contains("Deno/maieutics-deno-repl/deno.json", names);
        Assert.Contains("Deno/maieutics-deno-repl/deno.lock", names);
        Assert.Contains("Deno/maieutics-plugin-sdk/deno.json", names);
        // Tests and fixtures are excluded by design; they run in the deno workspace.
        Assert.DoesNotContain(names, item => Regex.IsMatch(item, "^Deno/.*/.*_test\\.ts$"));
        Assert.DoesNotContain(names, item => Regex.IsMatch(item, "^Deno/.*/test_fixtures/.*$"));
    }

    [Fact(Timeout = 60_000)]
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

        Assert.Empty(missing);
        }
        finally
        {
                            await TempArea.DeleteAsync(modules.ModuleDirectory, CancellationToken.None);
        }
    }

    [GeneratedRegex("""from\s+["']([^"']+)["']""")]
    private static partial Regex ImportPattern();
}
