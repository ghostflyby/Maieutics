using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Maieutics.Jupyter.Shared;

public interface IJupyterMessageSerializer
{
    string Delimiter { get; }

    IReadOnlyList<byte[]> Serialize(JupyterMessage message);

    JupyterMessage Deserialize(IReadOnlyList<byte[]> frames);
}

public sealed class JupyterMessageSerializer(string key) : IJupyterMessageSerializer
{
    private const string WireDelimiter = "<IDS|MSG>";

    private static readonly byte[] DelimiterBytes = Encoding.UTF8.GetBytes(WireDelimiter);
    private readonly byte[] _key = Encoding.UTF8.GetBytes(key);

    public string Delimiter => WireDelimiter;

    public IReadOnlyList<byte[]> Serialize(JupyterMessage message)
    {
        var header = SerializeJson(message.Header);
        var parentHeader = message.ParentHeader is null
            ? SerializeJson(new JsonObject())
            : SerializeJson(message.ParentHeader);
        var metadata = SerializeJson(message.Metadata);
        var content = SerializeJson(message.Content);

        var signature = Sign(header, parentHeader, metadata, content);
        var frames = new List<byte[]>(message.Identities.Count + message.Buffers.Count + 6);

        frames.AddRange(message.Identities);
        frames.Add(DelimiterBytes);
        frames.Add(Encoding.UTF8.GetBytes(signature));
        frames.Add(header);
        frames.Add(parentHeader);
        frames.Add(metadata);
        frames.Add(content);
        frames.AddRange(message.Buffers);

        return frames;
    }

    public JupyterMessage Deserialize(IReadOnlyList<byte[]> frames)
    {
        var delimiterIndex = FindDelimiter(frames);
        if (delimiterIndex < 0)
        {
            throw new JupyterProtocolException("Jupyter wire message did not contain the <IDS|MSG> delimiter.");
        }

        if (frames.Count < delimiterIndex + 6)
        {
            throw new JupyterProtocolException("Jupyter wire message did not contain all required header frames.");
        }

        var signature = Encoding.UTF8.GetString(frames[delimiterIndex + 1]);
        var headerFrame = frames[delimiterIndex + 2];
        var parentHeaderFrame = frames[delimiterIndex + 3];
        var metadataFrame = frames[delimiterIndex + 4];
        var contentFrame = frames[delimiterIndex + 5];
        var expectedSignature = Sign(headerFrame, parentHeaderFrame, metadataFrame, contentFrame);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expectedSignature)))
        {
            throw new JupyterProtocolException("Jupyter wire message signature verification failed.");
        }

        return new JupyterMessage(
            frames.Take(delimiterIndex).Select(Clone).ToArray(),
            DeserializeJson<JupyterMessageHeader>(headerFrame),
            DeserializeNullableHeader(parentHeaderFrame),
            DeserializeJsonObject(metadataFrame),
            DeserializeJsonObject(contentFrame),
            frames.Skip(delimiterIndex + 6).Select(Clone).ToArray());
    }

    private static int FindDelimiter(IReadOnlyList<byte[]> frames)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].AsSpan().SequenceEqual(DelimiterBytes))
            {
                return i;
            }
        }

        return -1;
    }

    private string Sign(params byte[][] frames)
    {
        if (_key.Length == 0)
        {
            return string.Empty;
        }

        using var hmac = new HMACSHA256(_key);
        foreach (var frame in frames)
        {
            hmac.TransformBlock(frame, 0, frame.Length, null, 0);
        }

        hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(hmac.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    private static byte[] SerializeJson<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
    }

    private static T DeserializeJson<T>(byte[] bytes)
    {
        return JsonSerializer.Deserialize<T>(bytes, Json.Options)
               ?? throw new JupyterProtocolException($"Could not deserialize {typeof(T).Name}.");
    }

    private static JupyterMessageHeader? DeserializeNullableHeader(byte[] bytes)
    {
        var node = JsonNode.Parse(bytes);
        if (node is not JsonObject obj || obj.Count == 0)
        {
            return null;
        }

        return obj.Deserialize<JupyterMessageHeader>(Json.Options);
    }

    private static JsonObject DeserializeJsonObject(byte[] bytes)
    {
        return JsonNode.Parse(bytes)?.AsObject()
               ?? throw new JupyterProtocolException("Expected a JSON object frame.");
    }

    private static byte[] Clone(byte[] value)
    {
        var copy = new byte[value.Length];
        Buffer.BlockCopy(value, 0, copy, 0, value.Length);
        return copy;
    }

    private static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}

public sealed class JupyterProtocolException(string message) : Exception(message);