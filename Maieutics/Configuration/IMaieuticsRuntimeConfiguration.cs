using Maieutics.Agent;
using Maieutics.Jupyter;

namespace Maieutics.Configuration;

internal interface IMaieuticsRuntimeConfiguration : IAgentRunProfileProvider, IMaieuticsModelProfileController
{
    string ConnectionFile { get; }

    long Version { get; }

    MaieuticsAgentKernelOptions GetKernelOptions();
}

internal interface IMaieuticsModelProfileController
{
    MaieuticsModelProfileSelection GetModelProfileSelection();

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