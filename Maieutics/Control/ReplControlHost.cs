using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Agent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Maieutics.Control;

/// <summary>
/// Owns the process-wide HTTP and WebSocket control channel shared by all Deno REPL children.
/// The channel is mapped onto the single application host; HTTP requests are attributed through
/// peer process identity, while WebSocket bus connections bind to a session through the
/// <c>control.hello</c> handshake.
/// </summary>
internal sealed class ReplControlHost : IDisposable
{
    private const int WebSocketBufferSize = 256 * 1024;
    private const int EnvelopeVersion = 1;
    private readonly string socketPath;
    private readonly ReplControlSessionRegistry registry;
    private readonly ILogger<ReplControlHost> logger;
    private readonly IReadOnlyList<AIFunction> scriptTools;
    private readonly ConcurrentDictionary<string, WebSocket> connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> comms = new(StringComparer.Ordinal);
    private readonly ReplOperationRegistry operations = new();

    public ReplControlHost(
        string socketPath,
        ReplControlSessionRegistry registry,
        ILogger<ReplControlHost> logger,
        IReadOnlyList<AIFunction>? scriptTools = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        this.socketPath = socketPath;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.scriptTools = scriptTools ?? [];
    }

    /// <summary>Gets the unix domain socket path the channel listens on.</summary>
    public string SocketPath => socketPath;

    /// <summary>Creates a short socket path within the platform unix socket length limit.</summary>
    internal static string CreateSocketPath()
    {
        var name = $"mc-{Guid.NewGuid():N}"[..15] + ".sock";
        return Path.Combine(Path.GetTempPath(), name);
    }

