using Maieutics.Commands;
using Maieutics.Configuration;

namespace Maieutics.Product.Tests;

public sealed class MaieuticsCompletionTests
{
    private static readonly MaieuticsModelProfileInfo[] Profiles =
    [
        new("gpt", "openai", "OpenAI", "gpt-test", true, true),
        new("claude", "anthropic", "Anthropic", "claude-test", false, false)
    ];

    [Fact]
    public void RootCompletionExpandsAnExactCommandAndHandlesPrefixes()
    {
        var exact = Complete("%maieutics");
        Assert.Equal(new string[] { "%maieutics mcp", "%maieutics model", "%maieutics plugin", "%maieutics session", "%maieutics workspace" }, exact.Matches);
        Assert.Equal(0, exact.CursorStart);
        Assert.Equal(10, exact.CursorEnd);

        var prefix = Complete("%mai");
        Assert.Equal(new string[] { "%maieutics" }, prefix.Matches);
        Assert.Equal(0, prefix.CursorStart);
        Assert.Equal(4, prefix.CursorEnd);

        var afterRoot = Complete("%maieutics ");
        Assert.Equal(new string[] { "mcp", "model", "plugin", "session", "workspace" }, afterRoot.Matches);
        Assert.Equal(11, afterRoot.CursorStart);
        Assert.Equal(11, afterRoot.CursorEnd);
    }

    [Fact]
    public void SessionSubcommandsCompleteAtBoundariesAndIgnoreCase()
    {
        var canonical = Complete("%session ");
        Assert.Equal(new string[] { "current", "fork", "gc", "list", "new", "rename", "repair", "resume" }, canonical.Matches);
        Assert.Equal(9, canonical.CursorStart);
        Assert.Equal(9, canonical.CursorEnd);

        var legacy = Complete("%maieutics session ");
        Assert.Equal(new string[] { "current", "fork", "gc", "list", "new", "rename", "repair", "resume" }, legacy.Matches);

        var partial = Complete("%SESSION RES");
        Assert.Equal(new string[] { "resume" }, partial.Matches);
        Assert.Equal(9, partial.CursorStart);
        Assert.Equal(12, partial.CursorEnd);
    }

    [Fact]
    public void WorkspaceSubcommandsCompleteAtBoundariesAndIgnoreCase()
    {
        var commands = Complete("%maieutics workspace ");
        Assert.Equal(new string[] { "close", "current", "open" }, commands.Matches);
        Assert.Equal(21, commands.CursorStart);
        Assert.Equal(21, commands.CursorEnd);

        var partial = Complete("%MAIEUTICS WORKSPACE O");
        Assert.Equal(new string[] { "open" }, partial.Matches);
        Assert.Equal(21, partial.CursorStart);
        Assert.Equal(22, partial.CursorEnd);
    }

    [Fact]
    public void ModelSubcommandsCompleteAtBoundariesAndIgnoreCase()
    {
        var commands = Complete("%maieutics model ");
        Assert.Equal(new string[] { "available", "current", "list", "reset", "use" }, commands.Matches);
        Assert.Equal(17, commands.CursorStart);
        Assert.Equal(17, commands.CursorEnd);

        var partial = Complete("%MAIEUTICS MODEL U");
        Assert.Equal(new string[] { "use" }, partial.Matches);
        Assert.Equal(17, partial.CursorStart);
        Assert.Equal(18, partial.CursorEnd);
    }

    [Fact]
    public void McpSubcommandsCompleteForCanonicalAndLegacyCommands()
    {
        var legacy = Complete("%maieutics mcp ");
        Assert.Equal(new string[] { "list" }, legacy.Matches);
        Assert.Equal(15, legacy.CursorStart);
        Assert.Equal(15, legacy.CursorEnd);

        var canonical = Complete("%MCP L");
        Assert.Equal(new string[] { "list" }, canonical.Matches);
        Assert.Equal(5, canonical.CursorStart);
        Assert.Equal(6, canonical.CursorEnd);
    }

    [Fact]
    public void AvailableCompletesDynamicSourcesAndRefreshFlag()
    {
        var all = Complete(
            "%maieutics model available ",
            sourceIds: ["zeta", "vendor", "Vendor"]);
        Assert.Equal(new string[] { "--refresh", "vendor", "zeta" }, all.Matches);
        Assert.Equal(27, all.CursorStart);
        Assert.Equal(27, all.CursorEnd);

        var partial = Complete(
            "%maieutics model available ven",
            sourceIds: ["vendor"]);
        Assert.Equal(new string[] { "vendor" }, partial.Matches);
        Assert.Equal(27, partial.CursorStart);
        Assert.Equal(30, partial.CursorEnd);
    }

