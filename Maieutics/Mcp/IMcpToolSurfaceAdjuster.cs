using System.Text.Json;

namespace Maieutics.Mcp;

/// <summary>Projects one server's tool listing through the MCP adjustment chain
/// (ADR 0034). Implementations see declarations only — tool names, descriptions,
/// and input schemas — never tool-call traffic, which always routes to the owning
/// server. Returns the adjusted listing (entries map input identities via
/// <c>aliasOf</c>; omission removes the tool), or <c>null</c> when nothing may be
/// exposed (the chain failed and no previous adjusted listing exists).</summary>
internal interface IMcpToolSurfaceAdjuster
{
    Task<JsonElement?> AdjustToolsAsync(
        string serverId,
        JsonElement listing,
        CancellationToken cancellationToken);
}
