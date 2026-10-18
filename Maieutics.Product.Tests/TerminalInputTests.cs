using Maieutics.Agent;
using Maieutics.Execution;
using XTerm;

namespace Maieutics.Product.Tests;

public sealed class TerminalInputParserTests
{
    [Fact]
    public void TextLinesParseAsRawPayloads()
    {
        var batch = TerminalInputBatchParser.Parse("t hello\n" + "t <script>alert('x')</script>\n" + "t tab\there");

        Assert.Equal(3, batch.LineCount);
        var text = batch.Operations.Select(static operation => Assert.IsType<TerminalTextOperation>(operation))
            .ToArray();
        Assert.Equal(new string[] { "hello", "<script>alert('x')</script>", "tab\there" }, text.Select(static operation => operation.Text));
        Assert.Equal(new int[] { 1, 2, 3 }, text.Select(static operation => operation.Line));
    }

    [Fact]
    public void KeyLinesParseIntoTokensWithCounts()
    {
        var batch = TerminalInputBatchParser.Parse("k <Esc> <CR>\n" + "k 5<Down>\n" + "k <C-w> <C-j>");

        Assert.Equal(3, batch.Operations.Count());
        var first = Assert.IsType<TerminalKeysOperation>(batch.Operations[0]);
        Assert.Equal(new TerminalKey[] { new TerminalKey(null, new TerminalSpecialKey(TerminalKeyName.Escape, TerminalKeyModifiers.None)), new TerminalKey(null, new TerminalSpecialKey(TerminalKeyName.Enter, TerminalKeyModifiers.None)) }, first.Keys);

        var second = Assert.IsType<TerminalKeysOperation>(batch.Operations[1]);
        Assert.Equal(new TerminalKey(
            5,
            new TerminalSpecialKey(TerminalKeyName.Down, TerminalKeyModifiers.None)), Assert.Single(second.Keys));

        var third = Assert.IsType<TerminalKeysOperation>(batch.Operations[2]);
        Assert.Equal(new TerminalKeySpec[] { new TerminalCharKey('w', TerminalKeyModifiers.Control), new TerminalCharKey('j', TerminalKeyModifiers.Control) }, third.Keys.Select(static key => key.Spec));
    }

    [Theory]
    [InlineData("t a\u001bb")]
    [InlineData("t a\u0001b")]
    [InlineData("t a\u007fb")]
    public void TextPayloadsRejectEscapeAndControlBytes(string line)
    {
        var failure = () => TerminalInputBatchParser.Parse(line);
        Assert.Equal("terminal_invalid_input", Assert.ThrowsAny<AgentToolException>(() => failure.Invoke()).Code);
    }

    [Theory]
    [InlineData("unknown line")]
    [InlineData("k")]
    [InlineData("k <")]
    [InlineData("k <Unicorn>")]
    [InlineData("k <C-!>")]
    [InlineData("k <S-a>")]
    [InlineData("k <C-A-a>")]
    [InlineData("k 01<CR>")]
    [InlineData("k 0<CR>")]
    [InlineData("k 10001<CR>")]
    [InlineData("k 99999999999999999999<CR>")]
    [InlineData("k bare")]
    public void MalformedKeyLinesFailTheWholeBatch(string line)
    {
        var failure = () => TerminalInputBatchParser.Parse(line);
        Assert.Equal("terminal_invalid_input", Assert.ThrowsAny<AgentToolException>(() => failure.Invoke()).Code);
    }

    [Fact]
    public void TrailingNewlineDoesNotCreateAnErrorLine()
    {
        var batch = TerminalInputBatchParser.Parse("t hello\n");
        Assert.IsType<TerminalTextOperation>(Assert.Single(batch.Operations));
    }