    [Fact]
    public void UseCompletesProfileIdsAndConfiguredModelIds()
    {
        var all = Complete("%maieutics model use ");
        Assert.Equal(new string[] { "claude", "claude-test", "gpt", "gpt-test" }, all.Matches);
        Assert.Equal(21, all.CursorStart);
        Assert.Equal(21, all.CursorEnd);

        var partial = Complete("%maieutics model use cl");
        Assert.Equal(new string[] { "claude", "claude-test" }, partial.Matches);
        Assert.Equal(21, partial.CursorStart);
        Assert.Equal(23, partial.CursorEnd);
    }

    [Fact]
    public void UseCompletesQualifiedAutomaticProfilesFromTheDiscoveryCache()
    {
        MaieuticsModelProfileInfo[] automaticProfiles =
        [
            new(
                "@vendor/model-alpha",
                "vendor",
                "Vendor",
                "model-alpha",
                false,
                false,
                true),
            new(
                "@other/model-alpha",
                "other",
                "Vendor",
                "model-alpha",
                false,
                false,
                true),
            new(
                "@vendor/model-beta",
                "vendor",
                "Vendor",
                "model-beta",
                false,
                false,
                true)
        ];

        var all = Complete(
            "%maieutics model use @",
            automaticProfiles: automaticProfiles);
        Assert.Equal(new string[] { "@other/model-alpha", "@vendor/model-alpha", "@vendor/model-beta" }, all.Matches);

        var source = Complete(
            "%maieutics model use @ven",
            automaticProfiles: automaticProfiles);
        Assert.Equal(new string[] { "@vendor/model-alpha", "@vendor/model-beta" }, source.Matches);

        var uniqueModel = Complete(
            "%maieutics model use model-",
            [],
            automaticProfiles);
        Assert.Equal(new string[] { "model-beta" }, uniqueModel.Matches);

        var selectedAfterCacheExpiry = Complete(
            "%maieutics model use @ven",
            [automaticProfiles[2] with { IsSelected = true }],
            []);
        Assert.Equal(new string[] { "@vendor/model-beta" }, selectedAfterCacheExpiry.Matches);
    }

    [Fact]
    public void CompletionKeepsUtf16TokenRangesAcrossAstralCharacters()
    {
        // The wire cursor is UTF-16 code units end to end: an astral character
        // (one code point, two code units) must not shift the token range.
        const string code = "%maieutics model use claude😀";
        var cursorUtf16Index = code.IndexOf("claude", StringComparison.Ordinal) + 2;

        var completion = MaieuticsCommandLanguage.Complete(code, cursorUtf16Index, Profiles, [], []);

        Assert.Equal(new string[] { "claude", "claude-test" }, completion.Matches);
        Assert.Equal(21, completion.TokenStart);
        Assert.Equal(code.Length, completion.TokenEnd);
    }

    [Fact]
    public void SourceOnlyAndEmptyConfigurationsKeepRelevantCompletionContexts()
    {
        var sourceOnly = Complete(
            "%maieutics model available v",
            [],
            sourceIds: ["vendor"]);
        Assert.Equal(new string[] { "vendor" }, sourceOnly.Matches);

        var commands = Complete(
            "%maieutics model ",
            [],
            sourceIds: []);
        Assert.Equal(new string[] { "available", "current", "list", "reset", "use" }, commands.Matches);

        var use = Complete(
            "%maieutics model use ",
            [],
            sourceIds: ["vendor"]);
        Assert.Empty(use.Matches);
    }

    [Fact]
    public void CompletionSortsAndDeduplicatesCandidatesWithoutTouchingUnrelatedText()
    {
        MaieuticsModelProfileInfo[] profiles =
        [
            new("Z-profile", "source", "Vendor", "shared", true, true),
            new("a-profile", "source", "Vendor", "Shared", false, false)
        ];

        var use = Complete("%maieutics model use ", profiles, []);
        Assert.Equal(new string[] { "a-profile", "shared", "Z-profile" }, use.Matches);

        var unrelated = Complete("ordinary text", profiles, sourceIds: ["vendor"]);
        Assert.Empty(unrelated.Matches);
    }

    [Fact]
    public void SlashDiscoveryCompletesCanonicalCommandsAndReplacesTheSlashToken()
    {
        var all = Complete("/");
        Assert.Equal(new string[] { "%mcp", "%model", "%plugin", "%session", "%status", "%workspace" }, all.Matches);
        Assert.Equal(0, all.CursorStart);
        Assert.Equal(1, all.CursorEnd);

        var model = Complete("/m");
        Assert.Equal(new string[] { "%mcp", "%model" }, model.Matches);
        Assert.Equal(0, model.CursorStart);
        Assert.Equal(2, model.CursorEnd);

        var workspace = Complete("/workspace");
        Assert.Equal(new string[] { "%workspace" }, workspace.Matches);
        Assert.Equal(0, workspace.CursorStart);
        Assert.Equal(10, workspace.CursorEnd);

        var caseInsensitive = Complete("/MODEL");
        Assert.Equal(new string[] { "%model" }, caseInsensitive.Matches);
    }

