using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Mcp;
using Maieutics.Plugins;
using Maieutics.Skills;

namespace Maieutics.Commands;

internal sealed class MaieuticsStatusProvider(
    IAgentSession session,
    IMaieuticsRuntimeConfiguration runtimeConfiguration,
    Workspace workspace,
    PluginHostManager pluginHosts,
    IMaieuticsMcpController mcpController,
    DenoReplRegistry replRegistry,
    SkillCatalog skillCatalog)
{
    private readonly IMaieuticsMcpController _mcpController =
        mcpController ?? throw new ArgumentNullException(nameof(mcpController));

    private readonly PluginHostManager _pluginHosts =
        pluginHosts ?? throw new ArgumentNullException(nameof(pluginHosts));

    private readonly DenoReplRegistry _replRegistry =
        replRegistry ?? throw new ArgumentNullException(nameof(replRegistry));

    private readonly IMaieuticsRuntimeConfiguration _runtimeConfiguration =
        runtimeConfiguration ?? throw new ArgumentNullException(nameof(runtimeConfiguration));

    private readonly IAgentSession _session = session ?? throw new ArgumentNullException(nameof(session));

    private readonly Workspace _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

    internal MaieuticsStatusSnapshot Capture()
    {
        var skills = skillCatalog.Current;
        return new MaieuticsStatusSnapshot(
            _runtimeConfiguration.GetStatus(),
            _workspace.Capture(),
            _pluginHosts.GetStatus(),
            _mcpController.GetMcpServers(),
            _replRegistry.List(_session.Id),
            new SkillCatalogStatus(
                skills.Skills.Length,
                skills.Count(SkillSource.Workspace),
                skills.Count(SkillSource.User),
                skills.Diagnostics.Length));
    }
}

/// <summary>The skill catalog line of the status snapshot (ADR 0039): active skill counts
/// per source plus the diagnostics count from the latest merged catalog.</summary>
internal sealed record SkillCatalogStatus(
    int TotalSkills,
    int WorkspaceSkills,
    int UserSkills,
    int Diagnostics);

internal sealed record MaieuticsStatusSnapshot(
    MaieuticsRuntimeStatus Runtime,
    WorkspaceSnapshot Workspace,
    PluginHostStatus Plugins,
    IReadOnlyList<MaieuticsMcpServerInfo> McpServers,
    DenoReplListResult Repls,
    SkillCatalogStatus? Skills = null);
