namespace Maieutics.Plugins.Contributions;

/// <summary>The sticky key of one generated contribution part: the full registration
/// triple (PluginId, ExportName, ExtensionPointName), Ordinal. The triple — not the
/// (pluginId, exportName) pair — because the pair's equivalence to the registration key
/// only holds while every contribution has exactly one extension point; a second
/// extension point over the same face would silently collide.</summary>
internal readonly record struct SlotSourceKey(
    string PluginId,
    string ExportName,
    string ExtensionPointName)
{
    public static IEqualityComparer<SlotSourceKey> Ordinal { get; } =
        new SlotSourceKeyComparer();
}

internal sealed class SlotSourceKeyComparer : IEqualityComparer<SlotSourceKey>
{
    public bool Equals(SlotSourceKey left, SlotSourceKey right) =>
        string.Equals(left.PluginId, right.PluginId, StringComparison.Ordinal) &&
        string.Equals(left.ExportName, right.ExportName, StringComparison.Ordinal) &&
        string.Equals(left.ExtensionPointName, right.ExtensionPointName, StringComparison.Ordinal);

    public int GetHashCode(SlotSourceKey key) =>
        HashCode.Combine(
            key.PluginId.GetHashCode(StringComparison.Ordinal),
            key.ExportName.GetHashCode(StringComparison.Ordinal),
            key.ExtensionPointName.GetHashCode(StringComparison.Ordinal));
}

/// <summary>Read model of one (plugin, kind) contribution slot: the three optional
/// parts (declared / generated / published) as the table currently holds them. The
/// presence flag — a held slot means the plugin has a face even with empty parts, the
/// publish-only plugin form — is exactly the former <c>contributedSkillPlugins</c>
/// set membership.</summary>
internal sealed class ContributionSlot
{
    public ContributionSlot(
        string pluginId,
        string kindName,
        IReadOnlyList<ContributionDescriptor>? declared,
        IReadOnlyDictionary<SlotSourceKey, IReadOnlyList<ContributionDescriptor>> generated,
        IReadOnlyList<ContributionDescriptor>? published)
    {
        PluginId = pluginId;
        KindName = kindName;
        Declared = declared;
        Generated = generated;
        Published = published;
    }

    public string PluginId { get; }

    public string KindName { get; }

    /// <summary>The declarative part; null when this kind carries no declared part or
    /// none was contributed.</summary>
    public IReadOnlyList<ContributionDescriptor>? Declared { get; }

    /// <summary>The sticky per-source generated parts (insertion-ordered bag view).</summary>
    public IReadOnlyDictionary<SlotSourceKey, IReadOnlyList<ContributionDescriptor>> Generated { get; }

    /// <summary>The published part (wholesale-replaced); null when none.</summary>
    public IReadOnlyList<ContributionDescriptor>? Published { get; }

    public bool HoldsFace => true;
}

/// <summary>The per-kind contribution state the skills reconcile passes commit into:
/// the former four dictionaries (<c>pluginGeneratedSkills</c>, <c>pluginDeclarativeSkills</c>,
/// <c>pluginPublishedSkills</c>, <c>contributedSkillPlugins</c>) under one owner.
/// Every method must be called under the host manager's gate — the table owns no lock
/// of its own, matching the former dictionaries' locking discipline.
/// Composition order is semantics: declared → generated (sticky-bag insertion order,
/// including the re-insertion effect of a removal) → published. The bag is iterated in
/// first-write order, never key-sorted, because the skill catalog resolves in-plugin
/// name shadows by first occurrence — key sorting would flip the winner.</summary>
internal sealed class ContributionSlotTable
{
    private readonly string kindName;

    /// <summary>Sticky generated parts keyed by the full registration triple. Iteration
    /// order is first-write order (Dictionary semantics); a generated part may outlive
    /// its plugin's slot presence (a pass whose commit was declined leaves its sticky
    /// writes behind until a later successful pass composes them) — mirroring the
    /// former flat dictionary exactly.</summary>
    private readonly Dictionary<SlotSourceKey, IReadOnlyList<ContributionDescriptor>> generated =
        new(SlotSourceKey.Ordinal);

