using System.Text.RegularExpressions;

namespace Maieutics.Frontend;

/// <summary>
///     The server-side half of the skill-reference grammar
///     (<c>docs/web-frontend-protocol.md</c>, "Skill references"): a **standard markdown
///     link over the custom <c>skill://</c> protocol** — <c>[text](skill://name)</c>. The
///     reference identity is the URL host (the catalog name); the link text is display
///     metadata the frontend chooses. The turn build path splits the submitted text into
///     the remaining text plus the ordered references, whose bodies the kernel expands at
///     build time. Only the exact form is a reference — an invalid name character, a path
///     or query on the URL, a title, or stray whitespace stays ordinary text, so a
///     hand-written look-alike degrades instead of injecting a skill body. A backslash
///     directly before the opening bracket suppresses recognition and is consumed: that is
///     the mention form, how a frontend encodes link-shaped text that is NOT a user
///     selection.
/// </summary>
internal static class FrontendSkillMarkers
{
    /// <summary>One parsed reference: the skill name from the URL host and the link text
    /// exactly as written (the text admits everything but `]` and newlines).</summary>
    internal sealed record Marker(string Name, string Text);

    /// <summary>The text with every well-formed reference removed (suppressed mentions
    /// keep their link text minus the escape), and the references in order of
    /// appearance.</summary>
    internal sealed record SplitResult(string Remainder, IReadOnlyList<Marker> Markers);

    private const string HostCharset = @"[a-z0-9][a-z0-9-]{0,63}";

    // Both sides (kernel and the extension's skillReferences module) must agree exactly.
    private static readonly Regex MarkerPattern = new(
        $"""(?<!\\)\[(?<text>[^\]\n]*)\]\((?<url>skill://{HostCharset})\)""",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex MentionEscapePattern = new(
        $"""\\(\[[^\]\n]*\]\(skill://{HostCharset}\))""",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Splits submitted turn text into its remaining text and ordered references.
    /// Backslash-suppressed occurrences stay in the remainder with the escape consumed;
    /// duplicate names keep every occurrence in the ordering but are expanded once by the
    /// consumer (bodies are content-identical per name).</summary>
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
            markers.Add(new Marker(
                match.Groups["url"].Value["skill://".Length..],
                match.Groups["text"].Value));
        }

        remainder.Append(text, position, text.Length - position);
        return new SplitResult(Unescape(remainder.ToString()), markers);
    }

    /// <summary>Consumes the mention escape everywhere a full link-shaped occurrence
    /// carries it: the backslash marked the occurrence as literal text, and the escape has
    /// done its job once recognition is settled. Runs only over the non-reference
    /// remainder, so it never touches a real reference.</summary>
    private static string Unescape(string text)
    {
        return MentionEscapePattern.IsMatch(text)
            ? MentionEscapePattern.Replace(text, "$1")
            : text;
    }
}
