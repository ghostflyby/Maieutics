using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Maieutics.Jupyter.Shared;

public sealed record JupyterMessage(
    IReadOnlyList<byte[]> Identities,
    JupyterMessageHeader Header,
    JupyterMessageHeader? ParentHeader,
    JsonObject Metadata,
    JsonObject Content,
    IReadOnlyList<byte[]> Buffers)
{
    public string MessageType => Header.MessageType;

    public static JupyterMessage Create(
        string messageType,
        JsonObject content,
        JupyterSessionIdentity session,
        JupyterMessageHeader? parentHeader = null,
        JsonObject? metadata = null,
        IReadOnlyList<byte[]>? identities = null,
        IReadOnlyList<byte[]>? buffers = null)
    {
        return new JupyterMessage(
            identities ?? [],
            JupyterMessageHeader.Create(messageType, session),
            parentHeader,
            metadata ?? new JsonObject(),
            content,
            buffers ?? []);
    }
}

public sealed record JupyterMessageHeader(
    [property: JsonPropertyName("msg_id")] string MessageId,
    [property: JsonPropertyName("username")]
    string Username,
    [property: JsonPropertyName("session")]
    string Session,
    [property: JsonPropertyName("date")] DateTimeOffset Date,
    [property: JsonPropertyName("msg_type")]
    string MessageType,
    [property: JsonPropertyName("version")]
    string Version)
{
    public static JupyterMessageHeader Create(string messageType, JupyterSessionIdentity session)
    {
        return new JupyterMessageHeader(
            Guid.NewGuid().ToString("N"),
            session.Username,
            session.SessionId,
            DateTimeOffset.UtcNow,
            messageType,
            "5.3");
    }
}

public sealed record JupyterSessionIdentity(string SessionId, string Username)
{
    public static JupyterSessionIdentity Create(string username = "maieutics")
    {
        return new JupyterSessionIdentity(Guid.NewGuid().ToString("N"), username);
    }
}