using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.DenoRepl;

/// <summary>
///     Versioned wire contract for the REPL output endpoint
///     (<c>/v1/repl/output/ws</c>). The endpoint carries every non-comm output
///     event the REPL actor produces — console, display/updateDisplay, and
///     clearOutput. Unlike the eval endpoint's JSON envelopes, frames are
///     binary: string MIME data rides in a JSON bundle while binary MIME
///     values (image bytes) travel as native byte buffers, so binary data is
///     never text/base64 encoded on the wire (AGENTS.md invariant 26). The
///     endpoint is half-duplex (process -> host only): the C# host only
///     receives frames and never sends.
///
///     Frame layout (all integers big-endian):
///     <code>[type:1] [seq:8 uint64] [executionIdLen:2 uint16] [executionId UTF-8] [payload...]</code>
///     - type 0 (stdout):  payload = UTF-8 text
///     - type 1 (stderr):  payload = UTF-8 text
///     - type 2 (display): payload =
///         [bundleJsonLen:4 uint32] [bundleJson UTF-8]
///         [metadataLen:4 uint32] [metadata UTF-8]
///         [displayIdLen:2 uint16] [displayId UTF-8]
///         [isUpdate:1] [bufferCount:2 uint16] [bufLen:4 uint32] [buf] ...
///       bundleJson values are strings or JSON objects verbatim; a binary value
///       is replaced by the placeholder <c>{"$buffer": index}</c> where index
///       is the buffer's position in the trailing buffers section. The
///       placeholder is kept as a JSON value on the parsed frame and is rebuilt
///       by the execution collector (the display payload keeps the trailing
///       <see cref="ReplOutputDisplayFrame.Buffers" /> list).
///     - type 3 (clearOutput): payload = [wait:1]
///     The 64 MiB frame ceiling is a safety guard for the binary buffers, not
///     a functional limit; the eval control endpoint keeps its own 1 MiB
///     ceiling.
/// </summary>
internal static class ReplOutputProtocol
{
    internal const int Version = 1;
    internal const string OutputPath = "/v1/repl/output/ws";
    internal const int MaximumMessageBytes = 64 * 1024 * 1024;
    internal const int QueueCapacity = 64;

    /// <summary>The largest frame sequence the wire accepts: the JS safe integer ceiling
    /// (2^53 - 1). Values above it cannot round-trip through the TS encoder.</summary>
    internal const ulong MaximumSafeSequence = (1UL << 53) - 1;
}

/// <summary>The binary frame types of the REPL output endpoint.</summary>
internal enum ReplOutputFrameType : byte
{
    Stdout = 0,
    Stderr = 1,
    Display = 2,
    ClearOutput = 3
}

/// <summary>Base type of every decoded REPL output frame.</summary>
internal abstract record ReplOutputFrame(ReplOutputFrameType Type, long Seq, string ExecutionId);

internal sealed record ReplOutputConsoleFrame(
    long Seq,
    string ExecutionId,
    string Stream,
    string Text) : ReplOutputFrame(Stream == "stderr" ? ReplOutputFrameType.Stderr : ReplOutputFrameType.Stdout, Seq, ExecutionId);

/// <summary>
///     A decoded display/updateDisplay frame. <see cref="Data" /> retains the
///     wire placeholder <c>{"$buffer": index}</c> for binary MIME values; the
///     actual bytes travel in <see cref="Buffers" />. The collector rebuilds
///     the placeholders into native byte arrays (phase 3) through
///     <see cref="ResolveBuffer" />.
/// </summary>
internal sealed record ReplOutputDisplayFrame(
    long Seq,
    string ExecutionId,
    IReadOnlyDictionary<string, JsonElement> Data,
    IReadOnlyDictionary<string, JsonElement> Metadata,
    string? DisplayId,
    bool IsUpdate,
    IReadOnlyList<byte[]> Buffers) : ReplOutputFrame(ReplOutputFrameType.Display, Seq, ExecutionId)
{
    /// <summary>Returns the trailing buffer at <paramref name="index" />, or throws a typed protocol
    /// exception when the placeholder index is out of range (wire corruption).</summary>
    internal byte[] ResolveBuffer(int index)
    {
        if (index < 0 || index >= Buffers.Count)
            throw new ReplOutputProtocolException(
                "invalid_buffer_reference",
                $"The REPL output display references buffer '{index}', which is out of range (count {Buffers.Count}).");
        return Buffers[index];
    }
}

