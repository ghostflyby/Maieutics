using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Mcp;
using Maieutics.Plugins;

namespace Maieutics.Commands;

internal sealed class MaieuticsStatusProvider(
    IAgentSession session,
    IMaieuticsRuntimeConfiguration runtimeConfiguration,
    Workspace workspace,
    PluginHostManager pluginHosts,
    IMaieuticsMcpController mcpController,
    DenoReplRegistry replRegistry,
    Skills.SkillCatalog skillCatalog)
{
    private readonly IMaieuticsMcpController mcpController =
        mcpController ?? throw new ArgumentNullException(nameof(mcpController));

    private readonly PluginHostManager pluginHosts =
        pluginHosts ?? throw new ArgumentNullException(nameof(pluginHosts));

    private readonly DenoReplRegistry replRegistry =
        replRegistry ?? throw new ArgumentNullException(nameof(replRegistry));

    private readonly IMaieuticsRuntimeConfiguration runtimeConfiguration =
        runtimeConfiguration ?? throw new ArgumentNullException(nameof(runtimeConfiguration));

    private readonly IAgentSession session = session ?? throw new ArgumentNullException(nameof(session));

    private readonly Workspace workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

    internal MaieuticsStatusSnapshot Capture()
    {
        var skills = skillCatalog.Current;
        return new MaieuticsStatusSnapshot(
            runtimeConfiguration.GetStatus(),
            workspace.Capture(),
            pluginHosts.GetStatus(),
            mcpController.GetMcpServers(),
            replRegistry.List(session.Id),
            new SkillCatalogStatus(
                skills.Skills.Length,
                skills.Count(Skills.SkillSource.Workspace),
                skills.Count(Skills.SkillSource.User),
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
