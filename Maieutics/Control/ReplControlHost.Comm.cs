using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Maieutics.Control;

/// <summary>
///     Dedicated WebSocket path for Jupyter comm traffic between the kernel and a REPL child.
///     This channel is separate from the control bus: it carries comm messages only, with binary
///     buffers as native bytes (no base64). Messages are a fixed binary encoding:
///     <c>[kind:1][commIdLen:2][commId][targetNameLen:2][targetName][dataLen:4][data][bufferCount:2][bufLen:4][buf]...</c>.
///     The first frame after accept is a JSON hello declaring the session id, verified against the
///     peer process identity like the control bus.
/// </summary>
internal sealed partial class ReplControlHost
{
    private const string CommHelloProperty = "sessionId";
    private readonly ConcurrentDictionary<string, CommBusConnection> commConnections = new(StringComparer.Ordinal);

    private void MapCommEndpoint(WebApplication application)
    {
        application.Map("/comm", HandleCommWebSocketAsync);
    }

    private async Task HandleCommWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var peerProcessId = ReplControlPeerProcess.GetProcessId(context);
        var authorizedIdentity = context.Items.TryGetValue(AuthorizedIdentityItem, out var value) &&
                                 value is string identityValue
            ? identityValue
            : null;
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var sessionId = await ReceiveCommHelloAsync(socket, peerProcessId, authorizedIdentity, context.RequestAborted)
            .ConfigureAwait(false);
        if (sessionId is null)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket
                    .CloseOutputAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "comm identity not established",
                        context.RequestAborted)
                    .ConfigureAwait(false);
            return;
        }

        var connection = new CommBusConnection(socket);
        if (commConnections.TryGetValue(sessionId, out var previous) && previous.State == WebSocketState.Open)
            await previous
                .CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None)
                .ConfigureAwait(false);

        commConnections[sessionId] = connection;
        try
        {
            await connection.SendTextAsync(
                """{"type":"comm.ready"}""",
                context.RequestAborted).ConfigureAwait(false);
            while (socket.State == WebSocketState.Open)
            {
                var frames = await CommFrameReader.ReadAsync(socket, context.RequestAborted).ConfigureAwait(false);
                if (frames is null) break;

                var message = CommCodec.Decode(frames);
                await RouteCommToFrontendAsync(message, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            commConnections.TryRemove(KeyValuePair.Create(sessionId, connection));
        }
    }

    private async Task<string?> ReceiveCommHelloAsync(
        WebSocket socket,
        int peerProcessId,
        string? authorizedIdentity,
        CancellationToken ct)
    {
        var text = await ReplControlMessageReader.ReadAsync(socket, ct).ConfigureAwait(false);
        if (text is null) return null;

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(CommHelloProperty, out var session) ||
                session.GetString() is not { } sessionId || sessionId.IsWhiteSpace())
                return null;

            if ((peerProcessId > 0 && registry.IsOwnedBy(peerProcessId, sessionId)) ||
                string.Equals(authorizedIdentity, sessionId, StringComparison.Ordinal) ||
                (peerProcessId <= 0 && authorizedIdentity is null && registry.ContainsSession(sessionId)))
                return sessionId;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            document?.Dispose();
        }

        return null;
    }

    /// <summary>Pushes a comm message to the REPL child of a session over its comm WebSocket.</summary>
    internal async Task PushCommMessageAsync(
        string sessionId,
        JupyterCommMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(message);
        if (!commConnections.TryGetValue(sessionId, out var connection) || connection.State != WebSocketState.Open)
            throw new InvalidOperationException(
                $"Session '{sessionId}' does not have an open comm connection.");

        await connection.SendAsync(CommCodec.Encode(message), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RouteCommToFrontendAsync(
        JupyterCommMessage message,
        CancellationToken cancellationToken)
    {
        var sink = commFrontendSink;
        if (sink is null) return;

        await sink(message, cancellationToken).ConfigureAwait(false);
    }

    internal static class CommCodec
    {
        internal static byte[] Encode(JupyterCommMessage message)
        {
            var kind = (byte)message.Kind;
            var commId = Encoding.UTF8.GetBytes(message.CommId);
            var targetName = message.TargetName is null ? [] : Encoding.UTF8.GetBytes(message.TargetName);
            var data = message.Data is { } dataElement
                ? JsonSerializer.SerializeToUtf8Bytes(dataElement, JupyterJsonContext.Default.JsonElement)
                : [];
            var metadata = message.Metadata is { } metadataElement
                ? JsonSerializer.SerializeToUtf8Bytes(metadataElement, JupyterJsonContext.Default.JsonElement)
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

        internal static JupyterCommMessage Decode(byte[] frames)
        {
            var offset = 0;
            var kind = (JupyterCommKind)frames[offset++];
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

            return new JupyterCommMessage(
                kind,
                commId,
                targetName,
                data,
                metadata,
                buffers,
                JupyterWireMessage.Create(
                    new JupyterMessage(
                        JupyterMessageHeader.Create("comm_msg", JupyterSessionIdentity.Create("maieutics")),
                        null,
                        JupyterJson.EmptyObject,
                        data ?? JupyterJson.EmptyObject)));
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

    private static class CommFrameReader
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

                if (result.Count > ReplControlLimits.MaximumCommMessageBytes - writer.WrittenCount)
                {
                    await CloseOutputAsync(
                        socket,
                        WebSocketCloseStatus.MessageTooBig,
                        $"comm message exceeds {ReplControlLimits.MaximumCommMessageBytes} bytes",
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

    private sealed record CommOutgoingMessage(byte[] Payload, WebSocketMessageType MessageType);

    private sealed class CommBusConnection : IAsyncDisposable
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Channel<CommOutgoingMessage> outgoing;
        private readonly WebSocket socket;
        private readonly Task writer;

        internal CommBusConnection(WebSocket socket)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            outgoing = Channel.CreateBounded<CommOutgoingMessage>(new BoundedChannelOptions(
                ReplControlLimits.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            writer = RunWriterAsync();
        }

        internal WebSocketState State => socket.State;

        internal Task Completion => completion.Task;

        internal Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(text);
            return SendAsync(
                Encoding.UTF8.GetBytes(text),
                WebSocketMessageType.Text,
                cancellationToken);
        }

        internal Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            return SendAsync(payload, WebSocketMessageType.Binary, cancellationToken);
        }

        private async Task SendAsync(
            byte[] payload,
            WebSocketMessageType messageType,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (payload.Length > ReplControlLimits.MaximumCommMessageBytes)
                throw new InvalidOperationException("The comm message exceeds the maximum message size.");

            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                throw new InvalidOperationException("The comm WebSocket is not open.");

            try
            {
                await outgoing.Writer
                    .WriteAsync(new CommOutgoingMessage(payload, messageType), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new InvalidOperationException("The comm WebSocket channel is closed.");
            }
        }

        internal Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string statusDescription,
            CancellationToken cancellationToken)
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                return Task.CompletedTask;
            return socket.CloseAsync(closeStatus, statusDescription, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            outgoing.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            lifetime.Dispose();
            completion.TrySetResult();
        }

        private async Task RunWriterAsync()
        {
            try
            {
                await foreach (var item in outgoing.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                    await socket
                        .SendAsync(item.Payload, item.MessageType, true, lifetime.Token)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }
}
