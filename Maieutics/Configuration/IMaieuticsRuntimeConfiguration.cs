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
    bool IsSelected);

/// <summary>Groups models discovered from one model source.</summary>
internal sealed record DiscoveredModelGroup(
    string SourceId,
    string Provider,
    string? Error,
    IReadOnlyList<AgentModelDescriptor> Models);