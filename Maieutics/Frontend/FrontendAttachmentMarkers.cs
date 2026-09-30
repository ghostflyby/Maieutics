using System.Text.RegularExpressions;

namespace Maieutics.Frontend;

/// <summary>
///     The server-side half of the attachment marker grammar
///     (<c>docs/web-frontend-protocol.md</c>, "Attachments"). A frontend ingests bytes and
///     references the returned content address inside the turn text with a strict canonical
///     marker; the turn build path splits the submitted text into the remaining text plus the
///     ordered references, which become blob reference data parts. Only the exact canonical
///     form is a marker — uppercase hex, reordered or missing fields, stray whitespace, a bad
///     media type token, or an unterminated quote stay ordinary text — so a hand-written
///     look-alike can never become a reference and a client newer than its server degrades to
///     literal text.
/// </summary>
internal static class FrontendAttachmentMarkers
{
    /// <summary>One parsed marker: the content address, media type, and display name exactly
    /// as the frontend wrote them (the name's quote/backslash escapes undone).</summary>
    internal sealed record Marker(string Sha256, string MediaType, string Name);

    /// <summary>The text with every well-formed marker removed, and the markers in order of
    /// appearance. The remainder is verbatim — whitespace collapsing is a display-only
    /// projection and stays client-side.</summary>
    internal sealed record SplitResult(string Remainder, IReadOnlyList<Marker> Markers);

    // Ported from the extension's attachments.ts grammar; both sides must agree exactly.
    // Control characters never appear inside a well-formed marker (emitters replace them),
    // and `"` / `\` travel escaped as \" and \\.
    private static readonly Regex MarkerPattern = new(
        """
        \[\[maieutics:object\ sha256=(?<sha>[0-9a-f]{64})\ mime=(?<mime>[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*)\ name="(?<name>(?:[^"\\\x00-\x1f\x7f]|\\["\\])*)"\]\]
        """,
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Splits submitted turn text into its remaining text and ordered markers.</summary>
    internal static SplitResult Split(string text)
    {
        var matches = MarkerPattern.Matches(text);
        if (matches.Count == 0) return new SplitResult(text, []);

        var markers = new List<Marker>(matches.Count);
        var remainder = new System.Text.StringBuilder(text.Length);
        var position = 0;
        foreach (Match match in matches)
        {
            remainder.Append(text, position, match.Index - position);
            position = match.Index + match.Length;
            markers.Add(new Marker(
                match.Groups["sha"].Value,
                match.Groups["mime"].Value,
                UnescapeName(match.Groups["name"].Value)));
        }

        remainder.Append(text, position, text.Length - position);
        return new SplitResult(remainder.ToString(), markers);
    }

    private static string UnescapeName(string name)
    {
        return name.Contains('\\') ? Regex.Replace(name, @"\\([""\\])", "$1") : name;
    }
}
