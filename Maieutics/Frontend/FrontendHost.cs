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
    private readonly FrontendTurnQueue turnQueue;
    private readonly ObjectStore? objectStore;
    private readonly FrontendCommRouter? commRouter;
    private readonly ILogger<FrontendHost> logger;
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] expectedToken;
    private int disposeState;

    public FrontendHost(
        FrontendOptions options,
        FrontendSessionService service,
        FrontendTurnQueue turnQueue,
        ILogger<FrontendHost> logger,
        ObjectStore? objectStore = null,
        FrontendCommRouter? commRouter = null)
    {
        this.options = options;
        this.service = service;
        this.turnQueue = turnQueue;
        this.logger = logger;
        this.objectStore = objectStore;
        this.commRouter = commRouter;
        expectedToken = Encoding.UTF8.GetBytes(options.Token);
    }

    /// <summary>Terminates every WebSocket so Kestrel shutdown does not wait on upgrades.
    /// The host-lifetime callback is synchronous, so the cancellation request is observed
    /// here instead of awaited by the caller.</summary>
    internal void BeginShutdown()
    {
        _ = ObserveShutdownCancelAsync();
    }

    private async Task ObserveShutdownCancelAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The frontend host shutdown cancellation faulted.");
        }
    }

    /// <summary>Maps the frontend middleware and endpoints. Call before the control bus maps
    /// its own middleware so frontend requests never reach the peer-identity gate.</summary>
    internal void MapEndpoints(WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var endpoints = (IEndpointRouteBuilder)application;
        // Unhandled endpoint exceptions must be loud: without this the request fails
        // with a bare 500 and no server-side trace (Kestrel does not log these for
        // in-process minimal APIs), which makes integration failures undiagnosable.
        application.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Unhandled exception on {Method} {Path}.",
                    context.Request.Method,
                    context.Request.Path);
                throw;
            }
        });
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
        endpoints.MapGet("/v1/agent/sessions/{sessionId}/queue", HandleGetQueue);
        endpoints.MapPost("/v1/agent/sessions/{sessionId}/queue", HandleEnqueueTurns);
        endpoints.MapDelete("/v1/agent/sessions/{sessionId}/queue/{itemId}", HandleDeleteQueuedItem);
        endpoints.MapDelete("/v1/agent/sessions/{sessionId}/queue", HandleClearQueue);
        endpoints.MapGet("/v1/agent/sessions/{sessionId}/transcript", HandleTranscript);
        endpoints.MapGet("/v1/agent/sessions/{sessionId}/events", HandleEvents);
        endpoints.MapGet("/v1/agent/sessions/{sessionId}/comms", HandleComms);
        endpoints.MapPost("/v1/agent/runs/{runId}/cancel", HandleCancel);
        endpoints.MapPost("/v1/agent/inputs/{requestId}", HandleInput);
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

            var accepted = await service
                .StartTurnAsync(sessionId, request.Text, context.RequestAborted)
                .ConfigureAwait(false);
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

    /// <summary>Serves the session's server-side turn queue snapshot (ADR 0025): the
    /// running item with its run id plus the pending items in run order, with texts.</summary>
    private Task HandleGetQueue(HttpContext context, string sessionId)
    {
        return GuardAsync(context, () => Task.FromResult(
            Results.Json(QueueSnapshot(sessionId), FrontendJsonContext.Default.FrontendQueueSnapshot)));
    }

    /// <summary>Enqueues turn texts (1..64, all or nothing) behind the session's current
    /// work. Command cells stay client-orchestrated on the turn endpoint and are rejected
    /// here with a typed 400. The acceptance is a 202 with the assigned ids and positions,
    /// mirroring the turn endpoint's status shape.</summary>
    private async Task HandleEnqueueTurns(HttpContext context, string sessionId)
    {
        var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendQueueEnqueueRequest)
            .ConfigureAwait(false);
        if (request is null) return;

        FrontendQueueEnqueueResponse response;
        try
        {
            var texts = request.Items is null ? [] : request.Items.Select(item => item.Text).ToArray();
            response = turnQueue.Enqueue(sessionId, texts);
        }
        catch (FrontendFailureException exception)
        {
            await WriteErrorAsync(context, exception.Code, exception.Message).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        await context.Response
            .WriteAsJsonAsync(response, FrontendJsonContext.Default.FrontendQueueEnqueueResponse)
            .ConfigureAwait(false);
    }

    /// <summary>Removes one queued item; the running item is a typed 409 because clients
    /// cancel the run itself instead.</summary>
    private Task HandleDeleteQueuedItem(HttpContext context, string sessionId, string itemId)
    {
        return GuardAsync(context, () =>
        {
            turnQueue.Remove(sessionId, itemId);
            return Task.FromResult<IResult>(Results.NoContent());
        });
    }

    /// <summary>Clears every queued item; the running item is untouched and completes.</summary>
    private Task HandleClearQueue(HttpContext context, string sessionId)
    {
        return GuardAsync(context, () =>
        {
            turnQueue.Clear(sessionId);
            return Task.FromResult<IResult>(Results.NoContent());
        });
    }

    /// <summary>Resolves the session for a queue route (typed 404 / invalid id) and builds
    /// the full-state snapshot with texts.</summary>
    private FrontendQueueSnapshot QueueSnapshot(string sessionId)
    {
        service.EnsureSessionResolved(sessionId);
        return turnQueue.Snapshot(sessionId);
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

    /// <summary>Delivers a REPL stdin answer announced by an <c>input.request</c> frame.
    /// A request that is no longer pending (already answered, run ended, or unknown id)
    /// is a typed 404, matching docs/web-frontend-protocol.md.</summary>
    private async Task HandleInput(HttpContext context, string requestId)
    {
        var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendInputAnswer)
            .ConfigureAwait(false);
        if (request is null) return;

        // The source-generated binder accepts JSON null even for the non-nullable
        // property; an empty string is the documented dismiss answer, null is not.
        if (request.Value is null)
        {
            await WriteErrorAsync(context, FrontendErrors.InvalidRequest, "The input answer must carry a value.");
            return;
        }

        if (!service.TryCompleteInput(requestId, request.Value))
        {
            await WriteErrorAsync(
                context,
                FrontendErrors.NotFound,
                $"No pending input request matches '{requestId}'.");
            return;
        }

        await context.Response.WriteAsJsonAsync(
            new Dictionary<string, object?>(),
            FrontendJsonContext.Default.DictionaryStringObject).ConfigureAwait(false);
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
        // Object ids are content addresses in canonical lowercase hex; the store's paths are
        // case-sensitive, so normalize here instead of failing with an untyped 500 on the
        // uppercase form the route happily accepts.
        objectId = objectId.ToLowerInvariant();
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
                var queueVersion = 0L;
                while (!peer.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    // One wait race per loop iteration: a run wake serves that run exactly
                    // once (queue wakes never touch the `previous` tracking, so a run can
                    // neither be missed nor double-served); a queue wake writes one
                    // full-state queue.updated frame straight on the socket. The queue
                    // wait is raced first so a socket that opens while queue state exists
                    // receives the current state immediately after hello, even when a run
                    // announcement is also pending.
                    var runWait = service.WaitForRunAsync(new AgentSessionId(addressedSession), previous, peer.Token);
                    var queueWait = turnQueue.WaitForChangeAsync(sessionId, queueVersion, peer.Token);
                    var settled = await Task.WhenAny(queueWait, runWait).ConfigureAwait(false);
                    if (ReferenceEquals(settled, runWait))
                    {
                        var stream = await runWait.ConfigureAwait(false);
                        await ServeStreamAsync(socket, stream, since, peer.Token).ConfigureAwait(false);
                        previous = stream;
                        // Replay offsets are per run; later runs stream live from their start.
                        since = 0;
                    }
                    else
                    {
                        // The run wait stays pending across queue wakes; the announcements
                        // channel it reads is never completed, so it cannot fault — but a
                        // late fault or cancellation must still be observed (WhenAny leaves
                        // the loser unobserved).
                        _ = runWait.ContinueWith(
                            static abandoned => _ = abandoned.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);

                        (var frameSnapshot, queueVersion) = turnQueue.FrameSnapshot(sessionId);
                        if (socket.State == WebSocketState.Open)
                            await SendFrameAsync(
                                socket,
                                new FrontendEventFrame("queue.updated", Queue: frameSnapshot),
                                peer.Token).ConfigureAwait(false);
                    }
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
                await CloseSocketOutputAsync(
                    socket,
                    WebSocketCloseStatus.NormalClosure,
                    "stream ended").ConfigureAwait(false);
        }
    }

    /// <summary>Sends the close frame with a bounded write: the events endpoint closes
    /// send-only (it never waits for the peer's handshake), but a dead socket must not
    /// hold the request's teardown open either.</summary>
    private static async Task CloseSocketOutputAsync(
        WebSocket socket,
        WebSocketCloseStatus closeStatus,
        string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.CloseOutputAsync(closeStatus, description, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when
            (exception is OperationCanceledException or WebSocketException or InvalidOperationException)
        {
        }
    }

    /// <summary>Closes a frontend WebSocket with a bounded handshake: a vanished or
    /// suspended peer can stall the close for a TCP-retransmit scale of time, and the
    /// wait must never consume the host's shutdown budget.</summary>
    private static async Task CloseSocketAsync(
        WebSocket socket,
        WebSocketCloseStatus closeStatus,
        string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.CloseAsync(closeStatus, description, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when
            (exception is OperationCanceledException or WebSocketException or InvalidOperationException)
        {
            // The peer vanished, the close lost the race with a concurrent close, or
            // the handshake overran its budget; the request ends either way.
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
                await CloseSocketAsync(
                    socket,
                    (WebSocketCloseStatus)1011,
                    "backpressure").ConfigureAwait(false);
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
            // The close frame write is bounded like the events socket close: a dead
            // peer must not hold the connection's teardown open.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await SendAsync(() => socket.CloseOutputAsync(status, description, timeout.Token))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or WebSocketException or InvalidOperationException)
            {
            }
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
                    await send(() => CloseSocketOutputAsync(
                        socket,
                        WebSocketCloseStatus.InvalidMessageType,
                        "comm frame is malformed")).ConfigureAwait(false);
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
                    await send(() => CloseSocketOutputAsync(
                        socket,
                        WebSocketCloseStatus.PolicyViolation,
                        reject.Message)).ConfigureAwait(false);
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
                or FrontendErrors.QueueFull or FrontendErrors.ItemRunning
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
