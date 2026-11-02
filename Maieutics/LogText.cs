using System.Text.RegularExpressions;

namespace Maieutics;

/// <summary>Renders external text as one diagnostic field without interpreting control characters.</summary>
internal static partial class LogText
{
    /// <summary>Preserves printable text and makes control characters and line separators visible.</summary>
    internal static string Escape(string value)
    {
        // Escape backslashes first so a literal "\n" stays distinguishable from a newline.
        return ControlCharacters().Replace(
            value.Replace("\\", "\\\\", StringComparison.Ordinal),
            static match => match.Value[0] switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                var character => $"\\u{(int)character:x4}"
            });
    }

    [GeneratedRegex(@"[\p{Cc}\p{Zl}\p{Zp}]")]
    private static partial Regex ControlCharacters();
}
