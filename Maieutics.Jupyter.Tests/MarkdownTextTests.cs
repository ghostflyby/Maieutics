using FluentAssertions;
using Maieutics.Commands;

namespace Maieutics.Jupyter.Tests;

/// <summary>
///     Pins the markdown escaping primitives behind <c>%status</c> and command
///     answers: code spans must stay verbatim inside the span (delimiter length
///     beats the longest embedded backtick run, edge padding keeps the span
///     parseable), and plain text escapes ASCII punctuation and symbols while
///     normalizing control characters.
/// </summary>
public sealed class MarkdownTextTests
{
    [Theory]
    [InlineData("model", "`model`")]
    [InlineData("", "` `")]
    [InlineData("a`b", "``a`b``")]
    [InlineData("a``b", "```a``b```")]
    [InlineData("`x`", "`` `x` ``")]
    // Edge padding adds one space at each edge; a trailing/leading content
    // space therefore shows up doubled next to the delimiter.
    [InlineData("x ", "` x  `")]
    [InlineData(" x", "`  x `")]
    [InlineData("a\nb", "`a\\nb`")]
    [InlineData("a\tb", "`a\\tb`")]
    [InlineData("a\rb", "`a\\rb`")]
    [InlineData("a\u0001b", "`a\\u0001b`")]
    [InlineData("a\u0007b", "`a\\u0007b`")]
    public void CodeSpanKeepsContentVerbatimInsideAParseableSpan(string value, string expected)
    {
        MarkdownText.CodeSpan(value).Should().Be(expected);
    }

    [Fact]
    public void CodeSpanDelimiterGrowsWithTheLongestEmbeddedRun()
    {
        // Three embedded backticks demand a four-backtick delimiter; the span
        // stays unpadded because neither edge is a backtick or a space.
        MarkdownText.CodeSpan("a```b").Should().Be("````a```b````");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("hello world", "hello world")]
    [InlineData("a.b", "a\\.b")]
    [InlineData("-*_", "\\-\\*\\_")]
    [InlineData("100%", "100\\%")]
    [InlineData("model=1", "model\\=1")]
    [InlineData("a`b", "a\\`b")]
    [InlineData("a+b|c~d", "a\\+b\\|c\\~d")]
    // Normalization runs first, so the backslashes it introduces are escaped
    // like any other ASCII punctuation: a literal newline renders as \\n.
    [InlineData("a\nb", "a\\\\nb")]
    [InlineData("a\u0001b", "a\\\\u0001b")]
    [InlineData("\u007f", "\\\\u007F")]
    public void PlainTextEscapesAsciiPunctuationAndSymbolsAndNormalizesControlCharacters(
        string value,
        string expected)
    {
        MarkdownText.PlainText(value).Should().Be(expected);
    }

    [Fact]
    public void PlainTextLeavesLettersDigitsSpacesAndNonAsciiPunctuationAlone()
    {
        MarkdownText.PlainText("v10 ready").Should().Be("v10 ready");
        // Fullwidth punctuation is outside the ASCII guard on purpose: the
        // escape targets markdown structure characters, which are ASCII.
        MarkdownText.PlainText("a，b").Should().Be("a，b");
    }
}
