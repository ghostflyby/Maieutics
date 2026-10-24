using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

public sealed class PluginImportMergerTests
{
    private static readonly IReadOnlySet<string> ReservedKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "@ghostflyby/worker-actor",
            "@preact/signals-core",
            "@maieutics/plugin-sdk"
        };

    [Fact]
    public void MergesRegistryValuesAndSortsKeysDeterministically()
    {
        var (imports, _, _) = Merge(
            Plugin("z-plugin", imports: [Entry("@std/path", "jsr:@std/path@^1")]),
            Plugin("a-plugin", imports: [Entry("@std/bytes", "jsr:@std/bytes@1")]));

        Assert.Equal(2, imports.Count());
        Assert.Equal("@std/bytes", imports[0].Key);
        Assert.Equal("@std/path", imports[1].Key);
    }

    [Fact]
    public void DedupesIdenticalEntriesAcrossPlugins()
    {
        var (imports, exclusions, warnings) = Merge(
            Plugin("a", imports: [Entry("@std/path", "jsr:@std/path@^1")]),
            Plugin("b", imports: [Entry("@std/path", "jsr:@std/path@^1")]));

        Assert.Single(imports);
        Assert.Empty(exclusions);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ConflictingEntryExcludesTheLaterPluginId()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a-plugin", imports: [Entry("@ui/lib", "jsr:@ui/lib@1")]),
            Plugin("b-plugin", imports: [Entry("@ui/lib", "jsr:@ui/lib@2")]));

        // The earlier plugin keeps its mapping and starts; the later id loses.
        Assert.Equal("jsr:@ui/lib@1", Assert.Single(imports).Value);
        Assert.Equal("b-plugin", Assert.Single(exclusions).PluginId);
    }

    [Fact]
    public void AbsolutizesRelativeValuesAgainstThePluginRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-merge-{Guid.NewGuid():N}");
        var (imports, _, _) = Merge(
            Plugin("a", rootDirectory: root, imports:
            [
                Entry("./util.ts", "./util.ts"),
                Entry("../shared.ts", "../shared.ts"),
            ]));

        Assert.Equal(2, imports.Count());
        Assert.Equal(new Uri(Path.GetFullPath(Path.Combine(root, "../shared.ts"))).AbsoluteUri, imports[0].Value);
        Assert.Equal(new Uri(Path.GetFullPath(Path.Combine(root, "util.ts"))).AbsoluteUri, imports[1].Value);
    }

    [Fact]
    public void SkipsReservedKeys()
    {
        var (imports, exclusions, warnings) = Merge(
            Plugin("a", imports: [Entry("@maieutics/plugin-sdk", "jsr:@maieutics/plugin-sdk@^0.1")]));

        Assert.Empty(imports);
        Assert.Empty(exclusions);
        Assert.Contains("reserved", Assert.Single(warnings));
    }

    [Fact]
    public void SkipsKeysThatNormalizeToPluginActorSpecifiers()
    {
        var (imports, exclusions, warnings) = Merge(
            Plugin("consumer", workers: [Worker("main")], imports:
            [
                Entry("@acme/widget/main", "jsr:@acme/widget@1/main"),
                Entry("jsr:@acme/widget@2/main", "jsr:@acme/widget@2/main"),
            ]),
            Plugin("@acme/widget", workers: [Worker("main")]));

        Assert.Empty(imports);
        Assert.Empty(exclusions);
        Assert.Equal(2, warnings.Count());
        Assert.All(warnings, warning => Assert.Contains("load hook", warning));
    }

    [Fact]
    public void ExcludesTrailingSlashKeysWithRegistryValues()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a", imports: [Entry("bytes/", "jsr:@std/bytes@1/")]));

        Assert.Empty(imports);
        Assert.Equal("a", Assert.Single(exclusions).PluginId);
    }

    [Fact]
    public void ExcludesBareAliasChainValues()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a", imports: [Entry("@std/path", "@std/path-full")]));

        Assert.Empty(imports);
        Assert.Contains("@std/path-full", Assert.Single(exclusions).Detail);
    }

    [Fact]
    public void ArrayValuesCollapseToTheFirstElementWithAWarning()
    {
        var (imports, exclusions, warnings) = Merge(
            Plugin("a", imports: [Entry("@std/bytes", "jsr:@std/bytes@1.0.1", wasArray: true)]));

        Assert.Equal("jsr:@std/bytes@1.0.1", Assert.Single(imports).Value);
        Assert.Empty(exclusions);
        Assert.Contains("first value", Assert.Single(warnings));
    }

    [Fact]
    public void ExcludingOnePluginKeepsTheOtherPluginsEntries()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a", imports: [Entry("@std/path", "jsr:@std/path@^1")]),
            Plugin("b", imports: [Entry("@ui/lib", "jsr:@ui/lib@1")]),
            Plugin("c", imports: [Entry("@ui/lib", "jsr:@ui/lib@2")]));

        Assert.Equal(2, imports.Count());
        Assert.Contains(imports, pair => pair.Key == "@std/path");
        Assert.Contains(imports, pair => pair.Key == "@ui/lib" && pair.Value == "jsr:@ui/lib@1");
        Assert.Equal("c", Assert.Single(exclusions).PluginId);
    }

    [Fact]
    public void DedupedKeyKeepsItsFirstOwnerWhenTheLaterPluginConflicts()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a", imports:
            [
                Entry("@std/bytes", "jsr:@std/bytes@1"),
                Entry("@std/path", "jsr:@std/path@^1"),
            ]),
            Plugin("b", imports:
            [
                Entry("@std/bytes", "jsr:@std/bytes@1"),
                Entry("@std/path", "jsr:@std/path@2"),
            ]));

        // b conflicts on @std/path and is excluded; the deduped @std/bytes entry
        // belongs to a and must survive b's exclusion untouched.
        Assert.Equal(2, imports.Count());
        Assert.Contains(imports, pair => pair.Key == "@std/bytes" && pair.Value == "jsr:@std/bytes@1");
        Assert.Equal("b", Assert.Single(exclusions).PluginId);
    }

    [Fact]
    public void ExcludedPluginCommitsNoEntries()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a", imports:
            [
                Entry("@std/bytes", "jsr:@std/bytes@1"),
                Entry("@std/other", "@std/bytes"),
            ]),
            Plugin("b", imports: [Entry("@std/bytes", "jsr:@std/bytes@2")]));

        // a is excluded for the bare-chain value; its earlier entry must not occupy
        // @std/bytes, so b's healthy mapping stands and b is not dragged into exclusion.
        Assert.Equal("jsr:@std/bytes@2", Assert.Single(imports).Value);
        Assert.Equal("a", Assert.Single(exclusions).PluginId);
    }

    [Fact]
    public void NormalizeSpecifierStripsJsrPrefixAndVersionSegment()
    {
        Assert.Equal("@acme/widget/main", PluginImportMerger.NormalizeSpecifier("jsr:@acme/widget@1.0/main"));
        Assert.Equal("@acme/widget/main", PluginImportMerger.NormalizeSpecifier("@acme/widget/main"));
        Assert.Equal("@acme/widget", PluginImportMerger.NormalizeSpecifier("@acme/widget"));
    }

    [Fact]
    public void SameMappingDetectsKeyAndValueChanges()
    {
        var baseMap = new[] { Entry("@std/bytes", "jsr:@std/bytes@1") };
        Assert.True(PluginImportMerger.SameMapping(baseMap, baseMap));
        Assert.False(PluginImportMerger.SameMapping(
            baseMap,
            [Entry("@std/bytes", "jsr:@std/bytes@1.0.6")]));
        Assert.False(PluginImportMerger.SameMapping(
            baseMap,
            [Entry("@std/bytes", "jsr:@std/bytes@1"), Entry("@std/path", "jsr:@std/path@^1")]));
        // Order and array-vs-string shape are not runtime-visible.
        Assert.True(PluginImportMerger.SameMapping(
            [Entry("@std/bytes", "jsr:@std/bytes@1"), Entry("@std/path", "jsr:@std/path@^1")],
            [Entry("@std/path", "jsr:@std/path@^1"), Entry("@std/bytes", "jsr:@std/bytes@1", wasArray: true)]));
    }

    private static PluginImportEntry Entry(string key, string value, bool wasArray = false) =>
        new(key, value, wasArray);

    private static PluginWorkerDescriptor Worker(string exportName) =>
        new(exportName, $"file:///{exportName}.ts");

    private static PluginDescriptor Plugin(
        string id,
        string? rootDirectory = null,
        string? name = null,
        PluginWorkerDescriptor[]? workers = null,
        PluginImportEntry[]? imports = null)
    {
        var grants = new PluginPermissionGrants(
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None,
            PluginPermissionGrant.None);
        return new PluginDescriptor(
            id,
            name ?? id,
            rootDirectory ?? Path.Combine(Path.GetTempPath(), $"mc-merge-{id}"),
            workers ?? [],
            grants,
            "auto",
            [],
            imports ?? [],
            [],
            [],
            [],
            [],
            [],
            [],
            InspectionsContentReadAll: false);
    }

    private static (
        IReadOnlyList<KeyValuePair<string, string>> Imports,
        IReadOnlyList<PluginExclusion> Exclusions,
        IReadOnlyList<string> Warnings) Merge(params PluginDescriptor[] plugins)
    {
        var result = PluginImportMerger.Merge(plugins, ReservedKeys);
        return (result.Imports, result.Exclusions, result.Warnings);
    }
}
