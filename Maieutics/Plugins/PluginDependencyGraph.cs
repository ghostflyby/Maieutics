namespace Maieutics.Plugins;

/// <summary>Why a plugin was excluded from the enabled set (degraded, never fatal).</summary>
internal enum PluginExclusionReason
{
    MissingDependency,
    DependencyCycle
}

/// <summary>A plugin excluded from the enabled set, with the reason recorded for diagnostics.</summary>
internal sealed record PluginExclusion(string PluginId, PluginExclusionReason Reason, string Detail);

/// <summary>
///     Validates the plugin dependency graph declared by each plugin's <c>maieutics.dependencies</c>
///     (plugin ids = directory names) and computes topological start order (dependencies first).
///     Missing dependencies exclude the plugin and its transitive dependents; cycles exclude the
///     cycle members and their dependents. Degraded, not fatal — the remaining plugins still run.
/// </summary>
internal static class PluginDependencyGraph
{
    /// <summary>
    ///     Computes the enabled plugins and their topological start order. The result preserves the
    ///     input order within each wave; waves are ordered so every plugin appears after its
    ///     dependencies.
    /// </summary>
    public static PluginGraphResult Validate(
        IReadOnlyList<PluginDescriptor> plugins,
        Func<string, bool>? isPluginPresent = null)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        var present = isPluginPresent ?? (id => plugins.Any(plugin => plugin.Id == id));
        var byId = plugins.ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var exclusions = new List<PluginExclusion>();

        // Pass 1: missing dependencies — exclude the plugin and, transitively, everything that
        // depends on it.
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var plugin in plugins)
            {
                if (excluded.Contains(plugin.Id)) continue;
                foreach (var dependency in plugin.Dependencies)
                {
                    if (present(dependency) && !excluded.Contains(dependency)) continue;
                    excluded.Add(plugin.Id);
                    exclusions.Add(new PluginExclusion(
                        plugin.Id,
                        PluginExclusionReason.MissingDependency,
                        $"Missing dependency '{dependency}'."));
                    grew = true;
                    break;
                }
            }
        }

        // Pass 2: cycles among the remaining — exclude cycle members and their dependents.
        var cycleMembers = FindCycleMembers(plugins.Where(plugin => !excluded.Contains(plugin.Id)));
        grew = true;
        while (grew)
        {
            grew = false;
            foreach (var plugin in plugins)
            {
                if (excluded.Contains(plugin.Id)) continue;
                var inCycle = cycleMembers.Contains(plugin.Id);
                var dependsOnExcluded = plugin.Dependencies.Any(excluded.Contains);
                if (inCycle || dependsOnExcluded)
                {
                    excluded.Add(plugin.Id);
                    exclusions.Add(new PluginExclusion(
                        plugin.Id,
                        inCycle ? PluginExclusionReason.DependencyCycle : PluginExclusionReason.MissingDependency,
                        inCycle ? "Participates in a dependency cycle." : "Depends on an excluded plugin."));
                    grew = true;
                }
            }
        }

        var enabled = plugins.Where(plugin => !excluded.Contains(plugin.Id)).ToArray();
        return new PluginGraphResult(enabled, TopologicalWaves(enabled, excluded), exclusions);
    }

    /// <summary>Kahn-style topological waves: dependencies first, wave-parallel, waves-serial.</summary>
    private static IReadOnlyList<IReadOnlyList<PluginDescriptor>> TopologicalWaves(
        IReadOnlyList<PluginDescriptor> plugins,
        IReadOnlySet<string> excluded)
    {
        var byId = plugins.ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);
        var remaining = new HashSet<string>(plugins.Select(plugin => plugin.Id), StringComparer.Ordinal);
        var waves = new List<IReadOnlyList<PluginDescriptor>>();
        while (remaining.Count > 0)
        {
            var wave = plugins
                .Where(plugin => remaining.Contains(plugin.Id))
                .Where(plugin => plugin.Dependencies
                    .Where(dependency => byId.ContainsKey(dependency) && !excluded.Contains(dependency))
                    .All(dependency => !remaining.Contains(dependency)))
                .ToArray();
            if (wave.Length == 0)
            {
                // Cycle remnant (should not happen after validation): break deterministically.
                wave = plugins.Where(plugin => remaining.Contains(plugin.Id)).ToArray();
            }
            foreach (var plugin in wave) remaining.Remove(plugin.Id);
            waves.Add(wave);
        }
        return waves;
    }

    /// <summary>Returns the ids of plugins participating in a dependency cycle (Tarjan SCC > 1).</summary>
    private static HashSet<string> FindCycleMembers(IEnumerable<PluginDescriptor> plugins)
    {
        var byId = plugins.ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLink = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<string>();
        var nextIndex = 0;

        void StrongConnect(string id)
        {
            index[id] = nextIndex;
            lowLink[id] = nextIndex;
            nextIndex++;
            stack.Add(id);
            onStack.Add(id);

            foreach (var dependency in byId[id].Dependencies)
            {
                if (!byId.ContainsKey(dependency)) continue;
                if (!index.ContainsKey(dependency))
                {
                    StrongConnect(dependency);
                    lowLink[id] = Math.Min(lowLink[id], lowLink[dependency]);
                }
                else if (onStack.Contains(dependency))
                {
                    lowLink[id] = Math.Min(lowLink[id], index[dependency]);
                }
            }

            if (lowLink[id] != index[id]) return;
            // Root of an SCC: collect it; a cycle is an SCC with more than one node
            // (or a self-loop).
            var component = new List<string>();
            string member;
            do
            {
                member = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                onStack.Remove(member);
                component.Add(member);
            } while (member != id);

            if (component.Count > 1 || byId[id].Dependencies.Contains(id))
            {
                foreach (var item in component) members.Add(item);
            }
        }

        foreach (var plugin in byId.Values)
        {
            if (!index.ContainsKey(plugin.Id)) StrongConnect(plugin.Id);
        }
        return members;
    }
}

/// <summary>Result of dependency validation: the enabled plugins, their start waves, and exclusions.</summary>
internal sealed record PluginGraphResult(
    IReadOnlyList<PluginDescriptor> Enabled,
    IReadOnlyList<IReadOnlyList<PluginDescriptor>> Waves,
    IReadOnlyList<PluginExclusion> Exclusions);
