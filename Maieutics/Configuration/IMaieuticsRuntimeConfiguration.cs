using Maieutics.Agent;
using Maieutics.Jupyter;

namespace Maieutics.Configuration;

internal interface IMaieuticsRuntimeConfiguration : IAgentRunProfileProvider, IMaieuticsModelProfileController
{
    string ConnectionFile { get; }

    long Version { get; }

    MaieuticsAgentKernelOptions GetKernelOptions();

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

    void SelectModelProfile(string profileId);

    void ResetModelProfile();
}

internal sealed record MaieuticsModelProfileSelection(
    string DefaultProfileId,
    string SelectedProfileId,
    bool HasSessionOverride,
    IReadOnlyList<MaieuticsModelProfileInfo> Profiles);

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
