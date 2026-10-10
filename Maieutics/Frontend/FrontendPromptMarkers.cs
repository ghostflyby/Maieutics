using System.Text.RegularExpressions;

namespace Maieutics.Frontend;

/// <summary>
///     The server-side half of the MCP prompt-reference grammar (ADR 0041,
///     <c>docs/web-frontend-protocol.md</c> "Prompt references"): a standard markdown
///     link over the dedicated <c>mcp-prompt://</c> scheme —
///     <c>[text](mcp-prompt://serverId/name?args)</c>. The serverId segment is an
///     opaque token, deliberately not a valid generic URI authority: the operative
///     invariant is that a prompt reference never enters a generic URI parser — this
///     regex IS the parser, matching on the literal scheme prefix. The name split is
///     from the right (prompt names carry no <c>/</c>, server ids may). Only the exact
///     form is a reference; anything else stays ordinary text. A backslash directly
///     before the opening bracket suppresses recognition and is consumed — the mention
///     form. Query keys and values must be percent-encoded with the RFC 3986
///     unreserved set kept literal; raw reserved characters fail the match.
/// </summary>
internal static class FrontendPromptMarkers
{
    /// <summary>One parsed reference.</summary>
    /// <param name="ServerId">The opaque server id segment.</param>
    /// <param name="Name">The prompt name segment.</param>
    /// <param name="Text">The link display text, exactly as written.</param>
    /// <param name="Arguments">Decoded arguments in written order; duplicate keys
    /// surface as <see cref="SplitResult"/> diagnostics-carriers via the marker's
    /// <see cref="DuplicateKeys"/> flag.</param>
    internal sealed record Marker(
        string ServerId,
        string Name,
        string Text,
        IReadOnlyDictionary<string, string> Arguments,
        bool DuplicateKeys);

    internal sealed record SplitResult(string Remainder, IReadOnlyList<Marker> Markers);

    private const string PromptName = @"[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}";
    private const string ServerId = @"[^/?#\[\]()\\\s]+";
    // The query alphabet is the RFC 3986 unreserved set plus pct-escapes; keys are the
    // stricter encoder output (no sub-delims, so '=' cannot smuggle into a key), values
    // admit the sub-delims the encoder was told to keep — but never the parens that
    // would terminate the markdown link. Encoded and literal units interleave, so the
    // atom is (literal* escape*)*, not a single literal* followed by escapes.
    private const string QueryKey = @"(?:[A-Za-z0-9\-._~]+|%[0-9A-Fa-f]{2})+";
    private const string QueryValue = @"(?:[A-Za-z0-9\-._~!$&'*+,;=:@]+|%[0-9A-Fa-f]{2})*";
    private const string QueryPair = $"{QueryKey}={QueryValue}";

    private static readonly Regex MarkerPattern = new(
        $@"(?<!\\)\[(?<text>[^\]\n]*)\]\(mcp-prompt://(?<server>{ServerId})/(?<name>{PromptName})(?:\?(?<query>{QueryPair}(?:&{QueryPair})*))?\)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex MentionEscapePattern = new(
        $"""\\(\[[^\]\n]*\]\(mcp-prompt://{ServerId}/{PromptName}(?:\?{QueryPair}(?:&{QueryPair})*)?\))""",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex PairPattern = new(@"(?<key>[^=]+)=(?<value>.*)", RegexOptions.CultureInvariant);

    /// <summary>Splits submitted turn text into its remaining text and ordered prompt
    /// references; backslash-suppressed occurrences stay in the remainder with the
    /// escape consumed.</summary>
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
            markers.Add(Parse(
                match.Groups["server"].Value,
                match.Groups["name"].Value,
                match.Groups["text"].Value,
                match.Groups["query"].Success ? match.Groups["query"].Value : null));
        }

        remainder.Append(text, position, text.Length - position);
        return new SplitResult(Unescape(remainder.ToString()), markers);
    }

    private static Marker Parse(string serverId, string name, string text, string? query)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicate = false;
        if (query is not null)
        {
            foreach (var pair in query.Split('&'))
            {
                var match = PairPattern.Match(pair);
                var key = Uri.UnescapeDataString(match.Groups["key"].Value);
                var value = Uri.UnescapeDataString(match.Groups["value"].Value);
                if (arguments.ContainsKey(key)) duplicate = true;
                else arguments.Add(key, value);
            }
        }

        return new Marker(serverId, name, text, arguments, duplicate);
    }

    private static string Unescape(string text)
    {
        return MentionEscapePattern.IsMatch(text)
            ? MentionEscapePattern.Replace(text, "$1")
            : text;
    }
}