    [Theory]
    [InlineData("<C-@>", '@')]
    [InlineData("<C-[>", '[')]
    [InlineData("<C-\\>", '\\')]
    [InlineData("<C-]>", ']')]
    [InlineData("<C-^>", '^')]
    [InlineData("<C-_>", '_')]
    [InlineData("<M-x>", 'x')]
    [InlineData("<A-y>", 'y')]
    public void ExtendedControlAndMetaTokensParse(string token, char character)
    {
        var batch = TerminalInputBatchParser.Parse($"k {token}");
        var keys = Assert.IsType<TerminalKeysOperation>(batch.Operations.Single());
        var modifiers = token.StartsWith("<C-", StringComparison.Ordinal)
            ? TerminalKeyModifiers.Control
            : TerminalKeyModifiers.Alt;
        Assert.Equal(new TerminalCharKey(character, modifiers), Assert.Single(keys.Keys).Spec);
    }

    [Fact]
    public void SpecialKeysAcceptModifiers()
    {
        var batch = TerminalInputBatchParser.Parse("k <S-Tab> <C-F1> <A-Up>");
        var keys = Assert.IsType<TerminalKeysOperation>(batch.Operations.Single());
        Assert.Equal(new TerminalKey[] { new TerminalKey(null, new TerminalSpecialKey(TerminalKeyName.Tab, TerminalKeyModifiers.Shift)), new TerminalKey(null, new TerminalSpecialKey(TerminalKeyName.F1, TerminalKeyModifiers.Control)), new TerminalKey(null, new TerminalSpecialKey(TerminalKeyName.Up, TerminalKeyModifiers.Alt)) }, keys.Keys);
    }

    [Theory]
    [InlineData("<Ins>")]
    [InlineData("<Insert>")]
    public void InsertNamesParseToTheInsertSpecialKey(string token)
    {
        var batch = TerminalInputBatchParser.Parse($"k {token}");
        var keys = Assert.IsType<TerminalKeysOperation>(batch.Operations.Single());
        Assert.Equal(new TerminalSpecialKey(TerminalKeyName.Insert, TerminalKeyModifiers.None), Assert.Single(keys.Keys).Spec);
    }

    [Fact]
    public void CtrlSpaceParsesAsTheNulControlByte()
    {
        var batch = TerminalInputBatchParser.Parse("k <C-Space>");
        var keys = Assert.IsType<TerminalKeysOperation>(batch.Operations.Single());
        Assert.Equal(new TerminalCharKey('@', TerminalKeyModifiers.Control), Assert.Single(keys.Keys).Spec);
    }

    [Fact]
    public void ControlCharactersCannotHideInsideTextPayloads()
    {
        // A newline ends the line and is parsed as the next command; an embedded control byte is rejected.
        var batch = TerminalInputBatchParser.Parse("t first\nk <CR>\nt second");
        Assert.Equal(3, batch.Operations.Count());
    }
}

public sealed class TerminalKeyEncoderTests
{
    private static readonly byte[] Esc = [0x1b];

    private static TerminalKeyEncoder CreateEncoder(out Terminal terminal)
    {
        terminal = new Terminal(new XTerm.Options.TerminalOptions { Cols = 80, Rows = 24 });
        return new TerminalKeyEncoder(terminal);
    }

    [Fact]
    public void ControlLetterMapsToItsC0Byte()
    {
        var encoder = CreateEncoder(out _);
        var bytes = encoder.Encode([Key('c', TerminalKeyModifiers.Control)]);

        Assert.Equal(new byte[] { (byte)(0x03) }, bytes);
    }

    [Theory]
    [InlineData("@", 0x00)]
    [InlineData("[", 0x1b)]
    [InlineData("\\", 0x1c)]
    [InlineData("]", 0x1d)]
    [InlineData("^", 0x1e)]
    [InlineData("_", 0x1f)]
    public void ExtendedControlCharactersMapToC0Bytes(string token, byte expected)
    {
        var encoder = CreateEncoder(out _);
        var character = token[0];
        var bytes = encoder.Encode([Key(character, TerminalKeyModifiers.Control)]);

        Assert.Equal(new byte[] { expected }, bytes);
    }

