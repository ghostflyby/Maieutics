namespace Maieutics.Plugins;

/// <summary>
///     Validates plugin dependency declarations (missing dependencies, cycles, excluded
///     dependents) and derives the deterministic topological start order plus the cascade
///     closures used for teardown. The kernel is the policy authority: it decides which
///     plugins are eligible; the host process re-derives ordering at runtime from the same
///     edges because it owns the worker handles and crash events.
/// </summary>
internal sealed class PluginDependencyGraph
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> dependents;
    private readonly IReadOnlyDictionary<string, string> excludedReasons;

    private PluginDependencyGraph(
        IReadOnlyList<PluginDescriptor> startOrder,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependents,
        IReadOnlyDictionary<string, string> excludedReasons)
    {
        StartOrder = startOrder;
        this.dependents = dependents;
        this.excludedReasons = excludedReasons;
    }

    /// <summary>Eligible plugins in dependency-first topological order (deterministic).</summary>
    public IReadOnlyList<PluginDescriptor> StartOrder { get; }

    /// <summary>Excluded plugin id to the reason it was excluded (missing dependency, cycle, ...).</summary>
    public IReadOnlyDictionary<string, string> ExcludedReasons => excludedReasons;

    public static PluginDependencyGraph Build(IReadOnlyList<PluginDescriptor> plugins)
    {
        var known = new Dictionary<string, PluginDescriptor>(StringComparer.Ordinal);
        foreach (var plugin in plugins) known[plugin.Id] = plugin;

        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var plugin in plugins)
            foreach (var dependency in plugin.Dependencies)
                if (!known.ContainsKey(dependency))
                {
                    reasons[plugin.Id] = $"missing_dependency:{dependency}";
                    break;
                }

        PropagateExclusion(plugins, reasons);

        var remaining = plugins.Where(plugin => !reasons.ContainsKey(plugin.Id)).ToArray();
        var dependentsOf = BuildDependents(remaining);
        var inDegree = remaining.ToDictionary(plugin => plugin.Id, plugin => plugin.Dependencies.Count, StringComparer.Ordinal);
        var ready = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var plugin in remaining)
            if (inDegree[plugin.Id] == 0)
                ready.Add(plugin.Id);

        var startOrder = new List<PluginDescriptor>();
        while (ready.Count > 0)
        {
            var id = ready.Min;
            if (id is null) break;
            ready.Remove(id);
            startOrder.Add(known[id]);
            foreach (var dependent in dependentsOf[id])
            {
                inDegree[dependent] -= 1;
                if (inDegree[dependent] == 0) ready.Add(dependent);
            }
        }

        var leftover = remaining
            .Where(plugin => inDegree[plugin.Id] > 0)
            .Select(plugin => plugin.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in leftover)
            if (CanReachSelf(id, leftover, dependentsOf))
                reasons[id] = "dependency_cycle";

        foreach (var id in leftover)
        {
            if (reasons.ContainsKey(id)) continue;
            var dependency = known[id].Dependencies.First(candidate => leftover.Contains(candidate));
            reasons[id] = $"dependency_excluded:{dependency}";
        }

        startOrder.RemoveAll(plugin => reasons.ContainsKey(plugin.Id));

        var enabledIds = startOrder.Select(plugin => plugin.Id).ToHashSet(StringComparer.Ordinal);
        var enabledDependents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var plugin in startOrder)
            enabledDependents[plugin.Id] = [];

        foreach (var plugin in startOrder)
        {
            var dependentsOfPlugin = new List<string>();
            foreach (var dependent in startOrder)
                if (dependent.Dependencies.Contains(plugin.Id))
                    dependentsOfPlugin.Add(dependent.Id);
            enabledDependents[plugin.Id] = dependentsOfPlugin;
        }

        return new PluginDependencyGraph(startOrder, enabledDependents, reasons);
    }

    /// <summary>Direct enabled dependents of a plugin, in start-order sequence.</summary>
    public IReadOnlyList<string> DependentsOf(string pluginId)
    {
        return dependents.TryGetValue(pluginId, out var result) ? result : [];
    }

    /// <summary>Every enabled plugin that transitively depends on the given plugin, for cascade teardown.</summary>
    public IReadOnlyList<string> TransitiveDependentsOf(string pluginId)
    {
        var closure = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>();
        frontier.Enqueue(pluginId);
        visited.Add(pluginId);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var dependent in DependentsOf(current))
                if (visited.Add(dependent))
                {
                    closure.Add(dependent);
                    frontier.Enqueue(dependent);
                }
        }

        return closure;
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildDependents(
        IReadOnlyList<PluginDescriptor> plugins)
    {
        var dependents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var plugin in plugins) dependents[plugin.Id] = [];
        foreach (var plugin in plugins)
        {
            foreach (var dependency in plugin.Dependencies)
            {
                if (!dependents.TryGetValue(dependency, out var list) || list.Contains(plugin.Id)) continue;
                dependents[dependency] = [.. list, plugin.Id];
            }
        }

        return dependents;
    }

    private static bool CanReachSelf(
        string target,
        IReadOnlySet<string> leftover,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependents)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Stack<string>();
        frontier.Push(target);
        while (frontier.Count > 0)
        {
            var current = frontier.Pop();
            if (!visited.Add(current)) continue;
            foreach (var dependent in dependents[current])
            {
                if (dependent == target) return true;
                if (leftover.Contains(dependent)) frontier.Push(dependent);
            }
        }

        return false;
    }

    private static void PropagateExclusion(
        IReadOnlyList<PluginDescriptor> plugins,
        Dictionary<string, string> reasons)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var plugin in plugins)
            {
                if (reasons.ContainsKey(plugin.Id)) continue;
                var excludedDependency = plugin.Dependencies.FirstOrDefault(reasons.ContainsKey);
                if (excludedDependency is null) continue;
                reasons[plugin.Id] = $"dependency_excluded:{excludedDependency}";
                changed = true;
            }
        }
    }
}
