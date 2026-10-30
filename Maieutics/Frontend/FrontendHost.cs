using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.DenoRepl;
using Maieutics.Mcp;
using Maieutics.Persistence;
using Maieutics.Skills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics.Frontend;

/// <summary>
///     Maps the frontend web API onto the shared application host: a bearer-token gate that
///     follows the route table (every endpoint the frontend route group mounts carries route
///     metadata the middleware authenticates; the child-process control bus keeps its own
///     peer-identity middleware), REST endpoints for sessions/turns/commands, and the
///     per-session events WebSocket. The events endpoint is half-duplex (executable to
///     frontend only); the frontend reconnects with <c>sinceSequence</c> after any
///     disconnect, including the backpressure disconnect the run stream performs instead of
///     dropping frames.
/// </summary>
internal sealed class FrontendHost : IAsyncDisposable
{
    /// <summary>The native view-family capability version advertised in capabilities (ADR 0038).</summary>
    internal const int FrontendUiCapabilityVersion = 1;

    private readonly FrontendSessionService _service;
    private readonly FrontendElicitationPresenter _elicitationPresenter;
    private readonly FrontendToolApprovalPresenter? _toolApprovalPresenter;
    private readonly FrontendTurnQueue _turnQueue;
    private readonly ObjectStore? _objectStore;
    private readonly FrontendCommRouter? _commRouter;

    /// <summary>The live comm router, so tests can observe a session plane's live-set
    /// transitions through its completion signal instead of probing with hello reads.</summary>
    internal FrontendCommRouter? CommRouter => _commRouter;

    private readonly IFrontendPluginSurface? _pluginSurface;
    private readonly SkillCatalog? _skillCatalog;
    private readonly Func<IReadOnlyList<McpPromptServerAccess>>? _promptServersAccessor;
    private readonly IdeRequestRegistry? _ideRequests;
    private readonly FrontendIdeSockets? _ideSockets;
    private readonly ILogger<FrontendHost> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly byte[] _expectedToken;
    private int _disposeState;

    public FrontendHost(
        FrontendOptions options,
        FrontendSessionService service,
        FrontendTurnQueue turnQueue,
        ILogger<FrontendHost> logger,
        FrontendElicitationPresenter? elicitationPresenter = null,
        ObjectStore? objectStore = null,
        FrontendCommRouter? commRouter = null,
        IFrontendPluginSurface? pluginSurface = null,
        SkillCatalog? skillCatalog = null,
        Func<IReadOnlyList<McpPromptServerAccess>>? promptServersAccessor = null,
        IdeRequestRegistry? ideRequests = null,
        FrontendIdeSockets? ideSockets = null,
        FrontendToolApprovalPresenter? toolApprovalPresenter = null)
    {
        _service = service;
        _turnQueue = turnQueue;
        _logger = logger;
        _ideRequests = ideRequests;
        _ideSockets = ideSockets;
        _elicitationPresenter = elicitationPresenter
                                     ?? new FrontendElicitationPresenter(
                                         NoFramesPublisher.Instance,
                                         new FrontendFormModelHost(
                                             commRouter ?? new FrontendCommRouter((_, _, _) => ValueTask.CompletedTask),
                                             NoFramesPublisher.Instance));
        _toolApprovalPresenter = toolApprovalPresenter;
        _objectStore = objectStore;
        _commRouter = commRouter;
        _pluginSurface = pluginSurface;
        _skillCatalog = skillCatalog;
        _promptServersAccessor = promptServersAccessor;
        _expectedToken = Encoding.UTF8.GetBytes(options.Token);
    }

    /// <summary>Requests cancellation of every retained run (so shutdown does not wait on a
    /// provider stream) and terminates every WebSocket so Kestrel shutdown does not wait on
    /// upgrades. The host-lifetime callback is synchronous, so the cancellation requests are
    /// observed by their owners instead of awaited by the caller.</summary>
    internal void BeginShutdown()
    {
        _service.BeginShutdown();
        _ = ObserveShutdownCancelAsync();
    }

