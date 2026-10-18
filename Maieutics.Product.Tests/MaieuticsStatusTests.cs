using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Configuration;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Mcp;
using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

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
                    7),
                []),
            new WorkspaceSnapshot("/secret/workspace", 3, null),
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

        Assert.Contains("Configuration: version `7`", markdown);
        Assert.Contains("last reload `rejected`", markdown);
        Assert.Contains("profile `profile`", markdown);
        Assert.Contains("Capabilities: no model profiles", markdown);
        Assert.Contains("Workspace: fixed home", markdown);
        Assert.Contains("Plugins: `Exited`", markdown);
        Assert.Contains("`server`: `Reconnecting`", markdown);
        Assert.Contains("`default`: generation `4`, state `busy` (default)", markdown);
        Assert.DoesNotContain("/secret/workspace", markdown);
        Assert.DoesNotContain("/secret/repl", markdown);
    }

    [Fact]
    public void RenderingIncludesCapabilityProfiles()
    {
        var snapshot = new MaieuticsStatusSnapshot(
            new MaieuticsRuntimeStatus(
                7,
                new MaieuticsModelProfileSelection("profile", "profile", false, []),
                new MaieuticsConfigurationReloadInfo(
                    9,
                    MaieuticsConfigurationReloadOutcome.Applied,
                    7),
                [new MaieuticsCapabilityInfo(
                    "source",
                    "model",
                    true,
                    true,
                    AgentModelCapabilities.StreamingText,
                    ["Shell", "WebSearch"],
                    ["FileSearch", "WebSearch"])]),
            new WorkspaceSnapshot("/secret/workspace", 3, null),
            new PluginHostStatus(PluginHostState.Exited, 2, 4, true, false),
            [],
            new DenoReplListResult([]));

        var markdown = MaieuticsStatusRenderer.Render(snapshot);

        Assert.Contains("source `source`", markdown);
        Assert.Contains("model `model`", markdown);
        Assert.Contains("StreamingText", markdown);
        Assert.Contains("hosted `FileSearch, WebSearch`", markdown);
        Assert.Contains("potential `Shell, WebSearch`", markdown);
        Assert.Contains("(endpoint profile matched)", markdown);
    }

    [Fact]
    public void RenderingDistinguishesKnownVendorAndDeclaredBaseline()
    {
        var snapshot = new MaieuticsStatusSnapshot(
            new MaieuticsRuntimeStatus(
                7,
                new MaieuticsModelProfileSelection("profile", "profile", false, []),
                new MaieuticsConfigurationReloadInfo(
                    9,
                    MaieuticsConfigurationReloadOutcome.Applied,
                    7),
                [
                    new MaieuticsCapabilityInfo(
                        "known",
                        "model",
                        false,
                        true,
                        AgentModelCapabilities.StreamingText,
                        ["WebSearch"],
                        ["WebSearch"]),
                    new MaieuticsCapabilityInfo(
                        "unknown",
                        "model",
                        false,
                        false,
                        AgentModelCapabilities.StreamingText,
                        [],
                        [])
                ]),
            new WorkspaceSnapshot("/secret/workspace", 3, null),
            new PluginHostStatus(PluginHostState.Exited, 2, 4, true, false),
            [],
            new DenoReplListResult([]));

        var markdown = MaieuticsStatusRenderer.Render(snapshot);

        Assert.Contains("source `known`", markdown);
        Assert.Contains("(known vendor)", markdown);
        Assert.Contains("source `unknown`", markdown);
        Assert.Contains("(declared baseline)", markdown);
    }

    [Fact]
    public void RenderingIncludesSkillCatalogLineWhenPresent()
    {
        var snapshot = new MaieuticsStatusSnapshot(
            new MaieuticsRuntimeStatus(
                7,
                new MaieuticsModelProfileSelection("profile", "profile", false, []),
                new MaieuticsConfigurationReloadInfo(
                    9,
                    MaieuticsConfigurationReloadOutcome.Applied,
                    7),
                []),
            new WorkspaceSnapshot("/secret/workspace", 3, null),
            new PluginHostStatus(PluginHostState.Exited, 2, 4, true, false),
            [],
            new DenoReplListResult([]),
            new SkillCatalogStatus(3, 2, 1, 4));

        var markdown = MaieuticsStatusRenderer.Render(snapshot);

        Assert.Contains("- Skills: `3` (workspace `2`, user `1`, diagnostics `4`)", markdown);
    }

    [Fact]
    public void RenderingOmitsSkillCatalogLineWhenSnapshotCarriesNone()
    {
        var snapshot = new MaieuticsStatusSnapshot(
            new MaieuticsRuntimeStatus(
                7,
                new MaieuticsModelProfileSelection("profile", "profile", false, []),
                new MaieuticsConfigurationReloadInfo(
                    9,
                    MaieuticsConfigurationReloadOutcome.Applied,
                    7),
                []),
            new WorkspaceSnapshot("/secret/workspace", 3, null),
            new PluginHostStatus(PluginHostState.Exited, 2, 4, true, false),
            [],
            new DenoReplListResult([]));

        var markdown = MaieuticsStatusRenderer.Render(snapshot);

        Assert.DoesNotContain("Skills:", markdown);
    }
}
