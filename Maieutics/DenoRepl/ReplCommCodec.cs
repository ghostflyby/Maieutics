using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Maieutics.DenoRepl;

/// <summary>Ceilings for the comm plane, owned beside the codec both hops share.</summary>
internal static class ReplCommLimits
{
    /// <summary>Per-message ceiling for comm traffic. Comm carries widget state and native
    /// media buffers (ipywidgets Image/Audio/Video values travel as raw bytes in the message
    /// buffers), so a single comm message can legitimately reach a few MB; 16 MiB covers
    /// typical media buffers. Exceeding the ceiling closes the offending connection only —
    /// the REPL process and its eval/output channels keep running.</summary>
    internal const int MaximumMessageBytes = 16 * 1024 * 1024;
}

/// <summary>
///     The fixed binary encoding for one comm message, shared by every hop a comm message
///     takes (REPL child ↔ host, host ↔ frontend; ADR 0024). Layout, all lengths big-endian:
///     <c>[kind:1][commIdLen:2][commId][targetNameLen:2][targetName][dataLen:4][data][metadataLen:4][metadata][bufferCount:2][bufLen:4][buf]...</c>.
///     <c>data</c> and <c>metadata</c> are UTF-8 JSON with an empty length meaning absent;
///     buffers are native bytes, never base64 (invariant 26).
/// </summary>
internal static class ReplCommCodec
{
    internal static byte[] Encode(ReplCommMessage message)
    {
        var kind = (byte)message.Kind;
        var commId = Encoding.UTF8.GetBytes(message.CommId);
        var targetName = message.TargetName is null ? [] : Encoding.UTF8.GetBytes(message.TargetName);
        var data = message.Data is { } dataElement
            ? JsonSerializer.SerializeToUtf8Bytes(dataElement, ReplJsonContext.Default.JsonElement)
            : [];
        var metadata = message.Metadata is { } metadataElement
            ? JsonSerializer.SerializeToUtf8Bytes(metadataElement, ReplJsonContext.Default.JsonElement)
            : [];
        var buffers = message.Buffers;

        var total = 1 + 2 + commId.Length + 2 + targetName.Length + 4 + data.Length +
            4 + metadata.Length + 2;
        foreach (var buffer in buffers)
            total += 4 + buffer.Length;

        var result = new byte[total];
        var offset = 0;
        result[offset++] = kind;
        WriteUInt16(result, ref offset, commId.Length);
        commId.CopyTo(result, offset);
        offset += commId.Length;
        WriteUInt16(result, ref offset, targetName.Length);
        targetName.CopyTo(result, offset);
        offset += targetName.Length;
        WriteUInt32(result, ref offset, data.Length);
        data.CopyTo(result, offset);
        offset += data.Length;
        WriteUInt32(result, ref offset, metadata.Length);
        metadata.CopyTo(result, offset);
        offset += metadata.Length;
        WriteUInt16(result, ref offset, buffers.Count);
        foreach (var buffer in buffers)
        {
            WriteUInt32(result, ref offset, buffer.Length);
            buffer.CopyTo(result, offset);
            offset += buffer.Length;
        }

        return result;
    }

    internal static ReplCommMessage Decode(byte[] frames)
    {
        var offset = 0;
        var kind = (ReplCommKind)frames[offset++];
        var commIdLength = ReadUInt16(frames, ref offset);
        var commId = Encoding.UTF8.GetString(frames, offset, commIdLength);
        offset += commIdLength;
        var targetNameLength = ReadUInt16(frames, ref offset);
        var targetName = targetNameLength == 0
            ? null
            : Encoding.UTF8.GetString(frames, offset, targetNameLength);
        offset += targetNameLength;
        var dataLength = ReadUInt32(frames, ref offset);
        JsonElement? data = dataLength == 0
            ? null
            : JsonDocument.Parse(frames.AsMemory(offset, dataLength)).RootElement.Clone();
        offset += dataLength;
        var metadataLength = ReadUInt32(frames, ref offset);
        JsonElement? metadata = metadataLength == 0
            ? null
            : JsonDocument.Parse(frames.AsMemory(offset, metadataLength)).RootElement.Clone();
        offset += metadataLength;
        var bufferCount = ReadUInt16(frames, ref offset);
        var buffers = new List<byte[]>(bufferCount);
        for (var index = 0; index < bufferCount; index++)
        {
            var bufferLength = ReadUInt32(frames, ref offset);
            var buffer = new byte[bufferLength];
            Array.Copy(frames, offset, buffer, 0, bufferLength);
            offset += bufferLength;
            buffers.Add(buffer);
        }

        return new ReplCommMessage(
            kind,
            commId,
            targetName,
            data,
            metadata,
            buffers);
    }

    private static void WriteUInt16(byte[] destination, ref int offset, int value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination.AsSpan(offset, 2), (ushort)value);
        offset += 2;
    }

    private static void WriteUInt32(byte[] destination, ref int offset, int value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset, 4), (uint)value);
        offset += 4;
    }

    private static ushort ReadUInt16(byte[] source, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(offset, 2));
        offset += 2;
        return value;
    }

    private static int ReadUInt32(byte[] source, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset, 4));
        offset += 4;
        return checked((int)value);
    }
}

/// <summary>
///     Reads one complete binary WebSocket message with the comm size ceiling. A close
///     frame ends the stream with a null payload; a text application frame is a protocol
///     violation and ends the stream after a typed close.
/// </summary>
internal static class ReplCommFrameReader
{
    private const int ReceiveBufferBytes = 64 * 1024;

    internal static async Task<byte[]?> ReadAsync(WebSocket socket, CancellationToken cancellationToken)
    {
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
                    "comm messages must be binary",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (result.Count > ReplCommLimits.MaximumMessageBytes - writer.WrittenCount)
            {
                await CloseOutputAsync(
                    socket,
                    WebSocketCloseStatus.MessageTooBig,
                    $"comm message exceeds {ReplCommLimits.MaximumMessageBytes} bytes",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            writer.Write(rented.Memory.Span[..result.Count]);
            if (result.EndOfMessage) return writer.WrittenSpan.ToArray();
        }
    }

    private static Task CloseOutputAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        return socket.State is WebSocketState.Open or WebSocketState.CloseReceived
            ? socket.CloseOutputAsync(status, description, cancellationToken)
            : Task.CompletedTask;
    }
}
