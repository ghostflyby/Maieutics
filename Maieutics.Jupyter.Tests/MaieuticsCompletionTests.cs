using FluentAssertions;
using Maieutics.Configuration;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

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
        exact.Matches.Should().Equal("%maieutics mcp", "%maieutics model", "%maieutics session", "%maieutics workspace");
        exact.CursorStart.Should().Be(0);
        exact.CursorEnd.Should().Be(10);

        var prefix = Complete("%mai");
        prefix.Matches.Should().Equal("%maieutics");
        prefix.CursorStart.Should().Be(0);
        prefix.CursorEnd.Should().Be(4);

        var afterRoot = Complete("%maieutics ");
        afterRoot.Matches.Should().Equal("mcp", "model", "session", "workspace");
        afterRoot.CursorStart.Should().Be(11);
        afterRoot.CursorEnd.Should().Be(11);
    }

    [Fact]
    public void SessionSubcommandsCompleteAtBoundariesAndIgnoreCase()
    {
        var canonical = Complete("%session ");
        canonical.Matches.Should().Equal("current", "gc", "list", "new", "repair", "resume");
        canonical.CursorStart.Should().Be(9);
        canonical.CursorEnd.Should().Be(9);

        var legacy = Complete("%maieutics session ");
        legacy.Matches.Should().Equal("current", "gc", "list", "new", "repair", "resume");

        var partial = Complete("%SESSION RES");
        partial.Matches.Should().Equal("resume");
        partial.CursorStart.Should().Be(9);
        partial.CursorEnd.Should().Be(12);
    }

    [Fact]
    public void WorkspaceSubcommandsCompleteAtBoundariesAndIgnoreCase()
    {
        var commands = Complete("%maieutics workspace ");
        commands.Matches.Should().Equal("current", "reset", "use");
        commands.CursorStart.Should().Be(21);
        commands.CursorEnd.Should().Be(21);

        var partial = Complete("%MAIEUTICS WORKSPACE R");
        partial.Matches.Should().Equal("reset");
        partial.CursorStart.Should().Be(21);
        partial.CursorEnd.Should().Be(22);
    }

    [Fact]
    public void ModelSubcommandsCompleteAtBoundariesAndIgnoreCase()
    {
        var commands = Complete("%maieutics model ");
        commands.Matches.Should().Equal("available", "current", "list", "reset", "use");
        commands.CursorStart.Should().Be(17);
        commands.CursorEnd.Should().Be(17);

        var partial = Complete("%MAIEUTICS MODEL U");
        partial.Matches.Should().Equal("use");
        partial.CursorStart.Should().Be(17);
        partial.CursorEnd.Should().Be(18);
    }

    [Fact]
    public void McpSubcommandsCompleteForCanonicalAndLegacyCommands()
    {
        var legacy = Complete("%maieutics mcp ");
        legacy.Matches.Should().Equal("list");
        legacy.CursorStart.Should().Be(15);
        legacy.CursorEnd.Should().Be(15);

        var canonical = Complete("%MCP L");
        canonical.Matches.Should().Equal("list");
        canonical.CursorStart.Should().Be(5);
        canonical.CursorEnd.Should().Be(6);
    }

    [Fact]
    public void AvailableCompletesDynamicSourcesAndRefreshFlag()
    {
        var all = Complete(
            "%maieutics model available ",
            sourceIds: ["zeta", "vendor", "Vendor"]);
        all.Matches.Should().Equal("--refresh", "vendor", "zeta");
        all.CursorStart.Should().Be(27);
        all.CursorEnd.Should().Be(27);

        var partial = Complete(
            "%maieutics model available ven",
            sourceIds: ["vendor"]);
        partial.Matches.Should().Equal("vendor");
        partial.CursorStart.Should().Be(27);
        partial.CursorEnd.Should().Be(30);
    }

    [Fact]
    public void UseCompletesProfileIdsAndConfiguredModelIds()
    {
        var all = Complete("%maieutics model use ");
        all.Matches.Should().Equal("claude", "claude-test", "gpt", "gpt-test");
        all.CursorStart.Should().Be(21);
        all.CursorEnd.Should().Be(21);

        var partial = Complete("%maieutics model use cl");
        partial.Matches.Should().Equal("claude", "claude-test");
        partial.CursorStart.Should().Be(21);
        partial.CursorEnd.Should().Be(23);
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
        all.Matches.Should().Equal("@other/model-alpha", "@vendor/model-alpha", "@vendor/model-beta");

        var source = Complete(
            "%maieutics model use @ven",
            automaticProfiles: automaticProfiles);
        source.Matches.Should().Equal("@vendor/model-alpha", "@vendor/model-beta");

        var uniqueModel = Complete(
            "%maieutics model use model-",
            [],
            automaticProfiles);
        uniqueModel.Matches.Should().Equal("model-beta");

        var selectedAfterCacheExpiry = Complete(
            "%maieutics model use @ven",
            [automaticProfiles[2] with { IsSelected = true }],
            []);
        selectedAfterCacheExpiry.Matches.Should().Equal("@vendor/model-beta");
    }

    [Fact]
    public void CompletionUsesUnicodeCodePointOffsetsForCursorRanges()
    {
        const string code = "%maieutics model use claude😀";
        var cursorUtf16Index = code.IndexOf("claude", StringComparison.Ordinal) + 2;
        var request = new JupyterCompleteRequest(
            code,
            JupyterCursorPosition.FromUtf16Index(code, cursorUtf16Index));

        var result = MaieuticsCommandLanguage.Complete(request, Profiles, [], []);

        result.Matches.Should().Equal("claude", "claude-test");
        result.CursorStart.Should().Be(21);
        result.CursorEnd.Should().Be(28);
    }

    [Fact]
    public void SourceOnlyAndEmptyConfigurationsKeepRelevantCompletionContexts()
    {
        var sourceOnly = Complete(
            "%maieutics model available v",
            [],
            sourceIds: ["vendor"]);
        sourceOnly.Matches.Should().Equal("vendor");

        var commands = Complete(
            "%maieutics model ",
            [],
            sourceIds: []);
        commands.Matches.Should().Equal("available", "current", "list", "reset", "use");

        var use = Complete(
            "%maieutics model use ",
            [],
            sourceIds: ["vendor"]);
        use.Matches.Should().BeEmpty();
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
        use.Matches.Should().Equal("a-profile", "shared", "Z-profile");

        var unrelated = Complete("ordinary text", profiles, sourceIds: ["vendor"]);
        unrelated.Matches.Should().BeEmpty();
    }

    [Fact]
    public void SlashDiscoveryCompletesCanonicalCommandsAndReplacesTheSlashToken()
    {
        var all = Complete("/");
        all.Matches.Should().Equal("%mcp", "%model", "%session", "%status", "%workspace");
        all.CursorStart.Should().Be(0);
        all.CursorEnd.Should().Be(1);

        var model = Complete("/m");
        model.Matches.Should().Equal("%mcp", "%model");
        model.CursorStart.Should().Be(0);
        model.CursorEnd.Should().Be(2);

        var workspace = Complete("/workspace");
        workspace.Matches.Should().Equal("%workspace");
        workspace.CursorStart.Should().Be(0);
        workspace.CursorEnd.Should().Be(10);

        var caseInsensitive = Complete("/MODEL");
        caseInsensitive.Matches.Should().Equal("%model");
    }

    [Fact]
    public void SlashDiscoveryDoesNotHijackPathLikeInput()
    {
        var path = Complete("/Users/ghostflyby/repos");
        path.Matches.Should().BeEmpty();

        var device = Complete("/dev/null");
        device.Matches.Should().BeEmpty();
    }

    [Fact]
    public void RootCompletionListsCanonicalCommandsAndLegacyRoot()
    {
        var all = Complete("%");
        all.Matches.Should().Equal("%maieutics", "%mcp", "%model", "%session", "%status", "%workspace");

        var sharedPrefix = Complete("%m");
        sharedPrefix.Matches.Should().Equal("%maieutics", "%mcp", "%model");
    }

    [Fact]
    public void CanonicalCommandsCompleteSubcommandsAndBoundaries()
    {
        var mcp = Complete("%mcp ");
        mcp.Matches.Should().Equal("list");
        mcp.CursorStart.Should().Be(5);
        mcp.CursorEnd.Should().Be(5);

        var model = Complete("%model ");
        model.Matches.Should().Equal("available", "current", "list", "reset", "use");
        model.CursorStart.Should().Be(7);
        model.CursorEnd.Should().Be(7);

        var partial = Complete("%MODEL U");
        partial.Matches.Should().Equal("use");
        partial.CursorStart.Should().Be(7);
        partial.CursorEnd.Should().Be(8);

        var workspace = Complete("%workspace ");
        workspace.Matches.Should().Equal("current", "reset", "use");
        workspace.CursorStart.Should().Be(11);
        workspace.CursorEnd.Should().Be(11);

        var status = Complete("%status ");
        status.Matches.Should().BeEmpty();
        status.CursorStart.Should().Be(8);
        status.CursorEnd.Should().Be(8);
    }

    [Fact]
    public void CanonicalCommandsCompleteUseAndAvailable()
    {
        var use = Complete("%model use ");
        use.Matches.Should().Equal("claude", "claude-test", "gpt", "gpt-test");
        use.CursorStart.Should().Be(11);
        use.CursorEnd.Should().Be(11);

        var available = Complete("%model available ", sourceIds: ["zeta", "vendor"]);
        available.Matches.Should().Equal("--refresh", "vendor", "zeta");
        available.CursorStart.Should().Be(17);
        available.CursorEnd.Should().Be(17);

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
        qualified.Matches.Should().Equal("@other/model-alpha", "@vendor/model-alpha", "@vendor/model-beta");
    }

    [Fact]
    public void CanonicalCompletionUsesUnicodeCodePointOffsets()
    {
        const string code = "%model use claude😀";
        var cursorUtf16Index = code.IndexOf("claude", StringComparison.Ordinal) + 2;
        var request = new JupyterCompleteRequest(
            code,
            JupyterCursorPosition.FromUtf16Index(code, cursorUtf16Index));

        var result = MaieuticsCommandLanguage.Complete(request, Profiles, [], []);

        result.Matches.Should().Equal("claude", "claude-test");
        result.CursorStart.Should().Be(11);
        result.CursorEnd.Should().Be(18);
    }

    private static JupyterCompletionResult Complete(
        string code,
        IReadOnlyList<MaieuticsModelProfileInfo>? profiles = null,
        IReadOnlyList<MaieuticsModelProfileInfo>? automaticProfiles = null,
        IReadOnlyList<string>? sourceIds = null)
    {
        return MaieuticsCommandLanguage.Complete(
            new JupyterCompleteRequest(
                code,
                JupyterCursorPosition.FromUtf16Index(code, code.Length)),
            profiles ?? Profiles,
            automaticProfiles ?? [],
            sourceIds ?? []);
    }
}
