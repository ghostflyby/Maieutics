using FluentAssertions;
using Maieutics.Plugins;

namespace Maieutics.Jupyter.Tests;

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
            []);
    }

    [Fact]
    public void TopologicalWavesOrderDependenciesFirst()
    {
        var graph = PluginDependencyGraph.Validate(
            [Plugin("c", "b"), Plugin("b", "a"), Plugin("a")]);

        graph.Enabled.Select(static p => p.Id).Should().Contain(["a", "b", "c"]);
        var waveIds = graph.Waves
            .Select(static wave => string.Join(",", wave.Select(static p => p.Id)))
            .ToArray();
        waveIds.Should().Equal("a", "b", "c");
        graph.Exclusions.Should().BeEmpty();
    }

    [Fact]
    public void IndependentPluginsShareAWave()
    {
        var graph = PluginDependencyGraph.Validate(
            [Plugin("x"), Plugin("y")]);

        graph.Enabled.Should().HaveCount(2);
        graph.Waves.Should().HaveCount(1);
        graph.Waves[0].Select(static p => p.Id).Should().Contain(["x", "y"]);
    }

    [Fact]
    public void MissingDependencyExcludesThePluginAndTransitiveDependents()
    {
        var graph = PluginDependencyGraph.Validate(
            [Plugin("a", "missing"), Plugin("b", "a"), Plugin("independent")]);

        graph.Enabled.Select(static p => p.Id).Should().Equal("independent");
        graph.Exclusions.Select(static e => e.PluginId)
            .Should().Contain(["a", "b"]);
        graph.Exclusions.Should().Contain(e =>
            e.PluginId == "a" && e.Reason == PluginExclusionReason.MissingDependency);
    }

    [Fact]
    public void CycleExcludesMembersAndDependents()
    {
        var graph = PluginDependencyGraph.Validate(
            [Plugin("a", "b"), Plugin("b", "a"), Plugin("c", "a"), Plugin("ok")]);

        graph.Enabled.Select(static p => p.Id).Should().Equal("ok");
        graph.Exclusions.Select(static e => e.PluginId)
            .Should().Contain(["a", "b", "c"]);
        graph.Exclusions.Should().Contain(e =>
            e.PluginId == "a" && e.Reason == PluginExclusionReason.DependencyCycle);
    }

    [Fact]
    public void SelfLoopIsACycle()
    {
        var graph = PluginDependencyGraph.Validate([Plugin("a", "a")]);

        graph.Enabled.Should().BeEmpty();
        graph.Exclusions.Should().Contain(e => e.PluginId == "a");
    }
}
