using FluentAssertions;
using Maieutics.Frontend;
using Maieutics.Mcp;

namespace Maieutics.Product.Tests;

public sealed class FrontendPromptMarkersTests
{
    private const string Reference = "[review](mcp-prompt://plugin:demo::srv/code_review)";

    [Fact]
    public void ParsesWellFormedReferencesWithAndWithoutArguments()
    {
        var bare = FrontendPromptMarkers.Split(
            $"lead {Reference} tail");
        bare.Markers.Should().ContainSingle().Which.Name.Should().Be("code_review");
        bare.Markers[0].ServerId.Should().Be("plugin:demo::srv");
        bare.Markers[0].Text.Should().Be("review");
        bare.Markers[0].Arguments.Should().BeEmpty();
        bare.Markers[0].DuplicateKeys.Should().BeFalse();

        var withArgs = FrontendPromptMarkers.Split(
            "[评审](mcp-prompt://plugin:demo::srv/code_review?language=rust&depth=deep)");
        withArgs.Markers.Should().ContainSingle().Which.Arguments.Should().BeEquivalentTo(
            new Dictionary<string, string> { ["language"] = "rust", ["depth"] = "deep" });
    }

    [Fact]
    public void DecodesPercentEncodedArguments()
    {
        var split = FrontendPromptMarkers.Split(
            "[x](mcp-prompt://s/prompt?q=a%20b%26c%3Dd)");

        split.Markers.Should().ContainSingle()
            .Which.Arguments["q"].Should().Be("a b&c=d");
    }

    [Fact]
    public void NearMissesStayLiteralText()
    {
        const string text = """
            path in name: [x](mcp-prompt://s/a/b)
            raw space in query: [x](mcp-prompt://s/p?a b=c)
            raw paren in value: [x](mcp-prompt://s/p?q=f(x))
            missing name: [x](mcp-prompt://s/)
            other scheme: [x](skill://s) and [x](mcp://s/uri)
            title: [x](mcp-prompt://s/p "t")
            """;

        FrontendPromptMarkers.Split(text).Markers.Should().BeEmpty();
    }

    [Fact]
    public void BackslashSuppressesRecognitionAndIsConsumed()
    {
        var split = FrontendPromptMarkers.Split(
            $"mention: \\{Reference} and real: {Reference}");

        split.Markers.Should().ContainSingle();
        split.Remainder.Should().Contain(Reference)
            .And.NotContain($"\\{Reference}");
    }

    [Fact]
    public void DuplicateQueryKeysFlagTheMarker()
    {
        var split = FrontendPromptMarkers.Split(
            "[x](mcp-prompt://s/p?a=1&a=2)");

        split.Markers.Should().ContainSingle().Which.DuplicateKeys.Should().BeTrue();
    }

    [Fact]
    public void PlainTextWithoutReferencesPassesThroughUnchanged()
    {
        const string text = "$100 and [x](https://example.com) and mcp-prompt://bare";
        var split = FrontendPromptMarkers.Split(text);
        split.Markers.Should().BeEmpty();
        split.Remainder.Should().Be(text);
    }
}

public sealed class McpPromptReferenceGrammarTests
{
    [Theory]
    [InlineData("code_review", true)]
    [InlineData("Code-Review", true)]
    [InlineData("a", true)]
    [InlineData("-lead", false)]
    [InlineData("code review", false)]
    [InlineData("p?q", false)]
    [InlineData("", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void PromptNameReferencability(string name, bool expected)
    {
        McpPromptReferenceGrammar.IsValidPromptName(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("plugin:demo::srv", true)]
    [InlineData("plain", true)]
    [InlineData("a/b", false)]
    [InlineData("a?b", false)]
    [InlineData("a b", false)]
    [InlineData("a(b)", false)]
    [InlineData("", false)]
    public void ServerIdSegmentReferencability(string serverId, bool expected)
    {
        McpPromptReferenceGrammar.IsValidServerIdSegment(serverId).Should().Be(expected);
    }
}
