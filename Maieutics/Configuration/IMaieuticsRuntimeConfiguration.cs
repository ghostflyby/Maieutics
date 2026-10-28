using Maieutics.Agent;

namespace Maieutics.Configuration;

internal interface IMaieuticsRuntimeConfiguration : IAgentRunProfileProvider, IMaieuticsModelProfileController
{
    long Version { get; }

    /// <summary>Acquires a run profile lease for one explicit configured profile id,
    /// bypassing the process selection (the per-session override path). Returns
    /// <see langword="null" /> when the id no longer resolves.</summary>
    Task<IAgentRunProfileLease?> AcquireProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    MaieuticsRuntimeStatus GetStatus();

    /// <summary>Returns models discovered from each model source's API endpoint.</summary>
    /// <param name="sourceId">Optional source identifier to filter by.</param>
    /// <param name="refresh">When true, bypasses the cache and re-fetches from the API.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The list of discovered model groups, one per source.</returns>
    ValueTask<IReadOnlyList<DiscoveredModelGroup>> GetDiscoveredModelsAsync(
        string? sourceId = null,
        bool refresh = false,
        CancellationToken cancellationToken = default);
}

internal interface IMaieuticsModelProfileController
{
    MaieuticsModelProfileSelection GetModelProfileSelection();

    IReadOnlyList<MaieuticsModelProfileInfo> GetCachedAutomaticModelProfiles();

    IReadOnlyList<string> GetModelSourceIds();

    /// <summary>
    ///     Resolves one profile-selection value exactly like <see cref="SelectModelProfileAsync" /> — a
    ///     configured profile id, a model name matching exactly one configured profile, or an automatic
    ///     <c>@source/model</c> selector from the discovery cache — without changing the process
    ///     selection. An automatic selector registers a per-selector generation so later explicit
    ///     acquisitions resolve from the registry instead of the expiring discovery cache.
    /// </summary>
    /// <param name="profileId">The selection value a session override stores.</param>
    /// <returns>The resolved profile id or automatic selector to keep as the override.</returns>
    /// <exception cref="ArgumentException">
    ///     The value matches no profile, matches several profiles, or no cached automatic model
    ///     carries it.
    /// </exception>
    string PrepareProfileOverride(string profileId);

    /// <summary>
    ///     Gets the registered automatic profile for one selection value, or <see langword="null" />
    ///     when the value is not a registered (still-valid) automatic selector. Registered entries back
    ///     session overrides, so surfaces that resolve a stored override id — the
    ///     <c>%model current</c> render — can describe it.
    /// </summary>
    MaieuticsModelProfileInfo? GetRegisteredAutomaticProfile(string profileId);

    ValueTask SelectModelProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    void ResetModelProfile();
}

internal sealed record MaieuticsModelProfileSelection(
    string DefaultProfileId,
    string SelectedProfileId,
    bool HasSessionOverride,
    IReadOnlyList<MaieuticsModelProfileInfo> Profiles);

internal sealed record MaieuticsRuntimeStatus(
    long Version,
    MaieuticsModelProfileSelection ModelSelection,
    MaieuticsConfigurationReloadInfo LastReload,
    IReadOnlyList<MaieuticsCapabilityInfo> CapabilityProfiles);

internal sealed record MaieuticsCapabilityInfo(
    string SourceId,
    string ModelId,
    bool Matched,
    bool KnownVendor,
    AgentModelCapabilities Capabilities,
    IReadOnlyList<string> PotentialCapabilities,
    IReadOnlyList<string> HostedCapabilities);

internal sealed record MaieuticsConfigurationReloadInfo(
    long Attempt,
    MaieuticsConfigurationReloadOutcome Outcome,
    long ActiveVersion);

internal enum MaieuticsConfigurationReloadOutcome
{
    NotAttempted,
    Unchanged,
    Applied,
    Rejected
}

internal sealed record MaieuticsModelProfileInfo(
    string Id,
    string SourceId,
    string Provider,
    string Model,
    bool IsDefault,
    bool IsSelected,
    bool IsAutomatic = false);

internal static class MaieuticsAutomaticProfileSelector
{
    internal static string Format(string sourceId, string model)
    {
        return $"@{sourceId}/{model}";
    }

    internal static bool TryParse(string value, out string sourceId, out string model)
    {
        sourceId = string.Empty;
        model = string.Empty;
        if (value.Length < 4 || value[0] != '@') return false;

        var separator = value.IndexOf('/');
        if (separator <= 1 || separator == value.Length - 1) return false;

        sourceId = value[1..separator];
        model = value[(separator + 1)..];
        return !sourceId.Any(char.IsWhiteSpace) && !model.Any(char.IsWhiteSpace);
    }
}

/// <summary>Groups models discovered from one model source.</summary>
internal sealed record DiscoveredModelGroup(
    string SourceId,
    string Provider,
    ModelDiscoveryFailureKind? Failure,
    IReadOnlyList<AgentModelDescriptor> Models);

internal enum ModelDiscoveryFailureKind
{
    ProviderError
}