    private async Task ObserveShutdownCancelAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The frontend host shutdown cancellation faulted.");
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
                _logger.LogError(
                    exception,
                    "Unhandled exception on {Method} {Path}.",
                    context.Request.Method,
                    context.Request.Path);
                throw;
            }
        });
        application.Use(AuthorizeThenNextAsync);
        // The frontend's routes are mounted through one group and the group marks every
        // endpoint it registers with FrontendRouteMetadata; the bearer middleware
        // authenticates exactly the marked endpoints, so coverage follows the route
        // table — a route added to this group is gated by construction, with no second
        // prefix list to keep in sync (the previous list had drifted from these routes
        // and left /v1/skills, /v1/prompts, and /v1/plugins unauthenticated). The empty
        // group prefix keeps the mapped patterns identical to the documented wire paths.
        var frontend = endpoints.MapGroup(string.Empty);
        frontend.WithMetadata(FrontendRouteMetadata.Instance);
        frontend.MapGet("/v1/agent/capabilities", HandleCapabilities);
        frontend.MapGet("/v1/skills", HandleSkills);
        frontend.MapGet("/v1/prompts", HandlePrompts);
        frontend.MapGet("/v1/agent/session", HandleSession);
        frontend.MapPost("/v1/agent/sessions", HandleNewSession);
        frontend.MapGet("/v1/agent/sessions", HandleListSessions);
        frontend.MapGet("/v1/agent/sessions/{sessionId}", (HttpContext context, string sessionId) =>
            GuardAsync(context, () => Task.FromResult(Results.Json(
                _service.DescribeSession(sessionId), FrontendJsonContext.Default.FrontendSessionInfo))));
        frontend.MapPost("/v1/agent/sessions/{sessionId}/resume", HandleResumeSession);
        frontend.MapPost("/v1/agent/sessions/{sessionId}/rename", HandleRenameSession);
        frontend.MapPost("/v1/agent/sessions/{sessionId}/title-policy", async (HttpContext context, string sessionId) =>
        {
            var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendTitlePolicyRequest);
            if (request is null) return;
            await GuardAsync(context, async () => Results.Json(
                await _service.ChangeTitlePolicyAsync(sessionId, request, context.RequestAborted),
                FrontendJsonContext.Default.FrontendSessionInfo));
        });
        frontend.MapPost("/v1/agent/sessions/{sessionId}/fork", HandleForkSession);
        frontend.MapPost("/v1/agent/sessions/{sessionId}/gc", HandleGcSession);
        frontend.MapPost("/v1/agent/sessions/{sessionId}/repair", HandleRepairSession);
        frontend.MapPost("/v1/agent/sessions/{sessionId}/turns", HandleTurn);
        frontend.MapPost("/v1/agent/sessions/{sessionId}/objects", HandleIngestObject);
        frontend.MapGet("/v1/agent/sessions/{sessionId}/queue", HandleGetQueue);
        frontend.MapPost("/v1/agent/sessions/{sessionId}/queue", HandleEnqueueTurns);
        frontend.MapDelete("/v1/agent/sessions/{sessionId}/queue/{itemId}", HandleDeleteQueuedItem);
        frontend.MapDelete("/v1/agent/sessions/{sessionId}/queue", HandleClearQueue);
        frontend.MapGet("/v1/agent/sessions/{sessionId}/transcript", HandleTranscript);
        frontend.MapGet("/v1/agent/sessions/{sessionId}/events", HandleEvents)
            .WithMetadata(FrontendQueryTokenMetadata.Instance);
        frontend.MapGet("/v1/agent/sessions/{sessionId}/comms", HandleComms)
            .WithMetadata(FrontendQueryTokenMetadata.Instance);
        frontend.MapPost("/v1/agent/runs/{runId}/cancel", HandleCancel);
        frontend.MapPost("/v1/agent/inputs/{requestId}", HandleInput);
        frontend.MapPost("/v1/agent/ide/{requestId}", HandleIdeAnswer);
        frontend.MapGet("/v1/model/profiles", HandleModelProfiles);
        frontend.MapPost("/v1/agent/commands", HandleCommand);
        frontend.MapPost("/v1/agent/complete", HandleComplete);
        frontend.MapGet("/v1/status", HandleStatus);
        frontend.MapGet("/v1/plugins", HandlePlugins);
        frontend.MapPost("/v1/plugins/{pluginId}/form", HandlePublishPluginForm);
        frontend.MapGet("/v1/objects/{objectId}", HandleObject);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0) await _lifetime.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>Endpoint metadata marking an endpoint as part of the frontend web API plane:
    /// the bearer middleware authenticates exactly the endpoints carrying this marker, so
    /// coverage follows the route table instead of a hand-maintained prefix list (which had
    /// drifted from the mapped routes and left some of them unauthenticated).</summary>
    private sealed record FrontendRouteMetadata
    {
        public static readonly FrontendRouteMetadata Instance = new();

        private FrontendRouteMetadata()
        {
        }
    }

    /// <summary>Endpoint metadata marking the frontend endpoints whose token may also arrive
    /// as a query parameter (the browser-standard WebSocket client API cannot set headers).</summary>
    private sealed record FrontendQueryTokenMetadata
    {
        public static readonly FrontendQueryTokenMetadata Instance = new();

        private FrontendQueryTokenMetadata()
        {
        }
    }

    private async Task AuthorizeThenNextAsync(HttpContext context, Func<Task> next)
    {
        // The gate is route-table driven: only the endpoints the frontend group mounted
        // carry FrontendRouteMetadata. Endpoint routing matches paths case-insensitively,
        // so a variant spelling such as /V1/agent/session resolves to the same marked
        // endpoint and is authenticated exactly like its canonical form — there is no
        // path comparison left to mis-case.
        if (context.GetEndpoint() is not { } endpoint ||
            endpoint.Metadata.GetMetadata<FrontendRouteMetadata>() is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // The browser-standard WebSocket client API cannot carry headers, so the token may
        // arrive as a query parameter on the endpoints that opted in via metadata.
        var acceptsQueryToken = endpoint.Metadata.GetMetadata<FrontendQueryTokenMetadata>() is not null;
        if (!TryGetBearerToken(context, out var provided) && !(acceptsQueryToken &&
                                                               TryGetQueryToken(context, out provided)) ||
            !CryptographicOperations.FixedTimeEquals(_expectedToken, provided))
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

    /// <summary>Plugins surface (ADR 0038 stage 4): every discovered plugin with its
    /// approval state, declarative form template, and approved gateway page URL. The
    /// entrance token is embedded only in approved plugins' page links.</summary>
    private IResult HandlePlugins()
    {
        if (_pluginSurface is null)
        {
            return Results.Json(
                new FrontendPluginsResponse([]),
                FrontendJsonContext.Default.FrontendPluginsResponse);
        }

        var surfaces = _pluginSurface.GetPluginSurfaces();
        var plugins = surfaces.Select(surface => new FrontendPluginInfo(
            surface.Id,
            surface.Name,
            surface.ApprovalState,
            surface.Form is null
                ? null
                : new FrontendUiFormState(
                    surface.Form.Title,
                    surface.Form.Fields,
                    surface.Form.SubmitLabel,
                    surface.Form.CancelLabel),
            surface.FormError,
            surface.PageUrl)).ToArray();
        return Results.Json(
            new FrontendPluginsResponse(plugins),
            FrontendJsonContext.Default.FrontendPluginsResponse);
    }

    /// <summary>Publishes one plugin's declarative form into the live session's comm
    /// plane (ADR 0038 stage 4): the on-demand trigger for the data-form lane.
    /// Approval is enforced inside the manager's sink path; failures are typed.</summary>
    private async Task<IResult> HandlePublishPluginForm(string pluginId, CancellationToken cancellationToken)
    {
        if (_pluginSurface is null)
        {
            return Results.Json(
                new FrontendError("plugins_unavailable", "The plugin host is not available."),
                FrontendJsonContext.Default.FrontendError,
                statusCode: 404);
        }

        try
        {
            await _pluginSurface.PublishUiFormAsync(pluginId, cancellationToken).ConfigureAwait(false);
            return Results.Json(
                new FrontendPluginFormPublished(pluginId, "*"),
                FrontendJsonContext.Default.FrontendPluginFormPublished);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Json(
                new FrontendError("plugin_form_unavailable", exception.Message),
                FrontendJsonContext.Default.FrontendError,
                statusCode: 409);
        }
    }

    /// <summary>Serves the live skill catalog for frontend completion and pickers: active
    /// skills only (name, description, source), mirroring what the prompt section carries.
    /// A null catalog (no skills host) answers an empty list rather than an error, so a
    /// frontend built for skills degrades to "no skills offered" on hosts without them.</summary>
    private IResult HandleSkills()
    {
        var snapshot = _skillCatalog?.Current ?? SkillCatalogSnapshot.Empty;
        var skills = snapshot.Skills
            .Select(static skill => new FrontendSkillInfo(
                skill.Name,
                skill.Description,
                skill.Source.ToString()))
            .ToArray();
        return Results.Json(
            new FrontendSkillList(skills, snapshot.Diagnostics.Length),
            FrontendJsonContext.Default.FrontendSkillList);
    }

    /// <summary>Serves the live MCP prompt catalog for frontend completion and pickers
    /// (ADR 0041): every prompt of every live server generation, with the server's
    /// connection state and the referencability marking (both segments' charset). A
    /// host without MCP servers answers an empty list.</summary>
    private IResult HandlePrompts()
    {
        var prompts = (_promptServersAccessor?.Invoke() ?? [])
            .SelectMany(static server => server.Catalog.Prompts.Select(prompt => (server, prompt)))
            .Select(static entry =>
            {
                var (server, prompt) = entry;
                return new FrontendPromptInfo(
                    server.Id,
                    server.Generation.GetInfo().State.ToString(),
                    prompt.Name,
                    prompt.Title,
                    prompt.Description,
                    prompt.Arguments
                        .Select(static argument => new FrontendPromptArgument(
                            argument.Name,
                            argument.Description,
                            argument.Required))
                        .ToArray(),
                    McpPromptReferenceGrammar.IsValidServerIdSegment(server.Id) &&
                    McpPromptReferenceGrammar.IsValidPromptName(prompt.Name));
            })
            .ToArray();
        return Results.Json(
            new FrontendPromptList(prompts),
            FrontendJsonContext.Default.FrontendPromptList);
    }

    private IResult HandleCapabilities()
    {
        var session = _service.DescribeSession();
        return Results.Json(new FrontendCapabilities(
            FrontendProtocol.Version,
            typeof(FrontendHost).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            session,
            _service.WorkspaceRoot,
            _commRouter is null
                ? null
                : new FrontendCommCapability(
                    FrontendCommRouter.Version,
                    ReplCommLimits.MaximumMessageBytes),
            Ui: _commRouter is null
                ? null
                : new FrontendUiCapability(
                    FrontendUiCapabilityVersion,
                    [FrontendUiWire.FormFamily]),
            MultiSession: true), FrontendJsonContext.Default.FrontendCapabilities);
    }

    private IResult HandleSession()
    {
        return Results.Json(_service.DescribeSession(), FrontendJsonContext.Default.FrontendSessionInfo);
    }

    private IResult HandleNewSession()
    {
        return Results.Json(_service.StartNew(), FrontendJsonContext.Default.FrontendSessionInfo);
    }

    private IResult HandleListSessions()
    {
        return Results.Json(_service.ListStoredSessions(), FrontendJsonContext.Default.FrontendStoredSessionArray);
    }

    private async Task HandleResumeSession(HttpContext context, string sessionId)
    {
        await GuardAsync(context, () => Task.FromResult(
            Results.Json(_service.Resume(sessionId), FrontendJsonContext.Default.FrontendSessionInfo)));
    }

    private async Task HandleRenameSession(HttpContext context, string sessionId)
    {
        var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendRenameRequest)
            .ConfigureAwait(false);
        if (request is null) return;

        await GuardAsync(context, () => Task.FromResult(
            Results.Json(_service.Rename(sessionId, request.Title), FrontendJsonContext.Default.FrontendRenameResponse)));
    }

    private async Task HandleForkSession(HttpContext context, string sessionId)
    {
        var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendForkRequest)
            .ConfigureAwait(false);
        if (request is null) return;

        await GuardAsync(context, async () => Results.Json(
            await _service.ForkAsync(sessionId, request, context.RequestAborted).ConfigureAwait(false),
            FrontendJsonContext.Default.FrontendForkResponse));
    }

    private async Task HandleModelProfiles(HttpContext context)
    {
        await GuardAsync(context, () => Task.FromResult(
            Results.Json(_service.ListModelProfiles(), FrontendJsonContext.Default.FrontendModelProfileArray)));
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

        await GuardAsync(context, () => Task.FromResult(
            Results.Json(new FrontendCommandResponse(
                    $"**GC** removed {_service.PruneObjects(sessionId, graceHours)} unreferenced object(s) (grace {graceHours} h)."),
                FrontendJsonContext.Default.FrontendCommandResponse)));
    }

    private async Task HandleRepairSession(HttpContext context, string sessionId)
    {
        await GuardAsync(context, () => Task.FromResult(
            Results.Json(new FrontendCommandResponse(
                    $"**View** ensured {_service.RepairObjectView(sessionId)} object link(s) under view/sessions."),
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
                var (markdown, movedForeground) = await _service
                    .ExecuteCommandAsync(request.Text, addressed, context.RequestAborted)
                    .ConfigureAwait(false);
                // The addressed session unless THIS command moved the foreground.
                var answerSession = movedForeground
                    ? _service.DescribeSession().Id
                    : addressed.Value.ToString("N");
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsJsonAsync(
                    new FrontendCommandResponse(markdown, answerSession),
                    FrontendJsonContext.Default.FrontendCommandResponse).ConfigureAwait(false);
                return;
            }

            var accepted = await _service
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

    /// <summary>Ingests attachment bytes (raw body, never base64) into the session's object
    /// library and answers the content address the marker grammar references. The session
    /// must exist; the ingest bound is enforced server-side with a typed 400 rather than a
    /// transport-level abort.</summary>
    private async Task HandleIngestObject(HttpContext context, string sessionId)
    {
        try
        {
            if (context.Request.ContentLength is { } declared && declared > _service.MaxIngestBytes)
                throw Oversized();

            // Kestrel's own body cap would end the request as a bare 413 before the typed
            // failure can be written; the bounded stream below enforces the bound instead.
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>()
                is { } bodySize)
                bodySize.MaxRequestBodySize = null;

            await using var bounded = new BoundedBodyStream(context.Request.Body, _service.MaxIngestBytes, Oversized());
            // Kestrel forbids synchronous reads on the request body while the object store's
            // ingest is a synchronous hash-and-write pass; the bounded async copy bridges the
            // two. The copy is capped by the same bound, so the buffer is bounded too.
            using var buffered = new MemoryStream(
                (int)Math.Min(context.Request.ContentLength ?? 0, _service.MaxIngestBytes));
            await bounded.CopyToAsync(buffered, context.RequestAborted).ConfigureAwait(false);
            buffered.Position = 0;
            var uploaded = _service.IngestObject(sessionId, buffered);
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(
                uploaded,
                FrontendJsonContext.Default.FrontendObjectUploaded).ConfigureAwait(false);
        }
        catch (FrontendFailureException exception)
        {
            await WriteErrorAsync(context, exception.Code, exception.Message);
        }
    }

    private static FrontendFailureException Oversized()
    {
        return new FrontendFailureException(
            FrontendErrors.InvalidRequest,
            "The object payload exceeds the ingest bound; store large payloads out of band and reference them by path.");
    }

    /// <summary>Caps a request body at the ingest bound: the first read past the bound throws
    /// the typed oversized-payload failure instead of letting a chunked body grow unchecked.</summary>
    private sealed class BoundedBodyStream(Stream inner, long bound, FrontendFailureException over) : Stream
    {
        private long _remaining = bound;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                _remaining -= read;
                if (_remaining < 0) throw over;
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _remaining -= read;
                if (_remaining < 0) throw over;
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private async Task HandleTranscript(HttpContext context, string sessionId)
    {
        await GuardAsync(context, () => Task.FromResult(
            Results.Json(_service.GetTranscript(sessionId), FrontendJsonContext.Default.FrontendTranscript)));
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
            response = _turnQueue.Enqueue(sessionId, texts);
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
            _turnQueue.Remove(sessionId, itemId);
            return Task.FromResult(Results.NoContent());
        });
    }

    /// <summary>Clears every queued item; the running item is untouched and completes.</summary>
    private Task HandleClearQueue(HttpContext context, string sessionId)
    {
        return GuardAsync(context, () =>
        {
            _turnQueue.Clear(sessionId);
            return Task.FromResult(Results.NoContent());
        });
    }

    /// <summary>Resolves the session for a queue route (typed 404 / invalid id) and builds
    /// the full-state snapshot with texts.</summary>
    private FrontendQueueSnapshot QueueSnapshot(string sessionId)
    {
        _service.EnsureSessionResolved(sessionId);
        return _turnQueue.Snapshot(sessionId);
    }

    private async Task HandleCancel(HttpContext context, string runId)
    {
        await GuardAsync(context, async () =>
        {
            await _service.CancelRunAsync(runId, context.RequestAborted).ConfigureAwait(false);
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
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (request.Value is null)
        {
            await WriteErrorAsync(context, FrontendErrors.InvalidRequest, "The input answer must carry a value.");
            return;
        }

        // Three consumers share one endpoint. The request-id prefix picks the owner before any
        // lookup, so an id can never cross-wire to the wrong registry; an unmatched id is the
        // documented 404.
        var completed = FrontendToolApprovalPresenter.OwnsRequestId(requestId)
            ? _toolApprovalPresenter?.TryCompleteInput(requestId, request.Action, request.Value) == true
            : request.Action is not null
                ? _elicitationPresenter.TryCompleteInput(requestId, request.Action, request.Value)
                : _service.TryCompleteInput(requestId, request.Value);
        if (!completed)
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

    /// <summary>Delivers an IDE request answer announced by an <c>ide.request</c> payload
    /// (ADR 0042 decision 1): the body carries exactly one of the request's value or its
    /// typed error. A request that is no longer pending — already answered, abandoned by a
    /// timed-out or cancelled tool call, replayed by a reconnecting frontend, or unknown —
    /// is a typed 404 that the answering frontend absorbs silently.</summary>
    private async Task HandleIdeAnswer(HttpContext context, string requestId)
    {
        var request = await ReadJsonAsync(context, FrontendJsonContext.Default.FrontendIdeAnswer)
            .ConfigureAwait(false);
        if (request is null) return;

        if (_ideRequests is null)
        {
            await WriteErrorAsync(
                context,
                FrontendErrors.NotFound,
                "The IDE request surface is not available in this host.");
            return;
        }

        if (request.Value is not null && request.Error is not null)
        {
            await WriteErrorAsync(
                context,
                FrontendErrors.InvalidRequest,
                "The IDE answer must carry exactly one of 'value' or 'error'.");
            return;
        }

        IdeRequestAnswer answer;
        if (request.Error is { } error)
        {
            if (string.IsNullOrWhiteSpace(error.Code) || string.IsNullOrWhiteSpace(error.Message))
            {
                await WriteErrorAsync(
                    context,
                    FrontendErrors.InvalidRequest,
                    "The IDE answer error must carry a code and a message.");
                return;
            }

            answer = IdeRequestAnswer.FromError(error.Code, error.Message);
        }
        else if (request.Value is { } value)
        {
            answer = IdeRequestAnswer.FromValue(value);
        }
        else
        {
            await WriteErrorAsync(
                context,
                FrontendErrors.InvalidRequest,
                "The IDE answer must carry exactly one of 'value' or 'error'.");
            return;
        }

        if (!_ideRequests.TryComplete(requestId, answer))
        {
            await WriteErrorAsync(
                context,
                FrontendErrors.NotFound,
                $"No pending ide request matches '{requestId}'.");
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
            var (markdown, _) = await _service.ExecuteCommandAsync(request.Text, null, context.RequestAborted)
                .ConfigureAwait(false);
            await context.Response.WriteAsJsonAsync(
                new FrontendCommandResponse(markdown, _service.DescribeSession().Id),
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
            var completion = _service.Complete(request);
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
            Results.Json(new FrontendStatusResponse(_service.CaptureStatusMarkdown()),
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

        if (_objectStore is null || !_objectStore.Exists(objectId))
        {
            await WriteErrorAsync(context, FrontendErrors.NotFound, $"No object matches '{objectId}'.");
            return;
        }

        // Content-addressed objects are immutable: unconditional client caching is the
        // contract (same URL = same bytes forever).
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        await Results.Stream(_objectStore.Open(objectId), "application/octet-stream")
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

        // The IDE capability announcement rides the upgrade request (ADR 0042 decision 2):
        // the events stream is half-duplex, so a query parameter beside `token` is the only
        // per-socket channel; it is per socket and cleared on disconnect.
        var ideAnnounced =
            context.Request.Query.TryGetValue("ide", out var rawIde) &&
            int.TryParse(rawIde, out var ideVersion) &&
            ideVersion == FrontendIdeSockets.Version;

        var singleRunId = context.Request.Query.TryGetValue("runId", out var rawRun)
            ? rawRun.ToString()
            : null;
        // Events are per session: the addressed session need not be the foreground.
        if (!Guid.TryParseExact(sessionId, "N", out var addressedSession) || addressedSession == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!_service.SessionExists(sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                new FrontendError(FrontendErrors.NotFound, $"No stored session matches '{sessionId}'."),
                FrontendJsonContext.Default.FrontendError).ConfigureAwait(false);
            return;
        }

        FrontendSessionInfo helloSession = _service.DescribeSession(sessionId);

        // The socket, peer source, and send gate below are disposed when this handler returns.
        // Every closure that touches them — the announcer send, the hello and run.missing
        // writes, the queue flush, the frame loop, and the close write — is awaited inside the
        // handler, the announcer is withdrawn in the finally before the drain completes, and
        // only then does the using scope end. The analyzer cannot see that sequencing, so the
        // disposed-closure findings are suppressed for this handler's scope.
        // ReSharper disable AccessToDisposedClosure
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var peer = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            _lifetime.Token);
        // A WebSocket permits a single outstanding send, and this socket has three writers
        // that run on different flows: the run frame stream served below, its interleaved
        // queue flush, and the ide.push delegate the announce registration hands to
        // FrontendIdeSockets (a context-less tool invocation writes through it from
        // another request). Writes are serialized through this gate — the same discipline
        // the comm socket applies — because an unserialized second send faults the run
        // stream (the exception escapes ServeStreamAsync and this handler's catches) and
        // makes FrontendIdeSockets withdraw the announcer as if the socket had died.
        using var sendGate = new SemaphoreSlim(1, 1);
        async Task SendAsync(Func<Task> write, CancellationToken token)
        {
            await sendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await write().ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }

        Guid? ideRegistration = ideAnnounced && _ideSockets is not null
            ? _ideSockets.Announce(sessionId, (frame, token) => SendAsync(() => SendFrameAsync(socket, frame, token), token))
            : null;
        _logger.LogDebug(
            "Events socket opened for session {SessionId} at {Path} (since {SinceSequence}, ide {IdeAnnounced}).",
            sessionId,
            context.Request.Path,
            since,
            ideAnnounced);
        // Copied before the closure: the loop below rewrites `since` for later runs, and the
        // hello frame must pin the replay status of the socket as opened.
        var replayed = since > 0;
        await SendAsync(
            () => SendFrameAsync(
                socket,
                new FrontendEventFrame("hello", Session: helloSession, Replayed: replayed),
                peer.Token),
            peer.Token).ConfigureAwait(false);
        var drain = DrainReceiveAsync(socket, peer.Token);
        using var metadataLifetime = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
        async Task PumpTitleMetadataAsync()
        {
            var revision = helloSession.TitleRevision;
            try
            {
                while (!metadataLifetime.IsCancellationRequested)
                {
                    await _service.WaitForTitleChangeAsync(sessionId, revision, metadataLifetime.Token).ConfigureAwait(false);
                    var metadata = _service.DescribeSession(sessionId);
                    revision = metadata.TitleRevision;
                    await SendAsync(() => SendFrameAsync(socket,
                        new FrontendEventFrame("session.updated", Session: metadata), metadataLifetime.Token), metadataLifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (metadataLifetime.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _logger.LogWarning("Session metadata socket failed for {SessionId} ({FailureType}).", sessionId, exception.GetType().Name);
                await peer.CancelAsync().ConfigureAwait(false);
            }
        }
        var metadataPump = singleRunId is null ? PumpTitleMetadataAsync() : Task.CompletedTask;
        try
        {
            if (singleRunId is not null)
            {
                if (!_service.TryGetRun(singleRunId, out var runStream) || runStream is null)
                {
                    await SendAsync(
                        () => SendFrameAsync(
                            socket,
                            new FrontendEventFrame("run.missing", RunId: singleRunId),
                            peer.Token),
                        peer.Token).ConfigureAwait(false);
                }
                else
                {
                    // A run-scoped socket has no queue wake. Keep that branch pending
                    // until cancellation rather than spinning on a completed no-op task.
                    await ServeStreamAsync(
                        socket,
                        SendAsync,
                        runStream,
                        since,
                        static token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                        peer.Token).ConfigureAwait(false);
                }
            }
            else
            {
                FrontendRunStream? previous = null;
                var queueVersion = 0L;

                // One full-state queue.updated when the version moved past the caller's
                // view; shared by the pre-serve flush and the run-frame interleaving.
                async Task FlushQueueAsync(CancellationToken token)
                {
                    var (frameSnapshot, latestQueueVersion) = _turnQueue.FrameSnapshot(sessionId);
                    if (latestQueueVersion == queueVersion) return;

                    queueVersion = latestQueueVersion;
                    _logger.LogTrace(
                        "Flushing queue.updated for session {SessionId} at version {QueueVersion}.",
                        sessionId,
                        queueVersion);
                    if (socket.State == WebSocketState.Open)
                        await SendAsync(
                            () => SendFrameAsync(
                                socket,
                                new FrontendEventFrame("queue.updated", Queue: frameSnapshot),
                                token),
                            token).ConfigureAwait(false);
                }

                // The pending run wait is carried across loop iterations instead of being
                // recreated each time. The announcements channel hands each item to exactly
                // one waiter, so an abandoned-but-still-pending wait stays queued ahead of a
                // freshly created one and takes the next announcement with it: the run would
                // never be served (a direct POST /turns run has no queue mutation to wake the
                // loop and recover it). One wait exists at a time; it is replaced only after
                // it completes.
                Task<FrontendRunStream>? pendingRunWait = null;
                while (!peer.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    // One wait race per loop iteration: a run wake serves that run exactly
                    // once (queue wakes never touch the `previous` tracking, so a run can
                    // neither be missed nor double-served); a queue wake writes one
                    // full-state queue.updated frame straight on the socket.
                    var runWait = pendingRunWait ??=
                        _service.WaitForRunAsync(new AgentSessionId(addressedSession), previous, peer.Token);
                    var queueWait = _turnQueue.WaitForChangeAsync(sessionId, queueVersion, peer.Token);
                    var settled = await Task.WhenAny(queueWait, runWait).ConfigureAwait(false);

                    // Flush queue state BEFORE serving a run: one worker transition fires
                    // both wakes (dequeue and announce), and clients need the queue frame
                    // to precede the run's own frames so they can map the new run id to
                    // its queued cell before text arrives. Comparing versions — not the
                    // wake winner — makes the order deterministic; a scheduling race here
                    // parked the frame behind a gated run on slow runners.
                    await FlushQueueAsync(peer.Token).ConfigureAwait(false);

                    if (ReferenceEquals(settled, runWait))
                    {
                        // The wait is consumed: the next iteration starts a fresh one against
                        // the run just served.
                        pendingRunWait = null;
                        var stream = await runWait.ConfigureAwait(false);
                        await ServeStreamAsync(
                            socket,
                            SendAsync,
                            stream,
                            since,
                            async token =>
                            {
                                await _turnQueue.WaitForChangeAsync(sessionId, queueVersion, token).ConfigureAwait(false);
                                await FlushQueueAsync(peer.Token).ConfigureAwait(false);
                            },
                            peer.Token).ConfigureAwait(false);
                        previous = stream;
                        // Replay offsets are per run; later runs stream live from their start.
                        since = 0;
                    }
                }

                // The loop ends with a wait possibly still outstanding (client disconnect or
                // host shutdown cancelled it). Observe it so a late fault cannot surface as an
                // unobserved task exception; its stream is not served — the socket is gone.
                DiscardPendingRunWait(pendingRunWait);
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
            await metadataLifetime.CancelAsync().ConfigureAwait(false);
            await metadataPump.ConfigureAwait(false);
            if (ideRegistration is { } registration) _ideSockets?.Withdraw(sessionId, registration);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // Cancelling the pending receive aborts the WebSocket transport.
                    // Send the close frame and let the peer acknowledge it first, under
                    // one budget for the send gate, close write, and receive drain.
                    using var closeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await SendAsync(
                            () => socket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "stream ended",
                                closeBudget.Token),
                            closeBudget.Token).ConfigureAwait(false);
                        await drain.WaitAsync(closeBudget.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when
                        (exception is OperationCanceledException or WebSocketException or InvalidOperationException)
                    {
                    }
                }
            }
            finally
            {
                await peer.CancelAsync().ConfigureAwait(false);
                await drain.ConfigureAwait(false);
                _logger.LogDebug("Events socket for session {SessionId} closed.", sessionId);
            }
        }
        // ReSharper restore AccessToDisposedClosure
    }

    /// <summary>Observes a run wait that was still pending when the events loop ended.
    /// Its run cannot be served (the socket is gone or the host is shutting down), and the
    /// wait is deliberately not awaited: at loop exit the peer token may not be cancelled
    /// yet, and blocking on an announcement that may never arrive would stall the socket's
    /// own teardown. The announcements channel it reads is never completed, so the wait
    /// cannot fault in practice — the continuation only ensures a late fault or cancellation
    /// is observed rather than left as an unobserved task exception.</summary>
    private static void DiscardPendingRunWait(Task<FrontendRunStream>? pendingRunWait)
    {
        if (pendingRunWait is null) return;

        _ = pendingRunWait.ContinueWith(
            static abandoned => _ = abandoned.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        Func<Func<Task>, CancellationToken, Task> send,
        FrontendRunStream stream,
        long since,
        Func<CancellationToken, Task> flushQueueAsync,
        CancellationToken cancellationToken)
    {
        var (initial, channel) = stream.Subscribe(since);
        // The run owns its pending waits independently of the socket receive: ending
        // replay must stop and join these waits without aborting the WebSocket.
        using var waits = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queueWake = Task.CompletedTask;
        var nextFrame = Task.FromResult(false);
        try
        {
            foreach (var frame in initial) await send(() => SendFrameAsync(socket, frame, cancellationToken), cancellationToken).ConfigureAwait(false);

            // Queue frames must interleave with run frames, not defer behind the whole
            // run: a run parked at a gated provider once pinned a worker transition
            // (running) behind the park for the run's entire lifetime, and a client
            // gating on that frame starved with it. One WaitToReadAsync in flight,
            // raced against the queue wake, flushes every mutation's frame between
            // run frames instead of within one whole run. Exactly one queue wake is
            // armed at a time; when the run frame wins the race, the wake stays armed
            // for the next race (WaitForChangeAsync wakes are broadcasts, so the
            // armed task neither steals nor loses anything).
            queueWake = flushQueueAsync(waits.Token);
            nextFrame = channel.Reader.WaitToReadAsync(waits.Token).AsTask();
            while (true)
            {
                var settled = await Task.WhenAny(nextFrame, queueWake).ConfigureAwait(false);
                if (ReferenceEquals(settled, queueWake))
                {
                    await queueWake.ConfigureAwait(false);
                    queueWake = flushQueueAsync(waits.Token);
                    continue;
                }

                if (!await nextFrame.ConfigureAwait(false)) break;

                nextFrame = channel.Reader.WaitToReadAsync(waits.Token).AsTask();
                while (channel.Reader.TryRead(out var frame))
                {
                    // Copied per iteration: `frame` is one variable across the loop, and the
                    // awaited send must pin the frame it was handed.
                    var currentFrame = frame;
                    await send(() => SendFrameAsync(socket, currentFrame, cancellationToken), cancellationToken).ConfigureAwait(false);
                    if (queueWake.IsCompleted)
                    {
                        await queueWake.ConfigureAwait(false);
                        queueWake = flushQueueAsync(waits.Token);
                    }
                }
            }
        }
        catch (ChannelClosedException exception) when
            (exception.InnerException is FrontendRunStream.FrontendBackpressureException)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await send(
                    () => CloseSocketAsync(
                        socket,
                        (WebSocketCloseStatus)1011,
                        "backpressure"),
                    cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await waits.CancelAsync().ConfigureAwait(false);
            stream.Unsubscribe(channel);
            try
            {
                await Task.WhenAll(queueWake, nextFrame).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (waits.IsCancellationRequested)
            {
            }
            catch (ChannelClosedException exception) when
                (exception.InnerException is FrontendRunStream.FrontendBackpressureException)
            {
            }
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
        if (_commRouter is null)
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

        if (!_service.SessionExists(sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                new FrontendError(FrontendErrors.NotFound, $"No stored session matches '{sessionId}'."),
                FrontendJsonContext.Default.FrontendError).ConfigureAwait(false);
            return;
        }

        var plane = _commRouter.PlaneFor(sessionId);

        // Same lifecycle shape as the events socket: the socket, peer source, and send gate
        // are disposed after the finally has withdrawn the subscription, cancelled the peer,
        // awaited the uplink, and written the close frame — every closure below completes
        // inside that sequence. The disposed-closure findings are suppressed for this
        // handler's scope.
        // ReSharper disable AccessToDisposedClosure
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var peer = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            _lifetime.Token);
        using var sendGate = new SemaphoreSlim(1, 1);
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
        var uplink = ServeCommUplinkAsync(socket, _commRouter, sessionId, SendAsync, peer.Token);
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
        // ReSharper restore AccessToDisposedClosure
    }

    /// <summary>Reads uplink comm frames until the socket closes; the loop owns its own
    /// transport failures so a dead socket never faults into the handler's finally.
    /// Rejections are typed comm.error text frames, and a frontend-originated open is a
    /// policy close.</summary>
    private async Task ServeCommUplinkAsync(
        WebSocket socket,
        FrontendCommRouter router,
        string sessionId,
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
                    await router
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

    /// <summary>
    ///     Renders one typed frontend failure as its JSON error response. Every rejected
    ///     request funnels through here, so this is also the single place a rejection is
    ///     recorded: without it a 4xx leaves no trace at all when the originating decision
    ///     point does not log. Kept at Debug — a client-caused rejection is diagnostic
    ///     detail, not an operator-visible anomaly; CI raises the Maieutics category to
    ///     Debug, where an unexplained 4xx would otherwise be undiagnosable. Rejections that
    ///     are anomalies worth seeing in production log at their own decision point.
    /// </summary>
    private async Task WriteErrorAsync(HttpContext context, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            // The handler already began a response, so this error cannot reach the client:
            // the request ends with whatever was partially written.
            _logger.LogWarning(
                "Frontend route {Method} {Path} failed with {Code} after the response had started; the client received a partial response.",
                context.Request.Method,
                context.Request.Path.Value,
                code);
            return;
        }

        _logger.LogDebug(
            "Frontend route {Method} {Path} rejected with {Code}.",
            context.Request.Method,
            context.Request.Path.Value,
            code);

        context.Response.StatusCode = code switch
        {
            FrontendErrors.Busy or FrontendErrors.SessionNotActive or FrontendErrors.ConfigurationError
                or FrontendErrors.QueueFull or FrontendErrors.ItemRunning
                => StatusCodes.Status409Conflict,
            FrontendErrors.NotFound => StatusCodes.Status404NotFound,
            FrontendErrors.SkillUnknown => StatusCodes.Status404NotFound,
            FrontendErrors.McpPromptUnknown => StatusCodes.Status404NotFound,
            FrontendErrors.McpPromptUnavailable => StatusCodes.Status409Conflict,
            FrontendErrors.SkillBudgetExceeded => StatusCodes.Status413PayloadTooLarge,
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
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => _started.TrySetResult());
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
            await _started.Task.ConfigureAwait(false);
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

/// <summary>Fallback presenter sink used when the host is composed without an elicitation
/// presenter (tests, tools-only frontends): nothing is ever pending, so every elicitation
/// answer 404s at the input endpoint and the MCP side answers cancel.</summary>
file sealed class NoFramesPublisher : IFrontendSessionFramePublisher
{
    internal static readonly NoFramesPublisher Instance = new();

    public bool TryPublishPresentation(AgentSessionId sessionId, string type, JsonElement data)
    {
        return false;
    }
}
