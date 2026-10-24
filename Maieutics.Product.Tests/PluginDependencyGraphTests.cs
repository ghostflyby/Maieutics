using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

public sealed class PluginDependencyGraphTests
{
    private static PluginDescriptor Plugin(string id, params string[] dependencies)
    {
        return new PluginDescriptor(
            id,
            id,
            $"/tmp/{id}",
            [],
            new PluginPermissionGrants(
                PluginPermissionGrant.None,
                PluginPermissionGrant.None,
                PluginPermissionGrant.None,
                PluginPermissionGrant.None,
                PluginPermissionGrant.None,
                PluginPermissionGrant.None,
                PluginPermissionGrant.None,
                PluginPermissionGrant.None),
            "auto",
            dependencies,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            InspectionsContentReadAll: false);
    }

    [Fact]
    public void TopologicalWavesOrderDependenciesFirst()
    {
        var graph = PluginDependencyGraph.Validate(
            [Plugin("c", "b"), Plugin("b", "a"), Plugin("a")]);

        Assert.All(["a", "b", "c"], item => Assert.Contains(item, graph.Enabled.Select(static p => p.Id)));
        var waveIds = graph.Waves
            .Select(static wave => string.Join(",", wave.Select(static p => p.Id)))
            .ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, waveIds);
        Assert.Empty(graph.Exclusions);
    }

    [Fact]
    public void IndependentPluginsShareAWave()
    {
        var graph = PluginDependencyGraph.Validate(
            [Plugin("x"), Plugin("y")]);

        Assert.Equal(2, graph.Enabled.Count());
        Assert.Single(graph.Waves);
        Assert.All(["x", "y"], item => Assert.Contains(item, graph.Waves[0].Select(static p => p.Id)));
    }

    [Fact]
    public void MissingDependencyExcludesThePluginAndTransitiveDependents()
    {
        var graph = PluginDependencyGraph.Validate(
            [Plugin("a", "missing"), Plugin("b", "a"), Plugin("independent")]);

        Assert.Equal(["independent"], graph.Enabled.Select(static p => p.Id));
        Assert.All(["a", "b"], item => Assert.Contains(item, graph.Exclusions.Select(static e => e.PluginId)));
        Assert.Contains(graph.Exclusions, e =>
            e is { PluginId: "a", Reason: PluginExclusionReason.MissingDependency });
    }

    [Fact]
    public void CycleExcludesMembersAndDependents()
    {
        var graph = PluginDependencyGraph.Validate(
            [Plugin("a", "b"), Plugin("b", "a"), Plugin("c", "a"), Plugin("ok")]);

        Assert.Equal(["ok"], graph.Enabled.Select(static p => p.Id));
        Assert.All(["a", "b", "c"], item => Assert.Contains(item, graph.Exclusions.Select(static e => e.PluginId)));
        Assert.Contains(graph.Exclusions, e =>
            e is { PluginId: "a", Reason: PluginExclusionReason.DependencyCycle });
    }

    [Fact]
    public void SelfLoopIsACycle()
    {
        var graph = PluginDependencyGraph.Validate([Plugin("a", "a")]);

        Assert.Empty(graph.Enabled);
        Assert.Contains(graph.Exclusions, e => e.PluginId == "a");
    }
}