    [Fact]
    public void MetaKeyIsEscPrefixed()
    {
        var encoder = CreateEncoder(out _);
        var bytes = encoder.Encode([Key('x', TerminalKeyModifiers.Alt)]);

        Assert.Equal(Esc.Concat("x"u8.ToArray()), bytes);
    }

    [Fact]
    public void EnterTabAndSpaceEncodeAsRawControlBytes()
    {
        var encoder = CreateEncoder(out _);
        Assert.Equal(new byte[] { (byte)(0x0d) }, encoder.Encode([Special(TerminalKeyName.Enter)]));
        Assert.Equal(new byte[] { (byte)(0x09) }, encoder.Encode([Special(TerminalKeyName.Tab)]));
        Assert.Equal(new byte[] { (byte)(0x20) }, encoder.Encode([Special(TerminalKeyName.Space)]));
    }

    [Fact]
    public void InsertEncodesAsTheStandardEscapeSequence()
    {
        var encoder = CreateEncoder(out _);
        Assert.Equal(Esc.Concat("[2~"u8.ToArray()), encoder.Encode([Special(TerminalKeyName.Insert)]));
    }

    [Fact]
    public void EscapeEncodesAsItsOwnByte()
    {
        var encoder = CreateEncoder(out _);
        Assert.Equal(new byte[] { (byte)(0x1b) }, encoder.Encode([Special(TerminalKeyName.Escape)]));
    }

    [Fact]
    public void ArrowKeysFollowApplicationCursorMode()
    {
        var encoder = CreateEncoder(out var terminal);
        Assert.Equal(Esc.Concat("[A"u8.ToArray()), encoder.Encode([Special(TerminalKeyName.Up)]));

        terminal.ApplicationCursorKeys = true;
        Assert.Equal(Esc.Concat("OA"u8.ToArray()), encoder.Encode([Special(TerminalKeyName.Up)]));
    }

    [Fact]
    public void CountRepeatsTheKeySequence()
    {
        var encoder = CreateEncoder(out _);
        var bytes = encoder.Encode([new TerminalKey(5, new TerminalSpecialKey(TerminalKeyName.Down, TerminalKeyModifiers.None))]);

        Assert.Equal(5 * 3, bytes.Count());
        Assert.Equal(Esc.Concat("[B"u8.ToArray())
            .Concat(Esc.Concat("[B"u8.ToArray()))
            .Concat(Esc.Concat("[B"u8.ToArray()))
            .Concat(Esc.Concat("[B"u8.ToArray()))
            .Concat(Esc.Concat("[B"u8.ToArray())), bytes);
    }

    [Fact]
    public void CtrlArrowEmitsTheExtendedCsiSequence()
    {
        var encoder = CreateEncoder(out _);
        var bytes = encoder.Encode([new TerminalKey(
            null,
            new TerminalSpecialKey(TerminalKeyName.Up, TerminalKeyModifiers.Control))]);

        Assert.Equal(Esc.Concat("[1;5A"u8.ToArray()), bytes);
    }

    [Fact]
    public void KeySequenceBytesAreWrittenAdjacent()
    {
        var encoder = CreateEncoder(out _);
        var bytes = encoder.Encode(
        [
            Special(TerminalKeyName.Escape),
            Key('x', TerminalKeyModifiers.Alt),
            Special(TerminalKeyName.Enter)
        ]);

        // The ESC of the meta 'x' must not be split from its payload by the surrounding keys.
        Assert.Equal(new byte[] { (byte)(0x1b), (byte)(0x1b), (byte)'x', (byte)(0x0d) }, bytes);
    }

    private static TerminalKey Special(TerminalKeyName name)
    {
        return new TerminalKey(null, new TerminalSpecialKey(name, TerminalKeyModifiers.None));
    }

    private static TerminalKey Key(char character, TerminalKeyModifiers modifiers)
    {
        return new TerminalKey(null, new TerminalCharKey(character, modifiers));
    }
}
