namespace Maieutics.Mcp;

internal interface IMaieuticsMcpController
{
    IReadOnlyList<MaieuticsMcpServerInfo> GetMcpServers();
}

/// <summary>Supplies the current workspace root path to MCP servers that were granted the
/// roots capability (ADR 0029): one root, read live on every server query so workspace
/// switches take effect without notification machinery.</summary>
internal interface IMcpWorkspaceRootsSource
{
    /// <summary>Gets the current workspace root path, or null when no workspace is open.</summary>
    string? GetRootPath();
}

internal sealed record MaieuticsMcpServerInfo(
    string Id,
    string Transport,
    MaieuticsMcpServerState State,
    TimeSpan? NextReconnectDelay,
    IReadOnlyList<MaieuticsMcpToolInfo> Tools);

internal sealed record MaieuticsMcpToolInfo(
    string RemoteName,
    string ExposedName,
    bool Available);

internal enum MaieuticsMcpServerState
{
    Connected,
    Degraded,
    Reconnecting
}