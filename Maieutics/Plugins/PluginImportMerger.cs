using System.Text.Json;

namespace Maieutics.Plugins;

/// <summary>One plugin-declared import mapping, normalized for merging: the raw key
/// and the resolved string value (the first element when the deno.json value is an
/// array; the flag is kept so the merge can warn about fallback semantics that do not
/// survive a flat merge).</summary>
internal sealed record PluginImportEntry(string Key, string Value, bool ValueWasArray);

internal sealed record PluginImportMergeResult(
    IReadOnlyList<KeyValuePair<string, string>> Imports,
    IReadOnlyList<PluginExclusion> Exclusions,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlySet<string> ExcludedPluginIds { get; init; } =
        Exclusions.Select(exclusion => exclusion.PluginId).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
///     Merges every enabled plugin's <c>deno.json</c> <c>imports</c> into the entries
///     of the kernel-materialized root config, so bare aliases declared by plugin
///     authors resolve at runtime (plugin directories are neither workspace members
///     nor registry packages, so Deno never consults their own config). See
///     docs/plugin-import-resolution.md §4. Pure and deterministic: output keys are
///     sorted, plugins are visited in plugin-id ordinal order, and mapping conflicts
///     exclude the lexicographically later plugin.
/// </summary>
internal static class PluginImportMerger
{
    /// <summary>Merges the imports of <paramref name="plugins"/>. Exclusions degrade
    /// the offending plugin for this cycle (it does not start); the returned imports
    /// contain only entries committed by surviving plugins.</summary>
    public static PluginImportMergeResult Merge(
        IReadOnlyList<PluginDescriptor> plugins,
        IReadOnlySet<string> reservedKeys)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(reservedKeys);

        var exclusions = new List<PluginExclusion>();
        var warnings = new List<string>();
        var excluded = new HashSet<string>(StringComparer.Ordinal);

        // Actor specifiers own the load hook's canonical match, which precedes native
        // resolution; a merged map entry for one would create a competing path (§4 R2).
        var actorSpecifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in plugins)
        {
            foreach (var worker in plugin.Workers)
            {
                actorSpecifiers.Add(NormalizeSpecifier($"{plugin.Name}/{worker.ExportName}"));
            }
        }

        var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var ownerByKey = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var plugin in plugins.OrderBy(plugin => plugin.Id, StringComparer.Ordinal))
        {
            if (excluded.Contains(plugin.Id)) continue;

            // Entries are staged per plugin and land only if the plugin survives
            // validation and conflict checks: an excluded plugin must leave nothing
            // in the map, or its leftovers would shadow healthy plugins' keys (§4 R5/R7/R8).
            var staged = new List<KeyValuePair<string, string>>();
            PluginExclusion? exclusion = null;
            foreach (var entry in plugin.Imports)
            {
                if (reservedKeys.Contains(entry.Key))
                {
                    warnings.Add(
                        $"Plugin '{plugin.Id}' import '{entry.Key}' is reserved by the host materialization and was skipped.");
                    continue;
                }
                if (actorSpecifiers.Contains(NormalizeSpecifier(entry.Key)))
                {
                    warnings.Add(
                        $"Plugin '{plugin.Id}' import '{entry.Key}' names a plugin actor entry owned by the load hook and was skipped.");
                    continue;
                }

                string value;
                if (IsPathValue(entry.Value))
                {
                    try
                    {
                        value = new Uri(Path.GetFullPath(Path.Combine(plugin.RootDirectory, entry.Value))).AbsoluteUri;
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException or NotSupportedException or UriFormatException)
                    {
                        warnings.Add(
                            $"Plugin '{plugin.Id}' import '{entry.Key}' could not be absolutized against the plugin root and was skipped.");
                        continue;
                    }
                }
                else if (IsRegistryScheme(entry.Value))
                {
                    value = entry.Value;
                }
                else
                {
                    exclusion = new PluginExclusion(
                        plugin.Id,
                        PluginExclusionReason.ImportMapping,
                        $"Import '{entry.Key}' maps to another bare specifier ('{entry.Value}'); map it directly to a jsr:, npm:, or file target.");
                    break;
                }

                if (entry.Key.EndsWith('/') && IsRegistryScheme(value))
                {
                    exclusion = new PluginExclusion(
                        plugin.Id,
                        PluginExclusionReason.ImportMapping,
                        $"Import '{entry.Key}' uses a trailing-slash key with a registry value ('{value}'); Deno cannot URL-parse that combination.");
                    break;
                }

                if (entry.ValueWasArray)
                {
                    warnings.Add(
                        $"Plugin '{plugin.Id}' import '{entry.Key}' is an array; only the first value ('{value}') survives the merge.");
                }
                staged.Add(new KeyValuePair<string, string>(entry.Key, value));
            }

            if (exclusion is not null)
            {
                exclusions.Add(exclusion);
                excluded.Add(plugin.Id);
                continue;
            }

            // Iteration is in ascending plugin-id order, so the current plugin is the
            // lexicographically later one and loses the conflict (§4 R8).
            string? conflictKey = null;
            foreach (var pair in staged)
            {
                if (merged.TryGetValue(pair.Key, out var existing) && existing != pair.Value)
                {
                    conflictKey = pair.Key;
                    break;
                }
            }

            if (conflictKey is not null)
            {
                exclusions.Add(new PluginExclusion(
                    plugin.Id,
                    PluginExclusionReason.ImportMapping,
                    $"Import '{conflictKey}' is mapped to '{merged[conflictKey]}' by plugin '{ownerByKey[conflictKey]}' and to " +
                    $"'{staged.First(pair => pair.Key == conflictKey).Value}' by plugin '{plugin.Id}'; " +
                    "the later plugin id is excluded."));
                excluded.Add(plugin.Id);
                continue;
            }

            foreach (var (key, value) in staged)
            {
                merged[key] = value;
                // A key an earlier plugin already mapped (identical value) keeps its
                // first owner: the owner names the plugin a future conflict must not evict.
                if (!ownerByKey.ContainsKey(key)) ownerByKey[key] = plugin.Id;
            }
        }

        return new PluginImportMergeResult(
            [.. merged.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value))],
            exclusions,
            warnings);
    }

    private static bool IsPathValue(string value) =>
        value.StartsWith("./", StringComparison.Ordinal) ||
        value.StartsWith("../", StringComparison.Ordinal) ||
        Path.IsPathRooted(value);

    private static bool IsRegistryScheme(string value) =>
        value.StartsWith("jsr:", StringComparison.Ordinal) ||
        value.StartsWith("npm:", StringComparison.Ordinal) ||
        value.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("node:", StringComparison.Ordinal) ||
        value.StartsWith("data:", StringComparison.Ordinal) ||
        value.StartsWith("file:", StringComparison.Ordinal);

    /// <summary>True when two import-map sets carry identical key→value mappings
    /// (declaration order and array-vs-string shape are not runtime-visible).</summary>
    public static bool SameMapping(
        IReadOnlyList<PluginImportEntry> left,
        IReadOnlyList<PluginImportEntry> right)
    {
        if (left.Count != right.Count) return false;
        var byKey = left.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return right.All(entry => byKey.TryGetValue(entry.Key, out var value) && value == entry.Value);
    }

    /// <summary>Canonical comparison form of a plugin specifier: strip a <c>jsr:</c>
    /// prefix and the <c>@version</c> segment after the package name — the same rule
    /// as the SDK load hook's canonical match.</summary>
    public static string NormalizeSpecifier(string specifier)
    {
        var value = specifier.StartsWith("jsr:", StringComparison.Ordinal)
            ? specifier["jsr:".Length..]
            : specifier;
        var at = value.IndexOf('@', 1);
        var slashAfterVersion = at == -1 ? -1 : value.IndexOf('/', at + 1);
        if (at > 0 && slashAfterVersion != -1)
        {
            value = string.Concat(value.AsSpan(0, at), value.AsSpan(slashAfterVersion));
        }
        return value;
    }
}

/// <summary>Manifest-side helper: reads <c>deno.json</c> <c>imports</c> entries into
/// normalized merge entries (array values collapse to their first element).</summary>
internal static class PluginImportReader
{
    public static IReadOnlyList<PluginImportEntry> Read(JsonElement? imports)
    {
        if (imports is not { ValueKind: JsonValueKind.Object } element) return [];

        var entries = new List<PluginImportEntry>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                if (property.Value.GetString() is { Length: > 0 } value)
                {
                    entries.Add(new PluginImportEntry(property.Name, value, ValueWasArray: false));
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } first)
                    {
                        entries.Add(new PluginImportEntry(property.Name, first, ValueWasArray: true));
                        break;
                    }
                }
            }
        }
        return entries;
    }
}
