namespace Maieutics.Plugins.Contributions;

/// <summary>The closed kernel-side catalog of contribution kinds (plugin-contribution
/// framework §3.2): the single source of truth for the manifest grammar's extension
/// kinds and data-entry names, their canonical spellings, the generated unknown-name
/// diagnostics, and the routing metadata the host's generic paths consume. Each
/// grammar keeps its historical case policy, and both record spellings exactly as the
/// former directories did so persisted fingerprints stay byte-stable (ADR 0040
/// decision 7): extensions kinds match case-insensitively and record the canonical
/// spelling (the former <c>PluginExtensionKind.Canonicalize</c>), data-entry names
/// match exactly and record the declared spelling (the former
/// <c>PluginDataName.IsKnown</c> — canonicalizing a misspelled known name would flip
/// its data-domain bytes and silently revoke an existing approval). Adding a kind is
/// one closed contract class plus one entry in <see cref="Contracts"/>; nothing else
/// in the kernel routes on kind names.
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

    /// <summary>The contract interpreting the data-entry name (exact spelling), or null
    /// when unknown — unknown names are still collected, inert with a diagnostic. The
    /// comparison is Ordinal on purpose: a case-insensitive match would both change the
    /// data-domain fingerprint bytes of an existing misspelled declaration (approval
    /// revoked) and activate its formerly inert interpretation (ADR 0040 decision 7).</summary>
    public static ContributionKindContract? ByDataEntryName(string name)
    {
        foreach (var contract in Contracts)
            if (contract.DataEntryName is { } owned &&
                string.Equals(owned, name, StringComparison.Ordinal))
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

    /// <summary>The extensions grammar's known kinds, for the unknown-kind diagnostics:
    /// exactly the contracts that claim their own kind name in the extensions grammar.
    /// The criterion is grammar participation, not a compute form — a future
    /// declaration-only extensions kind is listed by claiming its name, while a
    /// data-entry-only kind (which claims no extensions entry) stays off the list, its
    /// name not being a valid extensions kind.</summary>
    public static string KnownExtensionKindsText() =>
        string.Join(
            ", ",
            Contracts.Where(contract => contract.OwnsExtensionKind(contract.KindName))
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
