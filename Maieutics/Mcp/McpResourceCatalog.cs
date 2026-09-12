using System.Collections.Immutable;
using System.Text;
using Maieutics.Execution;

namespace Maieutics.Mcp;

/// <summary>One concrete MCP resource advertisement (<c>resources/list</c>).</summary>
internal sealed record McpResourceDescriptor(
    string Uri,
    string? Name,
    string? Description,
    string? MimeType);

/// <summary>One MCP resource template advertisement (<c>resources/templates/list</c>);
/// <see cref="UriTemplate"/> is an RFC 6570 string.</summary>
internal sealed record McpResourceTemplateDescriptor(
    string UriTemplate,
    string? Name,
    string? Description,
    string? MimeType);

/// <summary>The resource surface of one MCP server connection, refreshed alongside
/// its tool list. Servers without the resources capability contribute an empty
/// catalog instead of failing the connection (ADR 0026 decision 3).</summary>
internal sealed record McpResourceCatalog(
    ImmutableArray<McpResourceDescriptor> Resources,
    ImmutableArray<McpResourceTemplateDescriptor> Templates)
{
    internal static McpResourceCatalog Empty { get; } = new([], []);

    internal bool IsEmpty => Resources.IsEmpty && Templates.IsEmpty;

    /// <summary>True when the catalog serves the URI: an exact resource match first,
    /// then any RFC 6570 template match (ADR 0026 decision 3).</summary>
    internal bool Contains(string uri)
    {
        return Resources.Any(resource => string.Equals(resource.Uri, uri, StringComparison.Ordinal)) ||
               Templates.Any(template => McpResourceTemplateMatcher.Matches(template.UriTemplate, uri));
    }
}

/// <summary>Read-only snapshot of one server's resource surface plus the connection
/// generation that can read it. Produced by the configuration layer in `mcp.json`
/// server order; the provider resolves and reads through these in order.</summary>
internal sealed class McpResourceServerAccess(
    string id,
    McpResourceCatalog catalog,
    McpServerGeneration generation)
{
    internal string Id { get; } = id;

    internal McpResourceCatalog Catalog { get; } = catalog;

    internal McpServerGeneration Generation { get; } = generation;
}

/// <summary>Exposes the live MCP resource servers in resolution order. Implemented by
/// the runtime configuration, keeping the provider independent of reload mechanics.</summary>
internal interface IMcpResourceCatalogSource
{
    IReadOnlyList<McpResourceServerAccess> GetResourceServers();
}

/// <summary>Minimal RFC 6570 level 1–2 matcher for MCP resource templates:
/// literal text, <c>{var}</c> (one segment, no separators) and <c>{+var}</c>
/// (reserved expansion, may span separators). Sufficient for real-world MCP
/// templates; richer forms simply do not match (ADR 0026 decision 3).</summary>
internal static class McpResourceTemplateMatcher
{
    internal static bool Matches(string template, string candidate)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(candidate);
        if (Tokenize(template) is not { } tokens) return false;

        return Match(tokens, 0, candidate, 0);
    }

    /// <summary>Extracts the provider claim for a template: its scheme, and the
    /// authority when the template spells it out literally before the first
    /// expansion (e.g. <c>postgres://db.example.com/{db}</c>).</summary>
    internal static ResourceClaim? TryGetClaim(string template)
    {
        var separator = template.IndexOf(':');
        if (separator <= 0 || !IsSchemeCharacter(template[0]) ||
            !template.Take(separator).All(IsSchemeCharacter))
            return null;

        var scheme = template[..separator];
        var rest = template[(separator + 1)..];
        if (!rest.StartsWith("//", StringComparison.Ordinal)) return new ResourceClaim(scheme);

        rest = rest[2..];
        var authorityEnd = rest.FindFirstIndexOf('/', '{');
        if (authorityEnd < 0) authorityEnd = rest.Length;

        var authority = rest[..authorityEnd];
        return authority.Contains('{') || authority.Length == 0
            ? new ResourceClaim(scheme)
            : new ResourceClaim(scheme, authority);
    }

    private static bool Match(ImmutableArray<object> tokens, int tokenIndex, string candidate, int charIndex)
    {
        if (tokenIndex == tokens.Length) return charIndex == candidate.Length;

        switch (tokens[tokenIndex])
        {
            case string literal:
                return candidate.AsSpan(charIndex).StartsWith(literal, StringComparison.Ordinal) &&
                       Match(tokens, tokenIndex + 1, candidate, charIndex + literal.Length);
            case Expansion { Reserved: true }:
                // A reserved expansion may span separators; every split point is
                // tried so a following literal can pull the match back shorter.
                for (var end = candidate.Length; end > charIndex; end--)
                    if (Match(tokens, tokenIndex + 1, candidate, end))
                        return true;

                return false;
            case Expansion:
                // A simple expansion covers one non-empty segment without "/", "?", "#".
                var segmentEnd = charIndex;
                while (segmentEnd < candidate.Length &&
                       candidate[segmentEnd] is not ('/' or '?' or '#'))
                    segmentEnd++;

                for (var end = segmentEnd; end > charIndex; end--)
                    if (Match(tokens, tokenIndex + 1, candidate, end))
                        return true;

                return false;
            default:
                throw new InvalidOperationException("Unknown template token.");
        }
    }

    /// <summary>Parses a template into alternating literals and expansions; null when
    /// the template uses an unsupported RFC 6570 operator (such as <c>{?var}</c>) or
    /// malformed syntax. Unsupported templates never match.</summary>
    private static ImmutableArray<object>? Tokenize(string template)
    {
        var tokens = ImmutableArray.CreateBuilder<object>();
        var literalStart = 0;
        while (literalStart < template.Length)
        {
            var brace = template.IndexOf('{', literalStart);
            if (brace < 0)
            {
                tokens.Add(template[literalStart..]);
                break;
            }

            if (brace > literalStart) tokens.Add(template[literalStart..brace]);

            var close = template.IndexOf('}', brace);
            if (close < brace + 2) return null;

            var expansion = template[(brace + 1)..close];
            var reserved = expansion.StartsWith('+');
            var name = reserved ? expansion[1..] : expansion;
            if (name.Length == 0 || !name.All(IsVarChar)) return null;

            tokens.Add(new Expansion(name, reserved));
            literalStart = close + 1;
        }

        return tokens.ToImmutable();
    }

    internal static bool IsSchemeCharacter(char value)
    {
        return char.IsAsciiLetterOrDigit(value) || value is '+' or '-' or '.';
    }

    private static bool IsVarChar(char value)
    {
        return char.IsAsciiLetterOrDigit(value) || value == '_';
    }

    private readonly record struct Expansion(string Name, bool Reserved);
}

internal static class ResourceTemplateStringExtensions
{
    internal static int FindFirstIndexOf(this string value, char first, char second)
    {
        for (var index = 0; index < value.Length; index++)
            if (value[index] == first || value[index] == second)
                return index;

        return -1;
    }
}
