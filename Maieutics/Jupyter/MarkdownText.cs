using System.Globalization;
using System.Text;

namespace Maieutics.Jupyter;

internal static class MarkdownText
{
    internal static string CodeSpan(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = NormalizeInline(value);
        if (normalized.Length == 0) return "` `";

        var longestRun = 0;
        var currentRun = 0;
        foreach (var character in normalized)
            if (character == '`')
                longestRun = Math.Max(longestRun, ++currentRun);
            else
                currentRun = 0;

        var delimiter = new string('`', longestRun + 1);
        var needsPadding = normalized[0] is '`' or ' ' || normalized[^1] is '`' or ' ';
        var padding = needsPadding ? " " : string.Empty;
        return delimiter + padding + normalized + padding + delimiter;
    }

    internal static string PlainText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = NormalizeInline(value);
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character <= 0x7f && (char.IsPunctuation(character) || char.IsSymbol(character)))
                result.Append('\\');

            result.Append(character);
        }

        return result.ToString();
    }

    private static string NormalizeInline(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            switch (character)
            {
                case '\r':
                    result.Append("\\r");
                    break;
                case '\n':
                    result.Append("\\n");
                    break;
                case '\t':
                    result.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                        result.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        result.Append(character);

                    break;
            }

        return result.ToString();
    }
}
