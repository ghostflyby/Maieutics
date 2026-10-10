using System.Collections.Immutable;

namespace Maieutics.Mcp;

/// <summary>One MCP prompt advertisement (<c>prompts/list</c>), refreshed alongside the
/// tool list inside the connection generation (ADR 0041). The prompt reference charset
/// governs referencability — the MCP spec's own example name is snake_case, so the
/// charset admits underscore and case (unlike the skill catalog's host-grade charset),
/// while still excluding everything the reference grammar's markdown-link form cannot
/// carry.</summary>
internal sealed record McpPromptDescriptor(
    string Name,
    string? Title,
    string? Description,
    ImmutableArray<McpPromptArgumentDescriptor> Arguments);

/// <summary>One declared prompt argument.</summary>
internal sealed record McpPromptArgumentDescriptor(
    string Name,
    string? Description,
    bool Required);

/// <summary>The prompt surface of one MCP server connection. Servers without the
/// prompts capability (or refusing the listing) contribute an empty catalog instead of
/// failing the connection or the refresh — the resource-catalog tolerance, applied to
/// the third consumer face (ADR 0041 decision 1).</summary>
internal sealed record McpPromptCatalog(ImmutableArray<McpPromptDescriptor> Prompts)
{
    internal static McpPromptCatalog Empty { get; } = new([]);

    internal bool IsEmpty => Prompts.IsEmpty;
}

/// <summary>Read-only snapshot of one server's prompt surface plus the connection
/// generation that can expand a prompt (ADR 0041) — the resource-access shape on the
/// third consumer face. Produced by the configuration layer in id order; the frontend
/// expansion resolves through these.</summary>
internal sealed class McpPromptServerAccess(
    string id,
    McpPromptCatalog catalog,
    McpServerGeneration generation)
{
    internal string Id { get; } = id;

    internal McpPromptCatalog Catalog { get; } = catalog;

    internal McpServerGeneration Generation { get; } = generation;
}

/// <summary>Exposes the live MCP prompt surfaces in id order. Implemented by the
/// runtime configuration, keeping the frontend expansion independent of reload
/// mechanics (the resource-catalog-source seam, on the prompt face).</summary>
internal interface IMcpPromptServerSource
{
    IReadOnlyList<McpPromptServerAccess> GetPromptServers();
}

/// <summary>The prompt reference grammar's charset decisions (ADR 0041 decision 2),
/// shared by the kernel parser, the catalog's referencability marking, and tests.</summary>
internal static class McpPromptReferenceGrammar
{
    /// <summary>A prompt name is referencable when it fits
    /// <c>^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$</c>: the MCP spec's snake_case example
    /// names are the ecosystem's dominant convention and must be selectable. The
    /// charset carries no <c>/</c> (segment split is from the right), no
    /// <c>? # % )</c> (they would break the markdown-link grammar or the query),
    /// and no whitespace.</summary>
    internal static bool IsValidPromptName(string name)
    {
        if (name is not { Length: > 0 } || name.Length > 64) return false;
        if (!char.IsAsciiLetterOrDigit(name[0])) return false;
        foreach (var character in name)
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
                return false;
        return true;
    }

    /// <summary>A serverId is a referencable reference segment when it contains none
    /// of the characters the grammar's literal-prefix regexes and markdown-link form
    /// cannot carry. The id is an opaque token regardless; this only gates whether a
    /// reference to its prompts can be minted (referencable: false otherwise).</summary>
    internal static bool IsValidServerIdSegment(string serverId)
    {
        return serverId is { Length: > 0 } && serverId.All(static character =>
            character is not ('/' or '?' or '#' or '%' or '[' or ']' or '(' or ')' or '\\') &&
            !char.IsWhiteSpace(character));
    }
}