internal sealed record ReplOutputClearOutputFrame(
    long Seq,
    string ExecutionId,
    bool Wait) : ReplOutputFrame(ReplOutputFrameType.ClearOutput, Seq, ExecutionId);

/// <summary>Typed protocol error for REPL output frame decode failures.</summary>
internal sealed class ReplOutputProtocolException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    internal string Code { get; } = code;
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 64)]
[JsonSerializable(typeof(byte[]))]
internal sealed partial class ReplOutputJsonContext : JsonSerializerContext;

/// <summary>
///     Reads complete binary REPL output messages off a WebSocket. Each message
///     is accumulated into an <see cref="ArrayBufferWriter{T}" /> until
///     EndOfMessage (like the control reader, but binary and with the 64 MiB
///     output ceiling) and decoded as one <see cref="ReplOutputFrame" />.
///     Returns <c>null</c> when the peer closes the connection. A text message,
///     an oversized message, or any decode violation throws a typed
///     <see cref="ReplOutputProtocolException" />.
/// </summary>
internal static class ReplOutputFrameReader
{
    private const int ReceiveBufferBytes = 64 * 1024;

    internal static async Task<ReplOutputFrame?> ReadAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        using var rented = MemoryPool<byte>.Shared.Rent(ReceiveBufferBytes);
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await socket.ReceiveAsync(rented.Memory, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseOutputAsync(
                    socket,
                    WebSocketCloseStatus.NormalClosure,
                    "closed",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                await CloseOutputAsync(
                    socket,
                    WebSocketCloseStatus.InvalidMessageType,
                    "REPL output messages must be binary",
                    cancellationToken).ConfigureAwait(false);
                throw new ReplOutputProtocolException(
                    "invalid_message_type",
                    "The REPL output endpoint only accepts binary frames.");
            }

            if (result.Count > ReplOutputProtocol.MaximumMessageBytes - writer.WrittenCount)
            {
                await CloseOutputAsync(
                    socket,
                    WebSocketCloseStatus.MessageTooBig,
                    $"REPL output message exceeds {ReplOutputProtocol.MaximumMessageBytes} bytes",
                    cancellationToken).ConfigureAwait(false);
                throw new ReplOutputProtocolException(
                    "message_too_large",
                    $"REPL output messages must not exceed {ReplOutputProtocol.MaximumMessageBytes} bytes.");
            }

            writer.Write(rented.Memory.Span[..result.Count]);
            if (result.EndOfMessage) return Decode(writer.WrittenSpan);
        }
    }

    private static async Task CloseOutputAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseOutputAsync(status, description, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decodes one native binary output frame into its structured form. Mirrors the TS
    /// <c>decodeOutputFrame</c> wire contract exactly.</summary>
    internal static ReplOutputFrame Decode(ReadOnlySpan<byte> data)
    {
        var source = data;
        var typeByte = ReadByte(ref source);
        if (typeByte > (byte)ReplOutputFrameType.ClearOutput)
            throw new ReplOutputProtocolException(
                "unknown_frame_type",
                $"Unknown REPL output frame type '{typeByte}'.");

        var type = (ReplOutputFrameType)typeByte;
        var rawSeq = ReadUInt64(ref source);
        if (rawSeq == 0 || rawSeq > ReplOutputProtocol.MaximumSafeSequence)
            throw new ReplOutputProtocolException(
                "invalid_sequence",
                "The REPL output frame sequence must be a positive safe integer.");
        var seq = checked((long)rawSeq);

        var executionIdLength = ReadUInt16(ref source);
        var executionId = ReadUtf8(ref source, executionIdLength, "execution id");
        if (executionId.Length == 0)
            throw new ReplOutputProtocolException(
                "invalid_execution_id",
                "The REPL output frame requires a non-empty execution id.");

        switch (type)
        {
            case ReplOutputFrameType.Stdout:
            case ReplOutputFrameType.Stderr:
                var text = Encoding.UTF8.GetString(source);
                return type == ReplOutputFrameType.Stdout
                    ? new ReplOutputConsoleFrame(seq, executionId, "stdout", text)
                    : new ReplOutputConsoleFrame(seq, executionId, "stderr", text);

            case ReplOutputFrameType.Display:
                return DecodeDisplay(seq, executionId, source);

            case ReplOutputFrameType.ClearOutput:
                if (source.Length != 1)
                    throw new ReplOutputProtocolException(
                        "invalid_frame",
                        "The REPL output clearOutput frame has an invalid payload length.");
                return new ReplOutputClearOutputFrame(seq, executionId, source[0] != 0);

            default:
                throw new ReplOutputProtocolException(
                    "unknown_frame_type",
                    $"Unknown REPL output frame type '{typeByte}'.");
        }
    }

    private static ReplOutputDisplayFrame DecodeDisplay(long seq, string executionId, ReadOnlySpan<byte> source)
    {
        var bundleJsonLength = ReadUInt32(ref source);
        var bundle = ParseJson(ReadBytes(ref source, bundleJsonLength), "display bundle");
        var metadataLength = ReadUInt32(ref source);
        var metadata = ParseJson(ReadBytes(ref source, metadataLength), "display metadata");
        var displayIdLength = ReadUInt16(ref source);
        var displayId = ReadUtf8(ref source, displayIdLength, "display id");
        var isUpdate = ReadByte(ref source) != 0;
        var bufferCount = ReadUInt16(ref source);
        var buffers = new List<byte[]>(bufferCount);
        for (var index = 0; index < bufferCount; index++)
        {
            var bufferLength = ReadUInt32(ref source);
            buffers.Add(ReadBytes(ref source, bufferLength).ToArray());
        }

        if (source.Length != 0)
            throw new ReplOutputProtocolException(
                "invalid_frame",
                "The REPL output display frame has trailing bytes.");
        if (bundle.ValueKind != JsonValueKind.Object || metadata.ValueKind != JsonValueKind.Object)
            throw new ReplOutputProtocolException(
                "invalid_bundle",
                "The REPL output display bundle and metadata must be JSON objects.");

        return new ReplOutputDisplayFrame(
            seq,
            executionId,
            ToDictionary(bundle),
            ToDictionary(metadata),
            displayId.Length == 0 ? null : displayId,
            isUpdate,
            buffers);
    }

    private static JsonElement ParseJson(ReadOnlySpan<byte> bytes, string what)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new ReplOutputProtocolException(
                "invalid_json",
                $"The REPL output {what} is not valid JSON.",
                exception);
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> ToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) result[property.Name] = property.Value.Clone();
        return result;
    }

    private static byte ReadByte(ref ReadOnlySpan<byte> source)
    {
        if (source.Length == 0)
            throw Truncated();
        var value = source[0];
        source = source[1..];
        return value;
    }

    private static ushort ReadUInt16(ref ReadOnlySpan<byte> source)
    {
        Require(source, 2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(source);
        source = source[2..];
        return value;
    }

    private static uint ReadUInt32(ref ReadOnlySpan<byte> source)
    {
        Require(source, 4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(source);
        source = source[4..];
        return value;
    }

    private static ulong ReadUInt64(ref ReadOnlySpan<byte> source)
    {
        Require(source, 8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(source);
        source = source[8..];
        return value;
    }

    private static ReadOnlySpan<byte> ReadBytes(ref ReadOnlySpan<byte> source, uint length)
    {
        if (length > int.MaxValue)
            throw new ReplOutputProtocolException(
                "invalid_frame",
                "The REPL output frame declares a length beyond the message ceiling.");
        return ReadBytes(ref source, (int)length);
    }

    private static ReadOnlySpan<byte> ReadBytes(ref ReadOnlySpan<byte> source, int length)
    {
        if (length < 0 || source.Length < length)
            throw Truncated();
        var value = source[..length];
        source = source[length..];
        return value;
    }

    private static string ReadUtf8(ref ReadOnlySpan<byte> source, int length, string what)
    {
        var bytes = ReadBytes(ref source, length);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ReplOutputProtocolException(
                "invalid_utf8",
                $"The REPL output {what} is not valid UTF-8.",
                exception);
        }
    }

    private static void Require(ReadOnlySpan<byte> source, int length)
    {
        if (source.Length < length) throw Truncated();
    }

    private static ReplOutputProtocolException Truncated()
    {
        return new ReplOutputProtocolException(
            "invalid_frame",
            "The REPL output frame is truncated.");
    }
}
