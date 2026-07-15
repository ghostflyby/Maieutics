using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Maieutics.Jupyter.Shared;

public interface IJupyterMessageSerializer
{
    string Delimiter { get; }

    IReadOnlyList<byte[]> Serialize(JupyterWireMessage message);

    JupyterWireMessage Deserialize(IReadOnlyList<byte[]> frames);
}

public sealed class JupyterMessageSerializer : IJupyterMessageSerializer
{
    private const string WireDelimiter = "<IDS|MSG>";
    private const string SupportedSignatureScheme = "hmac-sha256";
    private static readonly byte[] DelimiterBytes = Encoding.UTF8.GetBytes(WireDelimiter);
    private readonly byte[] key;

    public JupyterMessageSerializer(string key, string signatureScheme = SupportedSignatureScheme)
    {
        if (!string.Equals(signatureScheme, SupportedSignatureScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Jupyter signature scheme '{signatureScheme}' is not supported.");
        }

        this.key = Encoding.UTF8.GetBytes(key);
    }

    public string Delimiter => WireDelimiter;

    public IReadOnlyList<byte[]> Serialize(JupyterWireMessage wireMessage)
    {
        var message = wireMessage.Message;
        var header =
            JsonSerializer.SerializeToUtf8Bytes(message.Header, JupyterJsonContext.Default.JupyterMessageHeader);
        var parentHeader = message.ParentHeader is null
            ? JupyterJson.EmptyObjectUtf8
            : JsonSerializer.SerializeToUtf8Bytes(message.ParentHeader,
                JupyterJsonContext.Default.JupyterMessageHeader);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(message.Metadata, JupyterJsonContext.Default.JsonElement);
        var content = JsonSerializer.SerializeToUtf8Bytes(message.Content, JupyterJsonContext.Default.JsonElement);
        var signature = Sign(header, parentHeader, metadata, content);
        var frames = new List<byte[]>(wireMessage.Identities.Count + wireMessage.Buffers.Count + 6);

        frames.AddRange(wireMessage.Identities.Select(Clone));
        frames.Add(DelimiterBytes);
        frames.Add(Encoding.ASCII.GetBytes(signature));
        frames.Add(header);
        frames.Add(parentHeader);
        frames.Add(metadata);
        frames.Add(content);
        frames.AddRange(wireMessage.Buffers.Select(Clone));
        return frames;
    }

    public JupyterWireMessage Deserialize(IReadOnlyList<byte[]> frames)
    {
        var delimiterIndex = FindDelimiter(frames);
        if (delimiterIndex < 0)
        {
            throw new JupyterProtocolException("Jupyter wire message did not contain the <IDS|MSG> delimiter.");
        }

        if (frames.Count < delimiterIndex + 6)
        {
            throw new JupyterProtocolException("Jupyter wire message did not contain all required frames.");
        }

        var signatureFrame = frames[delimiterIndex + 1];
        var headerFrame = frames[delimiterIndex + 2];
        var parentHeaderFrame = frames[delimiterIndex + 3];
        var metadataFrame = frames[delimiterIndex + 4];
        var contentFrame = frames[delimiterIndex + 5];
        var expected = Encoding.ASCII.GetBytes(Sign(headerFrame, parentHeaderFrame, metadataFrame, contentFrame));

        if (!CryptographicOperations.FixedTimeEquals(signatureFrame, expected))
        {
            throw new JupyterProtocolException("Jupyter wire message signature verification failed.");
        }

        var header = JsonSerializer.Deserialize(headerFrame, JupyterJsonContext.Default.JupyterMessageHeader)
                     ?? throw new JupyterProtocolException("Jupyter message header was empty.");
        var parentHeader = DeserializeParentHeader(parentHeaderFrame);
        var metadata = ParseObject(metadataFrame, "metadata");
        var content = ParseObject(contentFrame, "content");

        return new JupyterWireMessage(
            frames.Take(delimiterIndex).Select(Clone).ToArray(),
            new JupyterMessage(header, parentHeader, metadata, content),
            frames.Skip(delimiterIndex + 6).Select(Clone).ToArray());
    }

    private string Sign(params byte[][] frames)
    {
        if (key.Length == 0)
        {
            return string.Empty;
        }

        using var hmac = new HMACSHA256(key);
        foreach (var frame in frames)
        {
            hmac.TransformBlock(frame, 0, frame.Length, null, 0);
        }

        hmac.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hmac.Hash ?? []).ToLowerInvariant();
    }

    private static int FindDelimiter(IReadOnlyList<byte[]> frames)
    {
        for (var index = 0; index < frames.Count; index++)
        {
            if (frames[index].AsSpan().SequenceEqual(DelimiterBytes))
            {
                return index;
            }
        }

        return -1;
    }

    private static JupyterMessageHeader? DeserializeParentHeader(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JupyterProtocolException("Jupyter parent header must be a JSON object.");
        }

        if (!document.RootElement.EnumerateObject().Any())
        {
            return null;
        }

        return document.RootElement.Deserialize(JupyterJsonContext.Default.JupyterMessageHeader);
    }

    private static JsonElement ParseObject(byte[] bytes, string frameName)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JupyterProtocolException($"Jupyter {frameName} frame must be a JSON object.");
        }

        return document.RootElement.Clone();
    }

    private static byte[] Clone(byte[] value) => value.ToArray();
}

public sealed class JupyterProtocolException : Exception
{
    public JupyterProtocolException(string message)
        : base(message)
    {
    }

    public JupyterProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}