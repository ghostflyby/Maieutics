using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Execution;
using Maieutics.Mcp;
using Maieutics.Plugins;

namespace Maieutics.Jupyter;

internal sealed class MaieuticsStatusProvider(
    IAgentSession session,
    IMaieuticsRuntimeConfiguration runtimeConfiguration,
    Workspace workspace,
    PluginHostManager pluginHosts,
    IMaieuticsMcpController mcpController,
    DenoReplRegistry replRegistry)
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
        return new MaieuticsStatusSnapshot(
            runtimeConfiguration.GetStatus(),
            workspace.Capture(),
            pluginHosts.GetStatus(),
            mcpController.GetMcpServers(),
            replRegistry.List(session.Id));
    }
}

internal sealed record MaieuticsStatusSnapshot(
    MaieuticsRuntimeStatus Runtime,
    WorkspaceSnapshot Workspace,
    PluginHostStatus Plugins,
    IReadOnlyList<MaieuticsMcpServerInfo> McpServers,
    DenoReplListResult Repls);
