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
        Assert.Equal("code_review", Assert.Single(bare.Markers).Name);
        Assert.Equal("plugin:demo::srv", bare.Markers[0].ServerId);
        Assert.Equal("review", bare.Markers[0].Text);
        Assert.Empty(bare.Markers[0].Arguments);
        Assert.False(bare.Markers[0].DuplicateKeys);

        var withArgs = FrontendPromptMarkers.Split(
            "[评审](mcp-prompt://plugin:demo::srv/code_review?language=rust&depth=deep)");
        Assert.Equivalent(new Dictionary<string, string> { ["language"] = "rust", ["depth"] = "deep" }, Assert.Single(withArgs.Markers).Arguments);
    }

    [Fact]
    public void DecodesPercentEncodedArguments()
    {
        var split = FrontendPromptMarkers.Split(
            "[x](mcp-prompt://s/prompt?q=a%20b%26c%3Dd)");

        Assert.Equal("a b&c=d", Assert.Single(split.Markers).Arguments["q"]);
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

        Assert.Empty(FrontendPromptMarkers.Split(text).Markers);
    }

    [Fact]
    public void BackslashSuppressesRecognitionAndIsConsumed()
    {
        var split = FrontendPromptMarkers.Split(
            $"mention: \\{Reference} and real: {Reference}");

        Assert.Single(split.Markers);
        Assert.Contains(Reference, split.Remainder);
        Assert.DoesNotContain($"\\{Reference}", split.Remainder);
    }

    [Fact]
    public void DuplicateQueryKeysFlagTheMarker()
    {
        var split = FrontendPromptMarkers.Split(
            "[x](mcp-prompt://s/p?a=1&a=2)");

        Assert.True(Assert.Single(split.Markers).DuplicateKeys);
    }

    [Fact]
    public void PlainTextWithoutReferencesPassesThroughUnchanged()
    {
        const string text = "$100 and [x](https://example.com) and mcp-prompt://bare";
        var split = FrontendPromptMarkers.Split(text);
        Assert.Empty(split.Markers);
        Assert.Equal(text, split.Remainder);
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
        Assert.Equal(expected, McpPromptReferenceGrammar.IsValidPromptName(name));
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
        Assert.Equal(expected, McpPromptReferenceGrammar.IsValidServerIdSegment(serverId));
    }
}
