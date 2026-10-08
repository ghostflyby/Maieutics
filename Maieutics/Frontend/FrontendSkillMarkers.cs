using System.Text.RegularExpressions;

namespace Maieutics.Frontend;

/// <summary>
///     The server-side half of the skill-reference marker grammar
///     (<c>docs/web-frontend-protocol.md</c>, "Skill references"). A frontend turns a user's
///     skill selection into a strict canonical marker inside the turn text; the turn build
///     path splits the submitted text into the remaining text plus the ordered references,
///     whose bodies the kernel expands at build time. Only the exact canonical form is a
///     marker — an uppercase letter, stray whitespace, an invalid name character, or a
///     missing field stays ordinary text, so a hand-written look-alike degrades instead of
///     injecting a skill body. A backslash directly before the opening brackets suppresses
///     recognition and is consumed: that is the mention form, how a frontend encodes
///     marker-shaped text that is NOT a user selection.
/// </summary>
internal static class FrontendSkillMarkers
{
    /// <summary>One parsed marker: the skill name exactly as written (the grammar admits
    /// only the catalog name charset, so there is nothing to unescape).</summary>
    internal sealed record Marker(string Name);

    /// <summary>The text with every well-formed marker removed (suppressed mentions keep
    /// their marker text minus the escape), and the markers in order of appearance.</summary>
    internal sealed record SplitResult(string Remainder, IReadOnlyList<Marker> Markers);

    // Both sides (kernel and the extension's skillReferences module) must agree exactly.
    // The name is the skill:// host charset, so a marker is at the same time a valid
    // read pointer; control characters and quotes cannot appear inside the form.
    private static readonly Regex MarkerPattern = new(
        """(?<!\\)\[\[maieutics:skill name="(?<name>[a-z0-9][a-z0-9-]{0,63})"\]\]""",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private const string EscapePrefix = @"\[[maieutics:skill";

    /// <summary>Splits submitted turn text into its remaining text and ordered markers.
    /// Backslash-suppressed occurrences stay in the remainder with the escape consumed;
    /// duplicate names keep every occurrence in the remainder-free ordering but are
    /// expanded once by the consumer (bodies are content-identical per name).</summary>
    internal static SplitResult Split(string text)
    {
        var matches = MarkerPattern.Matches(text);
        if (matches.Count == 0) return new SplitResult(Unescape(text), []);

        var markers = new List<Marker>(matches.Count);
        var remainder = new System.Text.StringBuilder(text.Length);
        var position = 0;
        foreach (Match match in matches)
        {
            remainder.Append(text, position, match.Index - position);
            position = match.Index + match.Length;
            markers.Add(new Marker(match.Groups["name"].Value));
        }

        remainder.Append(text, position, text.Length - position);
        return new SplitResult(Unescape(remainder.ToString()), markers);
    }

    /// <summary>Consumes the mention escape everywhere: a backslash directly before the
    /// opening brackets marked the occurrence as literal text, and the escape has done its
    /// job once recognition is settled. Runs only over the non-marker remainder.</summary>
    private static string Unescape(string text)
    {
        return text.Contains(EscapePrefix, StringComparison.Ordinal)
            ? text.Replace(EscapePrefix, "[[maieutics:skill", StringComparison.Ordinal)
            : text;
    }
}
