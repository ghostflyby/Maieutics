using FluentAssertions;
using Maieutics.Plugins;

namespace Maieutics.Jupyter.Tests;

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

        imports.Should().HaveCount(2);
        imports[0].Key.Should().Be("@std/bytes");
        imports[1].Key.Should().Be("@std/path");
    }

    [Fact]
    public void DedupesIdenticalEntriesAcrossPlugins()
    {
        var (imports, exclusions, warnings) = Merge(
            Plugin("a", imports: [Entry("@std/path", "jsr:@std/path@^1")]),
            Plugin("b", imports: [Entry("@std/path", "jsr:@std/path@^1")]));

        imports.Should().ContainSingle();
        exclusions.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void ConflictingEntryExcludesTheLaterPluginId()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a-plugin", imports: [Entry("@ui/lib", "jsr:@ui/lib@1")]),
            Plugin("b-plugin", imports: [Entry("@ui/lib", "jsr:@ui/lib@2")]));

        // The earlier plugin keeps its mapping and starts; the later id loses.
        imports.Should().ContainSingle().Which.Value.Should().Be("jsr:@ui/lib@1");
        exclusions.Should().ContainSingle().Which.PluginId.Should().Be("b-plugin");
    }

    [Fact]
    public void AbsolutizesRelativeValuesAgainstThePluginRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-merge-{Guid.NewGuid():N}");
        var (imports, _, _) = Merge(
            Plugin("a", rootDirectory: root, imports: [
                Entry("./util.ts", "./util.ts"),
                Entry("../shared.ts", "../shared.ts"),
            ]));

        imports.Should().HaveCount(2);
        imports[0].Value.Should().Be(new Uri(Path.GetFullPath(Path.Combine(root, "../shared.ts"))).AbsoluteUri);
        imports[1].Value.Should().Be(new Uri(Path.GetFullPath(Path.Combine(root, "util.ts"))).AbsoluteUri);
    }

    [Fact]
    public void SkipsReservedKeys()
    {
        var (imports, exclusions, warnings) = Merge(
            Plugin("a", imports: [Entry("@maieutics/plugin-sdk", "jsr:@maieutics/plugin-sdk@^0.1")]));

        imports.Should().BeEmpty();
        exclusions.Should().BeEmpty();
        warnings.Should().ContainSingle().Which.Should().Contain("reserved");
    }

    [Fact]
    public void SkipsKeysThatNormalizeToPluginActorSpecifiers()
    {
        var (imports, exclusions, warnings) = Merge(
            Plugin("consumer", workers: [Worker("main")], imports: [
                Entry("@acme/widget/main", "jsr:@acme/widget@1/main"),
                Entry("jsr:@acme/widget@2/main", "jsr:@acme/widget@2/main"),
            ]),
            Plugin("@acme/widget", workers: [Worker("main")]));

        imports.Should().BeEmpty();
        exclusions.Should().BeEmpty();
        warnings.Should().HaveCount(2).And.OnlyContain(warning => warning.Contains("load hook"));
    }

    [Fact]
    public void ExcludesTrailingSlashKeysWithRegistryValues()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a", imports: [Entry("bytes/", "jsr:@std/bytes@1/")]));

        imports.Should().BeEmpty();
        exclusions.Should().ContainSingle().Which.PluginId.Should().Be("a");
    }

    [Fact]
    public void ExcludesBareAliasChainValues()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a", imports: [Entry("@std/path", "@std/path-full")]));

        imports.Should().BeEmpty();
        exclusions.Should().ContainSingle().Which.Detail.Should().Contain("@std/path-full");
    }

    [Fact]
    public void ArrayValuesCollapseToTheFirstElementWithAWarning()
    {
        var (imports, exclusions, warnings) = Merge(
            Plugin("a", imports: [Entry("@std/bytes", "jsr:@std/bytes@1.0.1", wasArray: true)]));

        imports.Should().ContainSingle().Which.Value.Should().Be("jsr:@std/bytes@1.0.1");
        exclusions.Should().BeEmpty();
        warnings.Should().ContainSingle().Which.Should().Contain("first value");
    }

    [Fact]
    public void ExcludingOnePluginKeepsTheOtherPluginsEntries()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a", imports: [Entry("@std/path", "jsr:@std/path@^1")]),
            Plugin("b", imports: [Entry("@ui/lib", "jsr:@ui/lib@1")]),
            Plugin("c", imports: [Entry("@ui/lib", "jsr:@ui/lib@2")]));

        imports.Should().HaveCount(2).And.Contain(pair => pair.Key == "@std/path");
        imports.Should().Contain(pair => pair.Key == "@ui/lib" && pair.Value == "jsr:@ui/lib@1");
        exclusions.Should().ContainSingle().Which.PluginId.Should().Be("c");
    }

    [Fact]
    public void DedupedKeyKeepsItsFirstOwnerWhenTheLaterPluginConflicts()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a", imports: [
                Entry("@std/bytes", "jsr:@std/bytes@1"),
                Entry("@std/path", "jsr:@std/path@^1"),
            ]),
            Plugin("b", imports: [
                Entry("@std/bytes", "jsr:@std/bytes@1"),
                Entry("@std/path", "jsr:@std/path@2"),
            ]));

        // b conflicts on @std/path and is excluded; the deduped @std/bytes entry
        // belongs to a and must survive b's exclusion untouched.
        imports.Should().HaveCount(2).And.Contain(
            pair => pair.Key == "@std/bytes" && pair.Value == "jsr:@std/bytes@1");
        exclusions.Should().ContainSingle().Which.PluginId.Should().Be("b");
    }

    [Fact]
    public void ExcludedPluginCommitsNoEntries()
    {
        var (imports, exclusions, _) = Merge(
            Plugin("a", imports: [
                Entry("@std/bytes", "jsr:@std/bytes@1"),
                Entry("@std/other", "@std/bytes"),
            ]),
            Plugin("b", imports: [Entry("@std/bytes", "jsr:@std/bytes@2")]));

        // a is excluded for the bare-chain value; its earlier entry must not occupy
        // @std/bytes, so b's healthy mapping stands and b is not dragged into exclusion.
        imports.Should().ContainSingle().Which.Value.Should().Be("jsr:@std/bytes@2");
        exclusions.Should().ContainSingle().Which.PluginId.Should().Be("a");
    }

    [Fact]
    public void NormalizeSpecifierStripsJsrPrefixAndVersionSegment()
    {
        PluginImportMerger.NormalizeSpecifier("jsr:@acme/widget@1.0/main").Should().Be("@acme/widget/main");
        PluginImportMerger.NormalizeSpecifier("@acme/widget/main").Should().Be("@acme/widget/main");
        PluginImportMerger.NormalizeSpecifier("@acme/widget").Should().Be("@acme/widget");
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
            imports ?? []);
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
