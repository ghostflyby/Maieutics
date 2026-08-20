using FluentAssertions;
using Maieutics.Plugins;

namespace Maieutics.Jupyter.Tests;

public sealed class PluginDependencyGraphTests
{
    [Fact]
    public void StartOrderPlacesDependenciesFirst()
    {
        var graph = Build(Plugin("a", ["b"]), Plugin("b"));

        graph.StartOrder.Select(plugin => plugin.Id).Should().Equal("b", "a");
        graph.ExcludedReasons.Should().BeEmpty();
    }

    [Fact]
    public void StartOrderIsDeterministicForIndependentPlugins()
    {
        var graph = Build(Plugin("zeta"), Plugin("alpha"), Plugin("mid"));

        graph.StartOrder.Select(plugin => plugin.Id).Should().Equal("alpha", "mid", "zeta");
    }

    [Fact]
    public void StartOrderHandlesDiamonds()
    {
        var graph = Build(
            Plugin("a", ["b", "c"]),
            Plugin("b", ["d"]),
            Plugin("c", ["d"]),
            Plugin("d"));

        graph.StartOrder.Select(plugin => plugin.Id).Should().Equal("d", "b", "c", "a");
    }

    [Fact]
    public void ExcludesPluginsInDependencyCycles()
    {
        var graph = Build(Plugin("a", ["b"]), Plugin("b", ["a"]), Plugin("c", ["a"]));

        graph.ExcludedReasons.Keys.Should().Contain(["a", "b", "c"]);
        graph.ExcludedReasons["a"].Should().Be("dependency_cycle");
        graph.ExcludedReasons["b"].Should().Be("dependency_cycle");
        graph.ExcludedReasons["c"].Should().Be("dependency_excluded:a");
        graph.StartOrder.Should().BeEmpty();
    }

    [Fact]
    public void ExcludesPluginsWithMissingDependencies()
    {
        var graph = Build(Plugin("a", ["ghost"]), Plugin("b", ["a"]), Plugin("c"));

        graph.ExcludedReasons["a"].Should().Be("missing_dependency:ghost");
        graph.ExcludedReasons["b"].Should().Be("dependency_excluded:a");
        graph.ExcludedReasons.Should().NotContainKey("c");
        graph.StartOrder.Select(plugin => plugin.Id).Should().Equal("c");
    }

    [Fact]
    public void TransitiveDependentsComputeTheCascadeClosure()
    {
        var graph = Build(
            Plugin("a", ["b"]),
            Plugin("b", ["d"]),
            Plugin("c", ["d"]),
            Plugin("d"));

        graph.DependentsOf("d").Should().Equal("b", "c");
        graph.TransitiveDependentsOf("d").Should().Equal("b", "c", "a");
        graph.TransitiveDependentsOf("a").Should().BeEmpty();
    }

    [Fact]
    public void SelfDependencyIsACycle()
    {
        var graph = Build(Plugin("a", ["a"]));

        graph.ExcludedReasons["a"].Should().Be("dependency_cycle");
    }

    private static PluginDependencyGraph Build(params PluginDescriptor[] plugins)
    {
        return PluginDependencyGraph.Build(plugins);
    }

    private static PluginDescriptor Plugin(string id, params string[] dependencies)
    {
        return new PluginDescriptor(
            id,
            $"@{id}/pkg",
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
            dependencies);
    }
}
