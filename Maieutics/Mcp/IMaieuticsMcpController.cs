namespace Maieutics.Mcp;

internal interface IMaieuticsMcpController
{
    IReadOnlyList<MaieuticsMcpServerInfo> GetMcpServers();
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