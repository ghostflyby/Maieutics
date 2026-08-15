using System.Text;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using XTerm;
using XTerm.Input;
using XTerm.Options;

namespace Maieutics.Jupyter.Tests;

public sealed class TerminalInputParserTests
{
    [Fact]
    public void TextLinesParseAsRawPayloads()
    {
        var batch = TerminalInputBatchParser.Parse("t hello\n" + "t <script>alert('x')</script>\n" + "t tab\there");

        batch.LineCount.Should().Be(3);
        var text = batch.Operations.Select(static operation => operation.Should().BeOfType<TerminalTextOperation>().Which)
            .ToArray();
        text.Select(static operation => operation.Text).Should().Equal(
            "hello",
            "<script>alert('x')</script>",
            "tab\there");
        text.Select(static operation => operation.Line).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void KeyLinesParseIntoTokensWithCounts()
    {
        var batch = TerminalInputBatchParser.Parse("k <Esc> <CR>\n" + "k 5<Down>\n" + "k <C-w> <C-j>");

        batch.Operations.Should().HaveCount(3);
        var first = batch.Operations[0].Should().BeOfType<TerminalKeysOperation>().Which;
        first.Keys.Should().Equal(
            new TerminalKey(null, new TerminalSpecialKey(TerminalKeyName.Escape, TerminalKeyModifiers.None)),
            new TerminalKey(null, new TerminalSpecialKey(TerminalKeyName.Enter, TerminalKeyModifiers.None)));

        var second = batch.Operations[1].Should().BeOfType<TerminalKeysOperation>().Which;
        second.Keys.Should().ContainSingle().Which.Should().Be(new TerminalKey(
            5,
            new TerminalSpecialKey(TerminalKeyName.Down, TerminalKeyModifiers.None)));

        var third = batch.Operations[2].Should().BeOfType<TerminalKeysOperation>().Which;
        third.Keys.Select(static key => key.Spec).Should().Equal(
            new TerminalCharKey('w', TerminalKeyModifiers.Control),
            new TerminalCharKey('j', TerminalKeyModifiers.Control));
    }

    [Theory]
    [InlineData("t a\u001bb")]
    [InlineData("t a\u0001b")]
    [InlineData("t a\u007fb")]
    public void TextPayloadsRejectEscapeAndControlBytes(string line)
    {
        var failure = () => TerminalInputBatchParser.Parse(line);
        failure.Should().Throw<AgentToolException>()
            .Which.Code.Should().Be("terminal_invalid_input");
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
        failure.Should().Throw<AgentToolException>()
            .Which.Code.Should().Be("terminal_invalid_input");
    }

    [Fact]
    public void TrailingNewlineDoesNotCreateAnErrorLine()
    {
        var batch = TerminalInputBatchParser.Parse("t hello\n");
        batch.Operations.Should().ContainSingle().Which.Should().BeOfType<TerminalTextOperation>();
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
        var keys = batch.Operations.Single().Should().BeOfType<TerminalKeysOperation>().Which;
        var modifiers = token.StartsWith("<C-", StringComparison.Ordinal)
            ? TerminalKeyModifiers.Control
            : TerminalKeyModifiers.Alt;
        keys.Keys.Should().ContainSingle().Which.Spec.Should().Be(new TerminalCharKey(character, modifiers));
    }

    [Fact]
    public void SpecialKeysAcceptModifiers()
    {
        var batch = TerminalInputBatchParser.Parse("k <S-Tab> <C-F1> <A-Up>");
        var keys = batch.Operations.Single().Should().BeOfType<TerminalKeysOperation>().Which;
        keys.Keys.Should().Equal(
            new TerminalKey(null, new TerminalSpecialKey(TerminalKeyName.Tab, TerminalKeyModifiers.Shift)),
            new TerminalKey(null, new TerminalSpecialKey(TerminalKeyName.F1, TerminalKeyModifiers.Control)),
            new TerminalKey(null, new TerminalSpecialKey(TerminalKeyName.Up, TerminalKeyModifiers.Alt)));
    }

    [Fact]
    public void ControlCharactersCannotHideInsideTextPayloads()
    {
        // A newline ends the line and is parsed as the next command; an embedded control byte is rejected.
        var batch = TerminalInputBatchParser.Parse("t first\nk <CR>\nt second");
        batch.Operations.Should().HaveCount(3);
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

        bytes.Should().Equal(0x03);
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

        bytes.Should().Equal(expected);
    }

    [Fact]
    public void MetaKeyIsEscPrefixed()
    {
        var encoder = CreateEncoder(out _);
        var bytes = encoder.Encode([Key('x', TerminalKeyModifiers.Alt)]);

        bytes.Should().Equal(Esc.Concat("x"u8.ToArray()));
    }

    [Fact]
    public void EnterTabAndSpaceEncodeAsRawControlBytes()
    {
        var encoder = CreateEncoder(out _);
        encoder.Encode([Special(TerminalKeyName.Enter)]).Should().Equal(0x0d);
        encoder.Encode([Special(TerminalKeyName.Tab)]).Should().Equal(0x09);
        encoder.Encode([Special(TerminalKeyName.Space)]).Should().Equal(0x20);
    }

    [Fact]
    public void EscapeEncodesAsItsOwnByte()
    {
        var encoder = CreateEncoder(out _);
        encoder.Encode([Special(TerminalKeyName.Escape)]).Should().Equal(0x1b);
    }

    [Fact]
    public void ArrowKeysFollowApplicationCursorMode()
    {
        var encoder = CreateEncoder(out var terminal);
        encoder.Encode([Special(TerminalKeyName.Up)]).Should().Equal(Esc.Concat("[A"u8.ToArray()));

        terminal.ApplicationCursorKeys = true;
        encoder.Encode([Special(TerminalKeyName.Up)]).Should().Equal(Esc.Concat("OA"u8.ToArray()));
    }

    [Fact]
    public void CountRepeatsTheKeySequence()
    {
        var encoder = CreateEncoder(out _);
        var bytes = encoder.Encode([new TerminalKey(5, new TerminalSpecialKey(TerminalKeyName.Down, TerminalKeyModifiers.None))]);

        bytes.Should().HaveCount(5 * 3);
        bytes.Should().Equal(Esc.Concat("[B"u8.ToArray())
            .Concat(Esc.Concat("[B"u8.ToArray()))
            .Concat(Esc.Concat("[B"u8.ToArray()))
            .Concat(Esc.Concat("[B"u8.ToArray()))
            .Concat(Esc.Concat("[B"u8.ToArray())));
    }

    [Fact]
    public void CtrlArrowEmitsTheExtendedCsiSequence()
    {
        var encoder = CreateEncoder(out _);
        var bytes = encoder.Encode([new TerminalKey(
            null,
            new TerminalSpecialKey(TerminalKeyName.Up, TerminalKeyModifiers.Control))]);

        bytes.Should().Equal(Esc.Concat("[1;5A"u8.ToArray()));
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
        bytes.Should().Equal(0x1b, 0x1b, (byte)'x', 0x0d);
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
