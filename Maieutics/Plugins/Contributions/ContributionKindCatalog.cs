namespace Maieutics.Plugins.Contributions;

/// <summary>The closed kernel-side catalog of contribution kinds (plugin-contribution
/// framework §3.2): the single source of truth for the manifest grammar's extension
/// kinds and data-entry names, their canonical spellings, the generated unknown-name
/// diagnostics, and the routing metadata the host's generic paths consume. Lookups are
/// case-insensitive and record the canonical spelling — the unified case policy of the
/// two formerly divergent grammar directories (extensions kinds were case-insensitive,
/// data names were exact-match). Adding a kind is one closed contract class plus one
/// entry in <see cref="Contracts"/>; nothing else in the kernel routes on kind names.
/// </summary>
internal static class ContributionKindCatalog
{
    public static IReadOnlyList<ContributionKindContract> Contracts { get; } =
    [
        McpDiscoverContributionKind.Instance,
        SkillsContributionKind.Instance,
        UiContributionKind.Instance,
    ];

    /// <summary>The contract claiming the extensions kind (any spelling), or null when
    /// unknown — unknown kinds are dropped with a diagnostic, never honored.</summary>
    public static ContributionKindContract? ByExtensionKind(string kind)
    {
        foreach (var contract in Contracts)
            if (contract.OwnsExtensionKind(kind))
                return contract;

        return null;
    }

    /// <summary>The contract interpreting the data-entry name (any spelling), or null
    /// when unknown — unknown names are still collected, inert with a diagnostic.</summary>
    public static ContributionKindContract? ByDataEntryName(string name)
    {
        foreach (var contract in Contracts)
            if (contract.DataEntryName is { } owned &&
                string.Equals(owned, name, StringComparison.OrdinalIgnoreCase))
            {
                return contract;
            }

        return null;
    }

    /// <summary>The contract whose runtime publish flows through the capability name,
    /// or null when no kind publishes through it.</summary>
    public static ContributionKindContract? ByPublishCapability(string capability)
    {
        foreach (var contract in Contracts)
            if (contract.PublishCapability is { } owned &&
                string.Equals(owned, capability, StringComparison.Ordinal))
            {
                return contract;
            }

        return null;
    }

    /// <summary>The canonical spelling of an extensions kind, or null when unknown.</summary>
    public static string? CanonicalExtensionKind(string kind) =>
        ByExtensionKind(kind)?.KindName;

    /// <summary>The canonical spelling of a data-entry name, or null when unknown.</summary>
    public static string? CanonicalDataEntryName(string name) =>
        ByDataEntryName(name)?.DataEntryName;

    public static string KnownExtensionKindsText() =>
        string.Join(
            ", ",
            Contracts.Where(contract => contract.HasComputeForm)
                .Select(contract => contract.KindName));

    public static string KnownDataEntryNamesText() =>
        string.Join(
            ", ",
            Contracts.Where(contract => contract.DataEntryName is not null)
                .Select(contract => contract.DataEntryName));

    /// <summary>The generated diagnostic for an unknown extensions kind. The former
    /// hand-written text carried a case-advice sentence ("the lowercase 'skills' form
    /// is recommended"); the catalog text drops it — the only intended wording change
    /// of B 期 on this surface (diagnostics never enter any fingerprint domain).</summary>
    public static string UnknownExtensionKindDiagnostic(string kind) =>
        $"Unknown extension kind '{kind}' is declared but not supported by this kernel; " +
        $"it is ignored (known kinds: {KnownExtensionKindsText()}).";

    /// <summary>The generated diagnostic for an unknown string-valued data entry. The
    /// catalog enumerates every known name, so a newly catalogued kind can no longer be
    /// missing from the list (the former text listed only <c>mcp</c> while <c>ui</c>
    /// was already catalogued).</summary>
    public static string UnknownDataEntryDiagnostic(string name) =>
        $"Unknown data entry '{name}' is declared but not supported by this kernel; " +
        $"it is collected but inert (known names: {KnownDataEntryNamesText()}).";

    /// <summary>The generated structural violation for an entrypoint section that is
    /// neither the worker map nor a string-valued catalogued data entry.</summary>
    public static string UnknownEntrypointSectionDiagnostic(string name) =>
        $"Unknown entrypoint section '{name}'. Worker declarations live under 'worker'; " +
        $"catalogued data entries take a string path (known names: {KnownDataEntryNamesText()}).";
}
