using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Maieutics.Agent;
using System.Buffers.Binary;
using Maieutics.Commands;
using Maieutics.DenoRepl;
using Maieutics.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics.Frontend;

/// <summary>
///     Maps the frontend web API onto the shared application host: a bearer-token middleware
///     scoped to the frontend paths (the child-process control bus keeps its own peer-identity
///     middleware), REST endpoints for sessions/turns/commands, and the per-session events
///     WebSocket. The events endpoint is half-duplex (executable to frontend only); the
///     frontend reconnects with <c>sinceSequence</c> after any disconnect, including the
///     backpressure disconnect the run stream performs instead of dropping frames.
/// </summary>
internal sealed class FrontendHost : IAsyncDisposable
{
    private static readonly HashSet<string> FrontendPrefixes = new(StringComparer.Ordinal)
    {
        "/v1/agent",
        "/v1/model",
        "/v1/status",
        "/v1/objects"
    };

    private readonly FrontendOptions options;
    private readonly FrontendSessionService service;
    private readonly ObjectStore? objectStore;
    private readonly FrontendCommRouter? commRouter;
    private readonly ILogger<FrontendHost> logger;
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] expectedToken;
    private int disposeState;

    public FrontendHost(
        FrontendOptions options,
        FrontendSessionService service,
        ILogger<FrontendHost> logger,
        ObjectStore? objectStore = null,
        FrontendCommRouter? commRouter = null)
    {
        this.options = options;
        this.service = service;
        this.logger = logger;
        this.objectStore = objectStore;
        this.commRouter = commRouter;
        expectedToken = Encoding.UTF8.GetBytes(options.Token);
    }

    /// <summary>Terminates every WebSocket so Kestrel shutdown does not wait on upgrades.</summary>
    internal void BeginShutdown()
    {
        _ = lifetime.CancelAsync();
    }

    /// <summary>Maps the frontend middleware and endpoints. Call before the control bus maps
    /// its own middleware so frontend requests never reach the peer-identity gate.</summary>
    internal void MapEndpoints(WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var endpoints = (IEndpointRouteBuilder)application;
        application.Use(AuthorizeThenNextAsync);
        endpoints.MapGet("/v1/agent/capabilities", HandleCapabilities);
        endpoints.MapGet("/v1/agent/session", HandleSession);
        endpoints.MapPost("/v1/agent/sessions", HandleNewSession);
        endpoints.MapGet("/v1/agent/sessions", HandleListSessions);
        endpoints.MapPost("/v1/agent/sessions/{sessionId}/resume", HandleResumeSession);
        endpoints.MapPost("/v1/agent/sessions/{sessionId}/rename", HandleRenameSession);
        endpoints.MapPost("/v1/agent/sessions/{sessionId}/fork", HandleForkSession);
        endpoints.MapPost("/v1/agent/sessions/{sessionId}/gc", HandleGcSession);
        endpoints.MapPost("/v1/agent/sessions/{sessionId}/repair", HandleRepairSession);
        endpoints.MapPost("/v1/agent/sessions/{sessionId}/turns", HandleTurn);
        endpoints.MapGet("/v1/agent/sessions/{sessionId}/transcript", HandleTranscript);
        endpoints.MapGet("/v1/agent/sessions/{sessionId}/events", HandleEvents);
        endpoints.MapGet("/v1/agent/sessions/{sessionId}/comms", HandleComms);
        endpoints.MapPost("/v1/agent/runs/{runId}/cancel", HandleCancel);
        endpoints.MapGet("/v1/model/profiles", HandleModelProfiles);
        endpoints.MapPost("/v1/agent/commands", HandleCommand);
        endpoints.MapPost("/v1/agent/complete", HandleComplete);
        endpoints.MapGet("/v1/status", HandleStatus);
        endpoints.MapGet("/v1/objects/{objectId}", HandleObject);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) == 0) await lifetime.CancelAsync().ConfigureAwait(false);
    }

    private async Task AuthorizeThenNextAsync(HttpContext context, Func<Task> next)
    {
        if (!FrontendPrefixes.Any(prefix =>
                context.Request.Path.StartsWithSegments(prefix, StringComparison.Ordinal)))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // The browser-standard WebSocket client API cannot carry headers, so the token may
        // arrive as a query parameter on the WebSocket endpoints only.
        var path = context.Request.Path.Value ?? string.Empty;
        var isWebSocketPath = context.Request.Path.StartsWithSegments("/v1/agent/sessions", StringComparison.Ordinal) &&
                              (path.EndsWith("/events", StringComparison.Ordinal) ||
                               path.EndsWith("/comms", StringComparison.Ordinal));
        if (!TryGetBearerToken(context, out var provided) && !(isWebSocketPath &&
                TryGetQueryToken(context, out provided)) ||
            !CryptographicOperations.FixedTimeEquals(expectedToken, provided))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new FrontendError("unauthorized", "A valid frontend bearer token is required."),
                FrontendJsonContext.Default.FrontendError).ConfigureAwait(false);
            return;
        }

        await next().ConfigureAwait(false);
    }

    private bool TryGetBearerToken(HttpContext context, out byte[] token)
    {
        token = [];
        if (!context.Request.Headers.TryGetValue("Authorization", out var authorization)) return false;

        var value = authorization.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        token = Encoding.UTF8.GetBytes(value[prefix.Length..].Trim());
        return token.Length > 0;
    }

    private bool TryGetQueryToken(HttpContext context, out byte[] token)
    {
        token = [];
        var value = context.Request.Query["token"].ToString();
        if (string.IsNullOrWhiteSpace(value)) return false;

        token = Encoding.UTF8.GetBytes(value.Trim());
        return token.Length > 0;
    }

    private IResult HandleCapabilities()
    {
        var session = service.DescribeSession();
        return Results.Json(new FrontendCapabilities(
            FrontendProtocol.Version,
            typeof(FrontendHost).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            session,
            service.WorkspaceRoot,
            commRouter is null
                ? null
                : new FrontendCommCapability(
                    FrontendCommRouter.Version,
                    ReplCommLimits.MaximumMessageBytes),
            MultiSession: true), FrontendJsonContext.Default.FrontendCapabilities);
    }

    private IResult HandleSession()
    {
        return Results.Json(service.DescribeSession(), FrontendJsonContext.Default.FrontendSessionInfo);
    }

    private IResult HandleNewSession()
    {
        return Results.Json(service.StartNew(), FrontendJsonContext.Default.FrontendSessionInfo);
    }

    private IResult HandleListSessions()
    {
        return Results.Json(service.ListStoredSessions(), FrontendJsonContext.Default.FrontendStoredSessionArray);
    }

    private async Task HandleResumeSession(HttpContext context, string sessionId)
    {
        await GuardAsync(context, () => Task.FromResult(
            Results.Json(service.Resume(sessionId), FrontendJsonContext.Default.FrontendSessionInfo)));
    }

    private async Task HandleRenameSession(HttpContext context, string sessionId)
    {
        var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendRenameRequest)
            .ConfigureAwait(false);
        if (request is null) return;

        await GuardAsync(context, () => Task.FromResult(
            Results.Json(service.Rename(sessionId, request.Title), FrontendJsonContext.Default.FrontendRenameResponse)));
    }

    private async Task HandleForkSession(HttpContext context, string sessionId)
    {
        var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendForkRequest)
            .ConfigureAwait(false);
        if (request is null) return;

        await GuardAsync(context, async () => Results.Json(
            await service.ForkAsync(sessionId, request, context.RequestAborted).ConfigureAwait(false),
            FrontendJsonContext.Default.FrontendForkResponse));
    }

    private async Task HandleModelProfiles(HttpContext context)
    {
        await GuardAsync(context, () => Task.FromResult(
            Results.Json(service.ListModelProfiles(), FrontendJsonContext.Default.FrontendModelProfileArray)));
    }

    private async Task HandleGcSession(HttpContext context, string sessionId)
    {
        var graceHours = 24;
        if (context.Request.Query.TryGetValue("graceHours", out var raw) &&
            (!int.TryParse(raw, out graceHours) || graceHours < 0))
        {
            await WriteErrorAsync(context, FrontendErrors.InvalidRequest,
                "The grace period must be a non-negative number of hours.");
            return;
        }

        await GuardAsync(context, () => Task.FromResult<IResult>(
            Results.Json(new FrontendCommandResponse(
                $"**GC** removed {service.PruneObjects(sessionId, graceHours)} unreferenced object(s) (grace {graceHours} h)."),
                FrontendJsonContext.Default.FrontendCommandResponse)));
    }

    private async Task HandleRepairSession(HttpContext context, string sessionId)
    {
        await GuardAsync(context, () => Task.FromResult<IResult>(
            Results.Json(new FrontendCommandResponse(
                    $"**View** ensured {service.RepairObjectView(sessionId)} object link(s) under view/sessions."),
                FrontendJsonContext.Default.FrontendCommandResponse)));
    }

    private async Task HandleTurn(HttpContext context, string sessionId)
    {
        var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendTurnRequest)
            .ConfigureAwait(false);
        if (request is null) return;

        try
        {
            // A command cell executes inline and answers with markdown, so the same cell text keeps its historical
            // semantics. The addressing session scopes session-aware commands; the answer's
            // sessionId lets the frontend re-pin when a command switched the foreground.
            if (MaieuticsCommandLanguage.IsCommandCell(request.Text))
            {
                var addressed = ParseOptionalSessionId(sessionId)
                    ?? throw new FrontendFailureException(
                        FrontendErrors.InvalidRequest, "The session id is not valid.");
                var (markdown, movedForeground) = await service
                    .ExecuteCommandAsync(request.Text, addressed, context.RequestAborted)
                    .ConfigureAwait(false);
                // The addressed session unless THIS command moved the foreground.
                var answerSession = movedForeground
                    ? service.DescribeSession().Id
                    : addressed.Value.ToString("N");
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsJsonAsync(
                    new FrontendCommandResponse(markdown, answerSession),
                    FrontendJsonContext.Default.FrontendCommandResponse).ConfigureAwait(false);
                return;
            }

            var accepted = await service.StartTurnAsync(sessionId, request.Text).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            await context.Response.WriteAsJsonAsync(
                accepted,
                FrontendJsonContext.Default.FrontendTurnAccepted).ConfigureAwait(false);
        }
        catch (MaieuticsCommandException exception)
        {
            await WriteErrorAsync(context, FrontendErrors.CommandError, exception.Message);
        }
        catch (FrontendFailureException exception)
        {
            await WriteErrorAsync(context, exception.Code, exception.Message);
        }
    }

    private async Task HandleTranscript(HttpContext context, string sessionId)
    {
        await GuardAsync(context, () => Task.FromResult(
            Results.Json(service.GetTranscript(sessionId), FrontendJsonContext.Default.FrontendTranscript)));
    }

    private async Task HandleCancel(HttpContext context, string runId)
    {
        await GuardAsync(context, async () =>
        {
            await service.CancelRunAsync(runId, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new FrontendCommandResponse("cancel requested"),
                FrontendJsonContext.Default.FrontendCommandResponse);
        });
    }

    private async Task HandleCommand(HttpContext context)
    {
        var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendCommandRequest)
            .ConfigureAwait(false);
        if (request is null) return;

        try
        {
            var (markdown, _) = await service.ExecuteCommandAsync(request.Text, null, context.RequestAborted)
                .ConfigureAwait(false);
            await context.Response.WriteAsJsonAsync(
                new FrontendCommandResponse(markdown, service.DescribeSession().Id),
                FrontendJsonContext.Default.FrontendCommandResponse).ConfigureAwait(false);
        }
        catch (MaieuticsCommandException exception)
        {
            await WriteErrorAsync(context, FrontendErrors.CommandError, exception.Message);
        }
    }

    /// <summary>Parses a route session id, or returns <see langword="null" /> when it is not
    /// a well-formed id (the caller decides whether that is fatal).</summary>
    private static AgentSessionId? ParseOptionalSessionId(string sessionId)
    {
        return Guid.TryParseExact(sessionId, "N", out var parsed) && parsed != Guid.Empty
            ? new AgentSessionId(parsed)
            : null;
    }

    private async Task HandleComplete(HttpContext context)
    {
        var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendCompleteRequest)
            .ConfigureAwait(false);
        if (request is null) return;

        try
        {
            var completion = service.Complete(request);
            await context.Response.WriteAsJsonAsync(
                completion,
                FrontendJsonContext.Default.FrontendCompleteResponse).ConfigureAwait(false);
        }
        catch (FrontendFailureException exception)
        {
            await WriteErrorAsync(context, exception.Code, exception.Message);
        }
    }

    private async Task HandleStatus(HttpContext context)
    {
        await GuardAsync(context, () => Task.FromResult(
            Results.Json(new FrontendStatusResponse(service.CaptureStatusMarkdown()),
                FrontendJsonContext.Default.FrontendStatusResponse)));
    }

    private async Task HandleObject(HttpContext context, string objectId)
    {
        if (objectId.Length != 64 || objectId.Any(character => !Uri.IsHexDigit(character)))
        {
            await WriteErrorAsync(context, FrontendErrors.InvalidRequest, "The object id is not valid.");
            return;
        }

        if (objectStore is null || !objectStore.Exists(objectId))
        {
            await WriteErrorAsync(context, FrontendErrors.NotFound, $"No object matches '{objectId}'.");
            return;
        }

        // Content-addressed objects are immutable: unconditional client caching is the
        // contract (same URL = same bytes forever).
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        await Results.Stream(objectStore.Open(objectId), "application/octet-stream")
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private async Task HandleEvents(HttpContext context, string sessionId)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        long since = 0;
        if (context.Request.Query.TryGetValue("sinceSequence", out var rawSince) &&
            (!long.TryParse(rawSince, out since) || since < 0))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var singleRunId = context.Request.Query.TryGetValue("runId", out var rawRun)
            ? rawRun.ToString()
            : null;
        // Events are per session: the addressed session need not be the foreground.
        if (!Guid.TryParseExact(sessionId, "N", out var addressedSession) || addressedSession == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!service.SessionExists(sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                new FrontendError(FrontendErrors.NotFound, $"No stored session matches '{sessionId}'."),
                FrontendJsonContext.Default.FrontendError).ConfigureAwait(false);
            return;
        }

        FrontendSessionInfo helloSession = service.DescribeSession(sessionId);

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var peer = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            lifetime.Token);
        await SendFrameAsync(
            socket,
            new FrontendEventFrame("hello", Session: helloSession, Replayed: since > 0),
            peer.Token).ConfigureAwait(false);
        var drain = DrainReceiveAsync(socket, peer.Token);
        try
        {
            if (singleRunId is not null)
            {
                if (!service.TryGetRun(singleRunId, out var runStream) || runStream is null)
                {
                    await SendFrameAsync(
                        socket,
                        new FrontendEventFrame("run.missing", RunId: singleRunId),
                        peer.Token).ConfigureAwait(false);
                }
                else
                {
                    await ServeStreamAsync(socket, runStream, since, peer.Token).ConfigureAwait(false);
                }
            }
            else
            {
                FrontendRunStream? previous = null;
                while (!peer.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var stream = await service
                        .WaitForRunAsync(new AgentSessionId(addressedSession), previous, peer.Token)
                        .ConfigureAwait(false);
                    await ServeStreamAsync(socket, stream, since, peer.Token).ConfigureAwait(false);
                    previous = stream;
                    // Replay offsets are per run; later runs stream live from their start.
                    since = 0;
                }
            }
        }
        catch (OperationCanceledException) when (peer.IsCancellationRequested)
        {
            // Host shutdown or client disconnect; the finally block closes the socket.
        }
        catch (WebSocketException)
        {
            // The peer vanished; nothing to deliver.
        }
        finally
        {
            await peer.CancelAsync().ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "stream ended",
                    CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ServeStreamAsync(
        WebSocket socket,
        FrontendRunStream stream,
        long since,
        CancellationToken cancellationToken)
    {
        var (initial, channel) = stream.Subscribe(since);
        try
        {
            foreach (var frame in initial) await SendFrameAsync(socket, frame, cancellationToken).ConfigureAwait(false);

            await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await SendFrameAsync(socket, frame, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception) when
            (exception.InnerException is FrontendRunStream.FrontendBackpressureException)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(
                    (WebSocketCloseStatus)1011,
                    "backpressure",
                    CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            stream.Unsubscribe(channel);
        }
    }

    /// <summary>
    ///     Serves the session-scoped full-duplex comm WebSocket (ADR 0024). The hello frame
    ///     carries the live-comm snapshot plus replay status; application frames are binary
    ///     codec frames wrapped in the 8-byte envelope sequence. All writes are serialized
    ///     through one gate because a WebSocket allows a single outstanding send: the
    ///     downlink loop and the uplink's error replies would otherwise race.
    /// </summary>
    private async Task HandleComms(HttpContext context, string sessionId)
    {
        if (commRouter is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                new FrontendError(FrontendErrors.NotFound, "The comm plane is not available."),
                FrontendJsonContext.Default.FrontendError).ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        long since = 0;
        if (context.Request.Query.TryGetValue("sinceSeq", out var rawSince) &&
            (!long.TryParse(rawSince, out since) || since < 0))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Comms are per session (ADR 0024); the addressed session need not be the
        // foreground, but it must exist — planes are created on demand.
        if (!Guid.TryParseExact(sessionId, "N", out var commsSession) || commsSession == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!service.SessionExists(sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                new FrontendError(FrontendErrors.NotFound, $"No stored session matches '{sessionId}'."),
                FrontendJsonContext.Default.FrontendError).ConfigureAwait(false);
            return;
        }

        var plane = commRouter.PlaneFor(sessionId);
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var peer = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            lifetime.Token);
        using var sendGate = new System.Threading.SemaphoreSlim(1, 1);
        async Task SendAsync(Func<Task> write)
        {
            await sendGate.WaitAsync(peer.Token).ConfigureAwait(false);
            try
            {
                await write().ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }

        async Task CloseAsyncOutput(WebSocketCloseStatus status, string description)
        {
            await SendAsync(() => socket.CloseOutputAsync(status, description, CancellationToken.None))
                .ConfigureAwait(false);
        }

        // Subscribe before the hello so the snapshot and the replay are one coherent view:
        // an open published in between lands in both initial replay and live, folded by
        // comm id.
        var (initial, channel, truncated) = plane.Subscribe(since);
        var uplink = ServeCommUplinkAsync(socket, commRouter, sessionId, plane, SendAsync, peer.Token);
        try
        {
            await SendAsync(() => SendTextFrameAsync(
                socket,
                JsonSerializer.Serialize(
                    new FrontendCommHello(plane.SnapshotLive(), since > 0, truncated),
                    FrontendJsonContext.Default.FrontendCommHello),
                peer.Token)).ConfigureAwait(false);
            foreach (var record in initial)
            {
                var payload = FrontendCommEnvelope.Encode(record.Sequence, record.Message);
                await SendAsync(() => SendBinaryFrameAsync(socket, payload, peer.Token)).ConfigureAwait(false);
            }

            await foreach (var record in channel.Reader.ReadAllAsync(peer.Token).ConfigureAwait(false))
            {
                var framePayload = FrontendCommEnvelope.Encode(record.Sequence, record.Message);
                await SendAsync(() => SendBinaryFrameAsync(socket, framePayload, peer.Token)).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException exception) when
            (exception.InnerException is FrontendCommStream.FrontendCommBackpressureException)
        {
            // Send-only close: CloseAsync would wait on the close handshake while the
            // uplink task may still hold the socket's single outstanding receive.
            await CloseAsyncOutput((WebSocketCloseStatus)1011, "backpressure").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (peer.IsCancellationRequested)
        {
            // Host shutdown or client disconnect; the finally block closes the socket.
        }
        catch (WebSocketException)
        {
            // The peer vanished; nothing to deliver.
        }
        finally
        {
            plane.Unsubscribe(channel);
            await peer.CancelAsync().ConfigureAwait(false);
            await uplink.ConfigureAwait(false);
            await CloseAsyncOutput(WebSocketCloseStatus.NormalClosure, "stream ended").ConfigureAwait(false);
        }
    }

    /// <summary>Reads uplink comm frames until the socket closes; the loop owns its own
    /// transport failures so a dead socket never faults into the handler's finally.
    /// Rejections are typed comm.error text frames, and a frontend-originated open is a
    /// policy close.</summary>
    private async Task ServeCommUplinkAsync(
        WebSocket socket,
        FrontendCommRouter commRouter,
        string sessionId,
        FrontendCommStream plane,
        Func<Func<Task>, Task> send,
        CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var payload = await ReplCommFrameReader
                    .ReadAsync(socket, cancellationToken)
                    .ConfigureAwait(false);
                if (payload is null) return;

                ReplCommMessage message;
                try
                {
                    // The client does not assign ordering; the server does.
                    message = FrontendCommEnvelope.Decode(payload).Message;
                }
                catch (Exception exception) when (exception is InvalidDataException or
                    JsonException or ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    // Invariant 17: a malformed frame ends this connection with a typed
                    // close instead of crashing the receive loop.
                    await send(() => socket.CloseOutputAsync(
                        WebSocketCloseStatus.InvalidMessageType,
                        "comm frame is malformed",
                        CancellationToken.None)).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await commRouter
                        .PushToReplAsync(sessionId, message, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (FrontendCommRejectException reject) when
                    (reject.Code == FrontendErrors.InvalidRequest)
                {
                    await send(() => socket.CloseOutputAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        reject.Message,
                        CancellationToken.None)).ConfigureAwait(false);
                    return;
                }
                catch (FrontendCommRejectException reject)
                {
                    await send(() => SendTextFrameAsync(
                        socket,
                        JsonSerializer.Serialize(
                            new FrontendEventFrame("comm.error", Code: reject.Code, CommId: reject.CommId),
                            FrontendJsonContext.Default.FrontendEventFrame),
                        cancellationToken)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            // The peer vanished; nothing to deliver.
        }
        catch (InvalidOperationException)
        {
            // A concurrent close won the race for the socket's single outstanding
            // operation; the connection is ending either way.
        }
    }

    private static async Task SendTextFrameAsync(
        WebSocket socket,
        string text,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        await socket
            .SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task SendBinaryFrameAsync(
        WebSocket socket,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await socket
            .SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DrainReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private static async Task SendFrameAsync(
        WebSocket socket,
        FrontendEventFrame frame,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, FrontendJsonContext.Default.FrontendEventFrame);
        await socket
            .SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Runs a handler, executes its result, and renders typed failures as protocol
    /// error bodies.</summary>
    private async Task GuardAsync(HttpContext context, Func<Task<IResult>> handler)
    {
        IResult result;
        try
        {
            result = await handler().ConfigureAwait(false);
        }
        catch (FrontendFailureException exception)
        {
            await WriteErrorAsync(context, exception.Code, exception.Message).ConfigureAwait(false);
            return;
        }

        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    private async Task<T?> ReadJsonAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            var request = await JsonSerializer
                .DeserializeAsync(context.Request.Body, typeInfo, context.RequestAborted)
                .ConfigureAwait(false);
            if (request is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteErrorAsync(context, FrontendErrors.InvalidRequest, "A JSON body is required.");
                return null;
            }

            return request;
        }
        catch (BadHttpRequestException exception) when
            (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return null;
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteErrorAsync(context, FrontendErrors.InvalidRequest, "The request body is not valid JSON.");
            return null;
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, string code, string message)
    {
        if (context.Response.HasStarted) return;

        context.Response.StatusCode = code switch
        {
            FrontendErrors.Busy or FrontendErrors.SessionNotActive or FrontendErrors.ConfigurationError
                => StatusCodes.Status409Conflict,
            FrontendErrors.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest
        };
        context.Response.ContentType = "application/json";
        await context.Response
            .WriteAsync(JsonSerializer.Serialize(
                    new FrontendError(code, message),
                    FrontendJsonContext.Default.FrontendError),
                context.RequestAborted)
            .ConfigureAwait(false);
    }
}

/// <summary>
///     Publishes the discovery file once Kestrel has bound the frontend listener
///     (<see cref="IHostApplicationLifetime.ApplicationStarted" />) and tears the frontend
///     down ahead of the Kestrel shutdown window.
/// </summary>
internal sealed class FrontendHostedService(
    FrontendOptions options,
    FrontendHost host,
    IHostApplicationLifetime lifetime,
    ILogger<FrontendHostedService> logger) : IHostedService
{
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        _ = PublishDiscoveryAsync();
        lifetime.ApplicationStopping.Register(() =>
        {
            host.BeginShutdown();
            options.DeleteDiscoveryFile();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task PublishDiscoveryAsync()
    {
        try
        {
            await started.Task.ConfigureAwait(false);
            options.WriteDiscoveryFile();
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not publish the frontend discovery file; stopping the host.");
            lifetime.StopApplication();
        }
    }
}
