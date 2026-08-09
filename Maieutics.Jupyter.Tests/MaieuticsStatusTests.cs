using FluentAssertions;
using Maieutics.Configuration;
using Maieutics.Execution;
using Maieutics.Jupyter;
using Maieutics.Mcp;
using Maieutics.Plugins;

namespace Maieutics.Jupyter.Tests;

public sealed class MaieuticsStatusTests
{
    [Fact]
    public void RenderingIncludesOperationalStateAndRedactsWorkspaceAndReplPaths()
    {
        var snapshot = new MaieuticsStatusSnapshot(
            new MaieuticsRuntimeStatus(
                7,
                new MaieuticsModelProfileSelection(
                    "profile",
                    "profile",
                    false,
                    [new MaieuticsModelProfileInfo(
                        "profile",
                        "source",
                        "Provider",
                        "model",
                        true,
                        true)]),
                new MaieuticsConfigurationReloadInfo(
                    9,
                    MaieuticsConfigurationReloadOutcome.Rejected,
                    7)),
            new WorkspaceSnapshot("/secret/workspace", 3, true),
            new PluginHostStatus(PluginHostState.Exited, 2, 4, true, false),
            [new MaieuticsMcpServerInfo(
                "server",
                "Http",
                MaieuticsMcpServerState.Reconnecting,
                TimeSpan.FromSeconds(2),
                [new MaieuticsMcpToolInfo("remote", "exposed", true)])],
            new DenoReplListResult([
                new DenoReplSessionResult("default", 4, "busy", "/secret/repl", true)
            ]));

        var markdown = MaieuticsStatusRenderer.Render(snapshot);

        markdown.Should()
            .Contain("Configuration: version `7`")
            .And.Contain("last reload `rejected`")
            .And.Contain("profile `profile`")
            .And.Contain("Workspace: session override")
            .And.Contain("Plugins: `Exited`")
            .And.Contain("`server`: `Reconnecting`")
            .And.Contain("`default`: generation `4`, state `busy` (default)")
            .And.NotContain("/secret/workspace")
            .And.NotContain("/secret/repl");
    }
}
