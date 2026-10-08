using Maieutics.Mcp;

namespace Maieutics.Plugins.Contributions;

/// <summary>One contribution kind's entire kernel-side knowledge (plugin-contribution
/// framework §3.2, wired in B 期): the grammar names it owns in the manifest, the load-
/// time interpretation of its declarations, and the metadata the host's generic paths
/// route through (synthetic registrations, capability dispatch, kernel retry policy).
/// One closed subclass per kind, registered once in <see cref="ContributionKindCatalog"/>
/// — the registry is closed at compile time, never mutated at runtime.</summary>
internal abstract class ContributionKindContract
{
    /// <summary>The canonical kind name — today the manifest extensions kind constant
    /// ("McpDiscover"/"Skills") or, for data-entry-only kinds, the data name ("ui").
    /// Note the manifest kind catalog and the wire registration catalog
    /// (<see cref="Control.ReplExtensionPointName"/>) are two directories; they
    /// coincidentally share spellings for the two kinds that have both.</summary>
    public abstract string KindName { get; }

    /// <summary>Whether the kind claims a manifest extensions entry spelled
    /// <paramref name="kind"/> — case-insensitive, the unified grammar policy. The
    /// base matches the kind's own name; data-entry-only kinds claim none.</summary>
    public virtual bool OwnsExtensionKind(string kind) =>
        string.Equals(kind, KindName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The manifest data-entry name the kind interprets, or null when it
    /// declares no data-entry form.</summary>
    public abstract string? DataEntryName { get; }

    /// <summary>The wire registration name (the <c>ReplExtensionPointName</c> catalog)
    /// the kind's export-set diff and computed invokes route through, or null when the
    /// kind has no worker form.</summary>
    public abstract string? ExtensionPointName { get; }

    /// <summary>Whether the kind has a worker-computed form (an extension point whose
    /// exports the kernel invokes).</summary>
    public abstract bool HasComputeForm { get; }

    /// <summary>Whether the kind has a runtime publish form. A non-null
    /// <see cref="PublishCapability"/> implies this.</summary>
    public abstract bool HasPublishForm { get; }

    /// <summary>The kernel capability name the kind's runtime publish flows through,
    /// or null when it publishes nothing (checked by the capability dispatch).</summary>
    public abstract string? PublishCapability { get; }

    /// <summary>The kind's framework-level configurability knobs (bounds, stickiness,
    /// retry policy, timeout defaults, interpolation behavior) — §5 of the framework
    /// design. Every default equals the former hardcoded value.</summary>
    public abstract ContributionKindMetadata Metadata { get; }

    /// <summary>Whether the kind's recorded declarations produce synthetic
    /// registrations (registry entries the kernel computes from the manifest snapshot
    /// without a worker — the manifest-declared discovery form). Skills ride worker
    /// registrations instead; ui declares without a worker form.</summary>
    public abstract bool HasDeclarativeRegistrations { get; }

    /// <summary>Interprets the declarations the kind claims into the load-time record
    /// face: descriptors plus the plugin-level sticky error channel (the mcp.json data
    /// file's <c>McpServersError</c> shape). Per-entry inert diagnostics are NOT
    /// produced here — the skills root walk stays in the per-pass delivery and the MCP
    /// entry validation stays in the discovery-time manifest branch (framework §3.2
    /// allocation invariants).</summary>
    public virtual ContributionDeclarationResult ParseDeclared(
        ContributionDeclarationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ContributionDeclarationResult.Empty;
    }

    /// <summary>Re-derives the kind's recorded interpretation products from a loaded
    /// descriptor (the manager records them per plugin so a synthetic registration's
    /// admission and the discovery read the same snapshot, including the
    /// remove-on-empty semantics that keep the transient
    /// <c>unknown_manifest_plugin</c> window). Empty when the kind records nothing.</summary>
    public virtual ContributionDeclarationResult DeclarationsFromDescriptor(
        PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return ContributionDeclarationResult.Empty;
    }

    /// <summary>Whether the kind currently holds a declarative registration face for
    /// the plugin: its extensions kind appears in the recorded entries, or its recorded
    /// interpretation is non-empty. Called under the host gate with the recorded
    /// snapshots.</summary>
    public bool HasDeclarativeRegistrationFace(
        IReadOnlyList<PluginExtensionEntry>? recordedExtensions,
        ContributionDeclarationResult? recordedDeclaration)
    {
        if (recordedExtensions is not null &&
            recordedExtensions.Any(entry => OwnsExtensionKind(entry.Kind)))
        {
            return true;
        }

        return recordedDeclaration is { } declaration &&
               (declaration.Descriptors.Count > 0 || declaration.PluginLevelError is not null);
    }
}

/// <summary>The declarations one kind claimed for one plugin at load time: the kind's
/// descriptors plus the optional plugin-level sticky error (the mcp.json data file's
/// failure rides here so the plugin stays loaded and its previous MCP contribution
/// stays discoverable-but-failed — the sticky-last-good rule).</summary>
internal sealed record ContributionDeclarationResult(
    IReadOnlyList<ContributionDescriptor> Descriptors,
    string? PluginLevelError)
{
    public static ContributionDeclarationResult Empty { get; } =
        new([], null);
}

/// <summary>The raw declaration material <see cref="ContributionKindContract.ParseDeclared"/>
/// receives: the plugin identity (the MCP server id prefix stamps it), the root the
/// data files resolve against, the canonicalized extensions entries, and the collected
/// data entries. Values only — the load path holds no callbacks.</summary>
internal sealed record ContributionDeclarationContext(
    string PluginId,
    string RootDirectory,
    IReadOnlyList<PluginExtensionEntry> ExtensionEntries,
    IReadOnlyList<PluginDataEntry> DataEntries)
{
    public PluginDataEntry? FindDataEntry(string name) =>
        DataEntries.FirstOrDefault(entry => entry.Name == name);
}

/// <summary>Unwraps the kind's descriptors back to their domain values (the inverse of
/// the contract's wrapping). A foreign descriptor type in the list is a wiring bug and
/// fails loudly instead of silently dropping entries.</summary>
internal static class ContributionDescriptorUnwrap
{
    public static IReadOnlyList<McpServerDefinition> McpServers(
        IReadOnlyList<ContributionDescriptor> descriptors)
    {
        return descriptors
            .Select(entry => entry is McpServerContribution server
                ? server.Definition
                : throw new InvalidOperationException(
                    $"The MCP declaration record carries a foreign descriptor type '{entry.GetType().Name}'."))
            .ToArray();
    }
}
