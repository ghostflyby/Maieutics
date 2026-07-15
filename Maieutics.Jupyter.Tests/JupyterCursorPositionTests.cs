using FluentAssertions;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class JupyterCursorPositionTests
{
    [Fact]
    public void ConvertsBetweenUtf16AndCodePointOffsets()
    {
        const string text = "a😀b";

        JupyterCursorPosition.FromUtf16Index(text, 3).Should().Be(2);
        JupyterCursorPosition.ToUtf16Index(text, 2).Should().Be(3);
        JupyterCursorPosition.FromUtf16Index(text, text.Length).Should().Be(3);
        JupyterCursorPosition.ToUtf16Index(text, 3).Should().Be(text.Length);
    }

    [Fact]
    public void RejectsUtf16IndexInsideSurrogatePair()
    {
        var act = () => JupyterCursorPosition.FromUtf16Index("a😀b", 2);

        act.Should().Throw<ArgumentException>().WithMessage("*surrogate pair*");
    }

    [Fact]
    public void ConvertsZeroBasedLineAndUtf16Column()
    {
        const string text = "a😀\nxy";

        JupyterCursorPosition.FromLineColumn(text, new JupyterTextPosition(1, 1)).Should().Be(4);
        JupyterCursorPosition.ToLineColumn(text, 4).Should().Be(new JupyterTextPosition(1, 1));
    }

    [Fact]
    public void RejectsOutOfRangePositions()
    {
        var utf16 = () => JupyterCursorPosition.ToUtf16Index("abc", 4);
        var line = () => JupyterCursorPosition.FromLineColumn("abc", new JupyterTextPosition(1, 0));

        utf16.Should().Throw<ArgumentOutOfRangeException>();
        line.Should().Throw<ArgumentOutOfRangeException>();
    }
}