    /// <summary>Declared and published parts plus slot presence, per plugin id.</summary>
    private readonly Dictionary<string, SlotParts> slots = new(StringComparer.Ordinal);

    public ContributionSlotTable(string kindName)
    {
        this.kindName = kindName;
    }

    public string KindName => kindName;

    public bool HoldsFace(string pluginId) =>
        slots.TryGetValue(pluginId, out var parts) && parts.Held;

    /// <summary>Upserts one export's generated part (sticky last-good).</summary>
    public void SetGenerated(
        string pluginId,
        SlotSourceKey key,
        IReadOnlyList<ContributionDescriptor> part)
    {
        generated[key] = part;
    }

    /// <summary>Sets or clears (null) the declared part.</summary>
    public void SetDeclared(string pluginId, IReadOnlyList<ContributionDescriptor>? declared)
    {
        GetOrAdd(pluginId).Declared = declared;
    }

    /// <summary>Sets or clears (null) the published part.</summary>
    public void SetPublished(string pluginId, IReadOnlyList<ContributionDescriptor>? published)
    {
        GetOrAdd(pluginId).Published = published;
    }

    /// <summary>Marks the plugin as holding a contribution slot (a face).</summary>
    public void MarkSlotHeld(string pluginId)
    {
        GetOrAdd(pluginId).Held = true;
    }

    /// <summary>Composes the plugin's contribution in the semantic order: declared →
    /// generated parts owned by the plugin (bag insertion order) → published.</summary>
    public IReadOnlyList<ContributionDescriptor> Compose(string pluginId)
    {
        var parts = slots.GetValueOrDefault(pluginId);
        var composed = new List<ContributionDescriptor>();
        if (parts?.Declared is { } declared) composed.AddRange(declared);
        foreach (var (key, part) in generated)
            if (key.PluginId == pluginId)
                composed.AddRange(part);
        if (parts?.Published is { } published) composed.AddRange(published);
        return composed;
    }

    /// <summary>The plugin's slot read model, or null when it holds none. Generated
    /// parts are visible even without a held slot (the declined-commit edge above).</summary>
    public ContributionSlot? Snapshot(string pluginId)
    {
        if (!slots.TryGetValue(pluginId, out var parts)) return null;
        var owned = generated
            .Where(pair => pair.Key.PluginId == pluginId)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, SlotSourceKey.Ordinal);
        return new ContributionSlot(pluginId, kindName, parts.Declared, owned, parts.Published);
    }

    /// <summary>Removes the plugin's whole slot — presence, declared, published, and its
    /// generated parts. Returns false when the plugin held no slot, in which case
    /// nothing is cleared (the former early return before any dictionary mutation: a
    /// slot that was never committed has nothing to remove and its sticky orphans stay
    /// for a later pass to compose).</summary>
    public bool Remove(string pluginId)
    {
        if (!slots.TryGetValue(pluginId, out var parts) || !parts.Held) return false;
        slots.Remove(pluginId);
        foreach (var key in generated.Keys.Where(key => key.PluginId == pluginId).ToArray())
            generated.Remove(key);
        return true;
    }

    /// <summary>Clears every slot and returns the plugin ids that held one, so the
    /// caller can detach them from the consumption view in the same order the former
    /// presence set iterated.</summary>
    public IReadOnlyList<string> ClearAll()
    {
        var present = slots.Keys.ToArray();
        slots.Clear();
        generated.Clear();
        return present;
    }

    private SlotParts GetOrAdd(string pluginId)
    {
        if (!slots.TryGetValue(pluginId, out var parts))
            slots[pluginId] = parts = new SlotParts();
        return parts;
    }

    private sealed class SlotParts
    {
        public bool Held;

        public IReadOnlyList<ContributionDescriptor>? Declared;

        public IReadOnlyList<ContributionDescriptor>? Published;
    }
}
