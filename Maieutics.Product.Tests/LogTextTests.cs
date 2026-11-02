namespace Maieutics.Product.Tests;

public sealed class LogTextTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("GET /v1/objects/中文", "GET /v1/objects/中文")]
    [InlineData("a\r\nb\tc", "a\\r\\nb\\tc")]
    [InlineData("a\u001b[2J\0\u007fb", "a\\u001b[2J\\u0000\\u007fb")]
    [InlineData("a\u0085b\u2028c\u2029d", "a\\u0085b\\u2028c\\u2029d")]
    [InlineData("literal\\n and actual\n", "literal\\\\n and actual\\n")]
    public void ExternalFieldsKeepPrintableTextAndExposeControlCharacters(string input, string expected)
    {
        Assert.Equal(expected, LogText.Escape(input));
    }
}
