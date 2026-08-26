using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Maieutics.Jupyter.Shared;

[JsonConverter(typeof(JupyterMessageIdJsonConverter))]
public readonly record struct JupyterMessageId(string Value)
{
    public static JupyterMessageId Create()
    {
        return new JupyterMessageId(Guid.NewGuid().ToString("N"));
    }

    public override string ToString()
    {
        return Value;
    }
}

public sealed record JupyterMessage(
    JupyterMessageHeader Header,
    JupyterMessageHeader? ParentHeader,
    JsonElement Metadata,
    JsonElement Content,
    string? Channel = null)
{
    public string MessageType => Header.MessageType;

    public static JupyterMessage Create<TContent>(
        string messageType,
        TContent content,
        JsonTypeInfo<TContent> contentType,
        JupyterSessionIdentity session,
        JupyterMessageHeader? parentHeader = null,
        JsonElement? metadata = null)
    {
        return new JupyterMessage(
            JupyterMessageHeader.Create(messageType, session),
            parentHeader,
            metadata ?? JupyterJson.EmptyObject,
            JsonSerializer.SerializeToElement(content, contentType));
    }

    public TContent GetContent<TContent>(JsonTypeInfo<TContent> contentType)
    {
        return Content.Deserialize(contentType)
               ?? throw new JupyterProtocolException(
                   $"Message '{MessageType}' did not contain valid {typeof(TContent).Name} content.");
    }
}

public sealed record JupyterWireMessage(
    IReadOnlyList<byte[]> Identities,
    JupyterMessage Message,
    IReadOnlyList<byte[]> Buffers)
{
    public static JupyterWireMessage Create(JupyterMessage message)
    {
        return new JupyterWireMessage([], message, []);
    }
}

public sealed class JupyterMessageHeader
{
    public const string CurrentProtocolVersion = "5.5";

    [JsonPropertyName("msg_id")] public JupyterMessageId MessageId { get; init; }

    [JsonPropertyName("username")] public string Username { get; init; } = string.Empty;

    [JsonPropertyName("session")] public string Session { get; init; } = string.Empty;

    [JsonPropertyName("date")] public DateTimeOffset Date { get; init; }

    [JsonPropertyName("msg_type")] public string MessageType { get; init; } = string.Empty;

    [JsonPropertyName("version")] public string Version { get; init; } = CurrentProtocolVersion;

    [JsonPropertyName("subshell_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubshellId { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public static JupyterMessageHeader Create(string messageType, JupyterSessionIdentity session)
    {
        return new JupyterMessageHeader
        {
            MessageId = JupyterMessageId.Create(),
            Username = session.Username,
            Session = session.SessionId,
            Date = DateTimeOffset.UtcNow,
            MessageType = messageType,
            Version = CurrentProtocolVersion
        };
    }
}

public sealed record JupyterSessionIdentity(string SessionId, string Username)
{
    public static JupyterSessionIdentity Create(string username = "maieutics")
    {
        return new JupyterSessionIdentity(Guid.NewGuid().ToString("N"), username);
    }
}

internal sealed class JupyterMessageIdJsonConverter : JsonConverter<JupyterMessageId>
{
    public override JupyterMessageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new JupyterMessageId(reader.GetString()
                                    ?? throw new JsonException("Jupyter message id must be a string."));
    }

    public override void Write(Utf8JsonWriter writer, JupyterMessageId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}