    [Fact]
    public void SlashDiscoveryDoesNotHijackPathLikeInput()
    {
        var path = Complete("/Users/ghostflyby/repos");
        Assert.Empty(path.Matches);

        var device = Complete("/dev/null");
        Assert.Empty(device.Matches);
    }

    [Fact]
    public void RootCompletionListsCanonicalCommandsAndLegacyRoot()
    {
        var all = Complete("%");
        Assert.Equal(new string[] { "%maieutics", "%mcp", "%model", "%plugin", "%session", "%status", "%workspace" }, all.Matches);

        var sharedPrefix = Complete("%m");
        Assert.Equal(new string[] { "%maieutics", "%mcp", "%model" }, sharedPrefix.Matches);

        var pluginPrefix = Complete("%p");
        Assert.Equal(new string[] { "%plugin" }, pluginPrefix.Matches);
    }

    [Fact]
    public void CanonicalCommandsCompleteSubcommandsAndBoundaries()
    {
        var mcp = Complete("%mcp ");
        Assert.Equal(new string[] { "list" }, mcp.Matches);
        Assert.Equal(5, mcp.CursorStart);
        Assert.Equal(5, mcp.CursorEnd);

        var plugin = Complete("%plugin ");
        Assert.Equal(new string[] { "approve", "list", "revoke" }, plugin.Matches);
        Assert.Equal(8, plugin.CursorStart);
        Assert.Equal(8, plugin.CursorEnd);

        var model = Complete("%model ");
        Assert.Equal(new string[] { "available", "current", "list", "reset", "use" }, model.Matches);
        Assert.Equal(7, model.CursorStart);
        Assert.Equal(7, model.CursorEnd);

        var partial = Complete("%MODEL U");
        Assert.Equal(new string[] { "use" }, partial.Matches);
        Assert.Equal(7, partial.CursorStart);
        Assert.Equal(8, partial.CursorEnd);

        var workspace = Complete("%workspace ");
        Assert.Equal(new string[] { "close", "current", "open" }, workspace.Matches);
        Assert.Equal(11, workspace.CursorStart);
        Assert.Equal(11, workspace.CursorEnd);

        var status = Complete("%status ");
        Assert.Empty(status.Matches);
        Assert.Equal(8, status.CursorStart);
        Assert.Equal(8, status.CursorEnd);
    }

    [Fact]
    public void CanonicalCommandsCompleteUseAndAvailable()
    {
        var use = Complete("%model use ");
        Assert.Equal(new string[] { "claude", "claude-test", "gpt", "gpt-test" }, use.Matches);
        Assert.Equal(11, use.CursorStart);
        Assert.Equal(11, use.CursorEnd);

        var available = Complete("%model available ", sourceIds: ["zeta", "vendor"]);
        Assert.Equal(new string[] { "--refresh", "vendor", "zeta" }, available.Matches);
        Assert.Equal(17, available.CursorStart);
        Assert.Equal(17, available.CursorEnd);

        MaieuticsModelProfileInfo[] automaticProfiles =
        [
            new(
                "@vendor/model-alpha",
                "vendor",
                "Vendor",
                "model-alpha",
                false,
                false,
                true),
            new(
                "@other/model-alpha",
                "other",
                "Vendor",
                "model-alpha",
                false,
                false,
                true),
            new(
                "@vendor/model-beta",
                "vendor",
                "Vendor",
                "model-beta",
                false,
                false,
                true)
        ];

        var qualified = Complete("%model use @", automaticProfiles: automaticProfiles);
        Assert.Equal(new string[] { "@other/model-alpha", "@vendor/model-alpha", "@vendor/model-beta" }, qualified.Matches);
    }

    [Fact]
    public void CanonicalCompletionKeepsUtf16TokenRangesAcrossAstralCharacters()
    {
        const string code = "%model use claude😀";
        var cursorUtf16Index = code.IndexOf("claude", StringComparison.Ordinal) + 2;

        var completion = MaieuticsCommandLanguage.Complete(code, cursorUtf16Index, Profiles, [], []);

        Assert.Equal(new string[] { "claude", "claude-test" }, completion.Matches);
        Assert.Equal(11, completion.TokenStart);
        Assert.Equal(code.Length, completion.TokenEnd);
    }

    // CursorStart/CursorEnd preserve the assertion surface this file has always
    // used; the product values underneath are the UTF-16 TokenStart/TokenEnd.
    private sealed record CompletionResult(string[] Matches, int CursorStart, int CursorEnd);

    private static CompletionResult Complete(
        string code,
        IReadOnlyList<MaieuticsModelProfileInfo>? profiles = null,
        IReadOnlyList<MaieuticsModelProfileInfo>? automaticProfiles = null,
        IReadOnlyList<string>? sourceIds = null)
    {
        var completion = MaieuticsCommandLanguage.Complete(
            code,
            code.Length,
            profiles ?? Profiles,
            automaticProfiles ?? [],
            sourceIds ?? []);
        return new CompletionResult(completion.Matches, completion.TokenStart, completion.TokenEnd);
    }
}
