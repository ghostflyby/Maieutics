using FluentAssertions;
using Maieutics.Configuration;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class MaieuticsCompletionTests
{
    private static readonly MaieuticsModelProfileInfo[] Profiles =
    [
        new("gpt", "openai", "OpenAI", "gpt-test", IsDefault: true, IsSelected: true),
        new("claude", "anthropic", "Anthropic", "claude-test", IsDefault: false, IsSelected: false)
    ];

    [Fact]
    public void RootCompletionExpandsAnExactCommandAndHandlesPrefixes()
    {
        var exact = Complete("%maieutics");
        exact.Matches.Should().Equal("%maieutics model");
        exact.CursorStart.Should().Be(0);
        exact.CursorEnd.Should().Be(10);

        var prefix = Complete("%mai");
        prefix.Matches.Should().Equal("%maieutics");
        prefix.CursorStart.Should().Be(0);
        prefix.CursorEnd.Should().Be(4);

        var afterRoot = Complete("%maieutics ");
        afterRoot.Matches.Should().Equal("model");
        afterRoot.CursorStart.Should().Be(11);
        afterRoot.CursorEnd.Should().Be(11);
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
    public void CompletionUsesUnicodeCodePointOffsetsForCursorRanges()
    {
        const string code = "%maieutics model use claude😀";
        var cursorUtf16Index = code.IndexOf("claude", StringComparison.Ordinal) + 2;
        var request = new JupyterCompleteRequest(
            code,
            JupyterCursorPosition.FromUtf16Index(code, cursorUtf16Index));

        var result = MaieuticsCommandLanguage.Complete(request, Profiles, []);

        result.Matches.Should().Equal("claude", "claude-test");
        result.CursorStart.Should().Be(21);
        result.CursorEnd.Should().Be(28);
    }

    [Fact]
    public void SourceOnlyAndEmptyConfigurationsKeepRelevantCompletionContexts()
    {
        var sourceOnly = Complete(
            "%maieutics model available v",
            profiles: [],
            sourceIds: ["vendor"]);
        sourceOnly.Matches.Should().Equal("vendor");

        var commands = Complete(
            "%maieutics model ",
            profiles: [],
            sourceIds: []);
        commands.Matches.Should().Equal("available", "current", "list", "reset", "use");

        var use = Complete(
            "%maieutics model use ",
            profiles: [],
            sourceIds: ["vendor"]);
        use.Matches.Should().BeEmpty();
    }

    [Fact]
    public void CompletionSortsAndDeduplicatesCandidatesWithoutTouchingUnrelatedText()
    {
        MaieuticsModelProfileInfo[] profiles =
        [
            new("Z-profile", "source", "Vendor", "shared", IsDefault: true, IsSelected: true),
            new("a-profile", "source", "Vendor", "Shared", IsDefault: false, IsSelected: false)
        ];

        var use = Complete("%maieutics model use ", profiles, []);
        use.Matches.Should().Equal("a-profile", "shared", "Z-profile");

        var unrelated = Complete("ordinary text", profiles, ["vendor"]);
        unrelated.Matches.Should().BeEmpty();
    }

    private static JupyterCompletionResult Complete(
        string code,
        IReadOnlyList<MaieuticsModelProfileInfo>? profiles = null,
        IReadOnlyList<string>? sourceIds = null) =>
        MaieuticsCommandLanguage.Complete(
            new JupyterCompleteRequest(
                code,
                JupyterCursorPosition.FromUtf16Index(code, code.Length)),
            profiles ?? Profiles,
            sourceIds ?? []);
}