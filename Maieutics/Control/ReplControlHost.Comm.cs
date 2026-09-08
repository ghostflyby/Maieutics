using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Maieutics.DenoRepl;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Maieutics.Control;

/// <summary>
///     Dedicated WebSocket path for comm traffic between the host and a REPL child. This
///     channel is separate from the control bus: it carries comm messages only, with binary
///     buffers as native bytes (no base64). Messages use the shared fixed binary encoding
///     <see cref="ReplCommCodec"/>. The first frame after accept is a JSON hello declaring
///     the session id, verified against the peer process identity like the control bus.
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
                var frames = await ReplCommFrameReader.ReadAsync(socket, context.RequestAborted).ConfigureAwait(false);
                if (frames is null) break;

                var message = ReplCommCodec.Decode(frames);
                await RouteCommToFrontendAsync(sessionId, message, context.RequestAborted).ConfigureAwait(false);
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
        ReplCommMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(message);
        if (!commConnections.TryGetValue(sessionId, out var connection) || connection.State != WebSocketState.Open)
            throw new InvalidOperationException(
                $"Session '{sessionId}' does not have an open comm connection.");

        await connection.SendAsync(ReplCommCodec.Encode(message), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RouteCommToFrontendAsync(
        string sessionId,
        ReplCommMessage message,
        CancellationToken cancellationToken)
    {
        var sink = commFrontendSink;
        if (sink is null) return;

        await sink(sessionId, message, cancellationToken).ConfigureAwait(false);
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