    /// <summary>Maps the control channel middleware and endpoints onto the application.</summary>
    internal void MapEndpoints(WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            if (!Authorize(context))
            {
                logger.LogWarning("Rejected control channel connection with an unexpected peer identity.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        application.UseWebSockets();
        application.MapGet("/health", () => Results.Text("ok"));
        application.Map("/ws", HandleWebSocketAsync);
        application.MapPost("/v1/tool.invoke", HandleToolInvokeAsync);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(socketPath);
        }
        catch
        {
            // Kestrel removes the socket file on close; best-effort cleanup must not mask shutdown.
        }
    }

    private bool Authorize(HttpContext context)
    {
        var peerSocket = GetPeerSocket(context);
        if (peerSocket is null ||
            !PeerProcessCredentials.TryGetPeerIdentity(peerSocket, out var peerProcessId, out var peerUserId))
        {
            return false;
        }

        if (peerProcessId > 0)
        {
            return registry.TryGetSession(peerProcessId, out _);
        }

        return peerUserId > 0 && peerUserId == PeerProcessCredentials.GetCurrentUserId();
    }

    private static Socket? GetPeerSocket(HttpContext context)
    {
        return context.Features.Get<IConnectionSocketFeature>()?.Socket;
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var peerProcessId = ResolvePeerProcessId(context);
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var sessionId = await ReceiveHelloAsync(socket, peerProcessId, context.RequestAborted).ConfigureAwait(false);
        if (sessionId is null)
        {
            await socket
                .CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "session not established",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        if (connections.TryGetValue(sessionId, out var previous) && previous.State == WebSocketState.Open)
        {
            await previous
                .CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None)
                .ConfigureAwait(false);
        }

        connections[sessionId] = socket;
        try
        {
            await PushAsync(
                sessionId,
                new ReplEnvelope(EnvelopeVersion, ReplMessageType.ControlReady),
                context.RequestAborted).ConfigureAwait(false);
            while (socket.State == WebSocketState.Open)
            {
                var text = await ReadTextMessageAsync(socket, context.RequestAborted).ConfigureAwait(false);
                if (text is null)
                {
                    break;
                }

                await HandleBusMessageAsync(sessionId, text, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            if (connections.TryGetValue(sessionId, out var current) && ReferenceEquals(current, socket))
            {
                connections.TryRemove(sessionId, out _);
            }

            comms.TryRemove(sessionId, out _);
        }
    }

    private int ResolvePeerProcessId(HttpContext context)
    {
        var peerSocket = GetPeerSocket(context);
        if (peerSocket is not null &&
            PeerProcessCredentials.TryGetPeerIdentity(peerSocket, out var processId, out _))
        {
            return processId;
        }

        return 0;
    }

    private async Task<string?> ReceiveHelloAsync(WebSocket socket, int peerProcessId, CancellationToken ct)
    {
        var text = await ReadTextMessageAsync(socket, ct).ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        ReplEnvelope? envelope;
        try
        {
            envelope = ParseEnvelope(text);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope.Version != EnvelopeVersion ||
            envelope.Type != ReplMessageType.ControlHello ||
            envelope.Payload is not { } payload ||
            !payload.TryGetProperty("sessionId", out var session) ||
            string.IsNullOrWhiteSpace(session.GetString()))
        {
            return null;
        }

        var sessionId = session.GetString()!;
        if (peerProcessId > 0)
        {
            return registry.IsOwnedBy(peerProcessId, sessionId) ? sessionId : null;
        }

        return registry.ContainsSession(sessionId) ? sessionId : null;
    }

    private async Task HandleBusMessageAsync(string sessionId, string text, CancellationToken ct)
    {
        ReplEnvelope envelope;
        try
        {
            envelope = ParseEnvelope(text);
        }
        catch (JsonException)
        {
            await PushErrorAsync(
                sessionId,
                "invalid_envelope",
                "The message is not a valid envelope.",
                correlationId: null,
                ct).ConfigureAwait(false);
            return;
        }

        switch (envelope.Type)
        {
            case ReplMessageType.ControlPing:
                await PushAsync(
                    sessionId,
                    new ReplEnvelope(EnvelopeVersion, ReplMessageType.ControlPong, envelope.CorrelationId),
                    ct).ConfigureAwait(false);
                break;
            case ReplMessageType.ControlCancel:
                var cancel = ParsePayload<BusCancelPayload>(envelope);
                if (cancel is null || string.IsNullOrWhiteSpace(cancel.CorrelationId))
                {
                    await PushErrorAsync(
                        sessionId,
                        "invalid_cancel",
                        "The cancel message requires a correlationId.",
                        envelope.CorrelationId,
                        ct).ConfigureAwait(false);
                    break;
                }

                var cancelled = operations.TryCancel(cancel.CorrelationId);
                await PushAsync(
                    sessionId,
                    new ReplEnvelope(
                        EnvelopeVersion,
                        cancelled ? ReplMessageType.ControlCancelled : ReplMessageType.Error,
                        cancel.CorrelationId,
                        cancelled ? null : Payload(new BusErrorPayload("operation_not_found", "No in-flight operation has this correlationId."))),
                    ct).ConfigureAwait(false);
                break;
            case ReplMessageType.CommOpen:
                var open = ParsePayload<BusCommPayload>(envelope);
                if (open is null || string.IsNullOrWhiteSpace(open.CommId))
                {
                    await PushErrorAsync(
                        sessionId,
                        "invalid_comm",
                        "The comm.open message requires a commId.",
                        envelope.CorrelationId,
                        ct).ConfigureAwait(false);
                    break;
                }

                GetComms(sessionId).TryAdd(open.CommId, 0);
                await PushAckAsync(sessionId, open.CommId, ok: true, envelope.CorrelationId, ct).ConfigureAwait(false);
                break;
            case ReplMessageType.CommMsg:
                var message = ParsePayload<BusCommPayload>(envelope);
                if (message is null || !GetComms(sessionId).ContainsKey(message.CommId))
                {
                    await PushErrorAsync(
                        sessionId,
                        "comm_not_open",
                        "The comm channel must be opened before sending messages.",
                        envelope.CorrelationId,
                        ct).ConfigureAwait(false);
                    break;
                }

                await PushAckAsync(sessionId, message.CommId, ok: true, envelope.CorrelationId, ct).ConfigureAwait(false);
                break;
            case ReplMessageType.CommClose:
                var close = ParsePayload<BusCommPayload>(envelope);
                if (close is not null)
                {
                    GetComms(sessionId).TryRemove(close.CommId, out _);
                }

                await PushAckAsync(
                    sessionId,
                    close?.CommId ?? string.Empty,
                    ok: true,
                    envelope.CorrelationId,
                    ct).ConfigureAwait(false);
                break;
            default:
                await PushErrorAsync(
                    sessionId,
                    "unknown_message",
                    $"Unknown message type '{envelope.Type}'.",
                    envelope.CorrelationId,
                    ct).ConfigureAwait(false);
                break;
        }
    }

    private ConcurrentDictionary<string, byte> GetComms(string sessionId) =>
        comms.GetOrAdd(sessionId, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));

    private async Task PushAckAsync(
        string sessionId,
        string commId,
        bool ok,
        string? correlationId,
        CancellationToken ct)
    {
        await PushAsync(
            sessionId,
            new ReplEnvelope(
                EnvelopeVersion,
                ReplMessageType.CommAck,
                correlationId,
                Payload(new BusAckPayload(commId, ok))),
            ct).ConfigureAwait(false);
    }

    private async Task PushErrorAsync(
        string sessionId,
        string code,
        string message,
        string? correlationId,
        CancellationToken ct)
    {
        await PushAsync(
            sessionId,
            new ReplEnvelope(
                EnvelopeVersion,
                ReplMessageType.Error,
                correlationId,
                Payload(new BusErrorPayload(code, message))),
            ct).ConfigureAwait(false);
    }

    private async Task PushAsync(string sessionId, ReplEnvelope envelope, CancellationToken ct)
    {
        if (!connections.TryGetValue(sessionId, out var socket) || socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(envelope, ReplControlJsonContext.Default.ReplEnvelope);
        await socket
            .SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct)
            .ConfigureAwait(false);
    }

    private static ReplEnvelope ParseEnvelope(string text) =>
        JsonSerializer.Deserialize(text, ReplControlJsonContext.Default.ReplEnvelope)
        ?? throw new JsonException("The envelope is null.");

    private static T? ParsePayload<T>(ReplEnvelope envelope)
        where T : class
    {
        if (envelope.Payload is not { } payload)
        {
            return null;
        }

        return (T?)JsonSerializer.Deserialize(payload.GetRawText(), JsonTypeInfoFor<T>());
    }

    private static JsonTypeInfo JsonTypeInfoFor<T>() => typeof(T) switch
    {
        _ when typeof(T) == typeof(BusCancelPayload) => ReplControlJsonContext.Default.BusCancelPayload,
        _ when typeof(T) == typeof(BusCommPayload) => ReplControlJsonContext.Default.BusCommPayload,
        _ when typeof(T) == typeof(BusErrorPayload) => ReplControlJsonContext.Default.BusErrorPayload,
        _ when typeof(T) == typeof(BusAckPayload) => ReplControlJsonContext.Default.BusAckPayload,
        _ => throw new InvalidOperationException($"Unsupported bus payload type '{typeof(T).Name}'.")
    };

    private static JsonElement Payload<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonTypeInfoFor<T>());

    private static async Task<string?> ReadTextMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[WebSocketBufferSize];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", ct)
                    .ConfigureAwait(false);
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                continue;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task HandleToolInvokeAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        ToolInvokeRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                ReplControlJsonContext.Default.ToolInvokeRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (request is null || request.Version != 1 || string.IsNullOrWhiteSpace(request.Tool))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var function = scriptTools.FirstOrDefault(candidate => candidate.Name == request.Tool);
        if (function is null)
        {
            await WriteEnvelopeAsync(
                context,
                ToolJson.CreateFailureEnvelope(
                    "tool_not_found",
                    $"Tool '{request.Tool}' is not available to scripts."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var arguments = new AIFunctionArguments(request.Arguments.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));
        var correlationId = request.CorrelationId;
        using var operation = new CancellationTokenSource();
        var invokeToken = cancellationToken;
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            if (!operations.TryRegister(correlationId, operation))
            {
                await WriteEnvelopeAsync(
                    context,
                    ToolJson.CreateFailureEnvelope(
                        "duplicate_correlation",
                        "The correlationId is already in use."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            invokeToken = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken, operation.Token).Token;
        }

        JsonElement envelope;
        try
        {
            var result = await function.InvokeAsync(arguments, invokeToken).ConfigureAwait(false);
            envelope = result switch
            {
                null => ToolJson.CreateSuccessEnvelope(null),
                JsonElement element => ToolJson.CreateSuccessEnvelope(element),
                _ => ToolJson.CreateFailureEnvelope(
                    "tool_invalid_result",
                    "Script tools must return a structured JSON value.")
            };
        }
        catch (OperationCanceledException) when (!string.IsNullOrWhiteSpace(correlationId) && operation.IsCancellationRequested)
        {
            envelope = ToolJson.CreateCancelledEnvelope();
        }
        catch (AgentToolException exception)
        {
            envelope = ToolJson.CreateFailureEnvelope(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Script tool '{Tool}' failed.", request.Tool);
            envelope = ToolJson.CreateFailureEnvelope("tool_failed", exception.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                operations.Remove(correlationId);
            }
        }

        await WriteEnvelopeAsync(context, envelope, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEnvelopeAsync(
        HttpContext context,
        JsonElement envelope,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(envelope.GetRawText(), cancellationToken).ConfigureAwait(false);
    }
}
