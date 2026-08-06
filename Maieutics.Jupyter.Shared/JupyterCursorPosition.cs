namespace Maieutics.Jupyter.Shared;

public readonly record struct JupyterTextPosition(int Line, int Utf16Column);

public static class JupyterCursorPosition
{
    public static int FromUtf16Index(string text, int utf16Index)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(utf16Index);
        if (utf16Index > text.Length) throw new ArgumentOutOfRangeException(nameof(utf16Index));

        if (utf16Index > 0 && utf16Index < text.Length &&
            char.IsHighSurrogate(text[utf16Index - 1]) && char.IsLowSurrogate(text[utf16Index]))
            throw new ArgumentException("The UTF-16 index splits a surrogate pair.", nameof(utf16Index));

        var codePointOffset = 0;
        foreach (var _ in text.AsSpan(0, utf16Index).EnumerateRunes()) codePointOffset++;

        return codePointOffset;
    }

    public static int ToUtf16Index(string text, int codePointOffset)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(codePointOffset);

        var currentOffset = 0;
        var utf16Index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (currentOffset == codePointOffset) return utf16Index;

            currentOffset++;
            utf16Index += rune.Utf16SequenceLength;
        }

        if (currentOffset == codePointOffset) return utf16Index;

        throw new ArgumentOutOfRangeException(nameof(codePointOffset));
    }

    public static int FromLineColumn(string text, JupyterTextPosition position)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(position.Line);
        ArgumentOutOfRangeException.ThrowIfNegative(position.Utf16Column);

        var line = 0;
        var lineStart = 0;
        while (line < position.Line)
        {
            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0) throw new ArgumentOutOfRangeException(nameof(position));

            line++;
            lineStart = newline + 1;
        }

        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = text.Length;

        if (position.Utf16Column > lineEnd - lineStart) throw new ArgumentOutOfRangeException(nameof(position));

        return FromUtf16Index(text, lineStart + position.Utf16Column);
    }

    public static JupyterTextPosition ToLineColumn(string text, int codePointOffset)
    {
        var utf16Index = ToUtf16Index(text, codePointOffset);
        var line = 0;
        var lineStart = 0;
        for (var index = 0; index < utf16Index; index++)
            if (text[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }

        return new JupyterTextPosition(line, utf16Index - lineStart);
    }
}