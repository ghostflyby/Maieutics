using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Jupyter;
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
        "/v1/status",
        "/v1/objects"
    };

    private readonly FrontendOptions options;
    private readonly FrontendSessionService service;
    private readonly ObjectStore? objectStore;
    private readonly ILogger<FrontendHost> logger;
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] expectedToken;
    private int disposeState;

    public FrontendHost(
        FrontendOptions options,
        FrontendSessionService service,
        ILogger<FrontendHost> logger,
        ObjectStore? objectStore = null)
    {
        this.options = options;
        this.service = service;
        this.logger = logger;
        this.objectStore = objectStore;
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
        endpoints.MapPost("/v1/agent/sessions/{sessionId}/gc", HandleGcSession);
        endpoints.MapPost("/v1/agent/sessions/{sessionId}/repair", HandleRepairSession);
        endpoints.MapPost("/v1/agent/sessions/{sessionId}/turns", HandleTurn);
        endpoints.MapGet("/v1/agent/sessions/{sessionId}/transcript", HandleTranscript);
        endpoints.MapGet("/v1/agent/sessions/{sessionId}/events", HandleEvents);
        endpoints.MapPost("/v1/agent/runs/{runId}/cancel", HandleCancel);
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

        // The events WebSocket cannot carry headers (browser-standard client API), so the
        // token may arrive as a query parameter on that endpoint only.
        var isEventsPath = context.Request.Path.StartsWithSegments("/v1/agent/sessions", StringComparison.Ordinal) &&
                           context.Request.Path.Value?.EndsWith("/events", StringComparison.Ordinal) == true;
        if (!TryGetBearerToken(context, out var provided) && !(isEventsPath &&
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
            session), FrontendJsonContext.Default.FrontendCapabilities);
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
            // A command cell executes inline and answers with markdown, mirroring the Jupyter
            // adapter so the same cell text behaves identically on both frontends.
            if (MaieuticsCommandLanguage.IsCommandCell(request.Text))
            {
                var markdown = await service.ExecuteCommandAsync(request.Text, context.RequestAborted)
                    .ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsJsonAsync(
                    new FrontendCommandResponse(markdown),
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
            var markdown = await service.ExecuteCommandAsync(request.Text, context.RequestAborted)
                .ConfigureAwait(false);
            await context.Response.WriteAsJsonAsync(
                new FrontendCommandResponse(markdown),
                FrontendJsonContext.Default.FrontendCommandResponse).ConfigureAwait(false);
        }
        catch (MaieuticsCommandException exception)
        {
            await WriteErrorAsync(context, FrontendErrors.CommandError, exception.Message);
        }
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
        var activeSessionId = service.DescribeSession().Id;
        if (!string.Equals(sessionId, activeSessionId, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                new FrontendError(FrontendErrors.SessionNotActive,
                    "Events are served for the active session."),
                FrontendJsonContext.Default.FrontendError).ConfigureAwait(false);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var peer = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            lifetime.Token);
        await SendFrameAsync(
            socket,
            new FrontendEventFrame("hello", Session: service.DescribeSession(), Replayed: since > 0),
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
                        .WaitForRunAsync(new AgentSessionId(Guid.ParseExact(activeSessionId, "N")), previous, peer.Token)
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
