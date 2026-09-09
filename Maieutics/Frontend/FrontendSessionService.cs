using System.Collections.Concurrent;
using System.Threading.Channels;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Frontend;

/// <summary>Typed failure carried to the HTTP surface as a protocol error body.</summary>
internal sealed class FrontendFailureException(string code, string message) : Exception(message)
{
    /// <summary>Gets the stable protocol error code.</summary>
    public string Code { get; } = code;
}

/// <summary>
///     Orchestrates the frontend protocol against the executable's single authoritative
///     agent session: turn submission (one in-flight run per session; concurrent turns are
///     typed busy errors — the protocol does not queue), cancel, transcript snapshots,
///     session lifecycle, and run-stream lookup for the events WebSocket. Runs are owned by
///     their <see cref="FrontendRunStream" />, which keeps events flowing and replayable
///     regardless of connections.
/// </summary>
internal sealed class FrontendSessionService
{
    private readonly MaieuticsCommandExecutor commandExecutor;
    private readonly FrontendDenoReplPresentationRouter presentationRouter;
    private readonly FrontendRunRegistry registry = new();
    private readonly MaieuticsAgentSessionManager sessionManager;
    private readonly MaieuticsStatusProvider? statusProvider;
    private readonly IMaieuticsRuntimeConfiguration? runtimeConfiguration;
    private readonly Func<string?>? workspaceRootAccessor;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, SessionRunHub> hubs = new(StringComparer.Ordinal);

    public FrontendSessionService(
        MaieuticsAgentSessionManager sessionManager,
        MaieuticsCommandExecutor commandExecutor,
        FrontendDenoReplPresentationRouter presentationRouter,
        ILogger<FrontendSessionService> logger,
        IMaieuticsRuntimeConfiguration? runtimeConfiguration = null,
        MaieuticsStatusProvider? statusProvider = null,
        Func<string?>? workspaceRootAccessor = null)
    {
        this.sessionManager = sessionManager;
        this.commandExecutor = commandExecutor;
        this.presentationRouter = presentationRouter;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.runtimeConfiguration = runtimeConfiguration;
        this.statusProvider = statusProvider;
        this.workspaceRootAccessor = workspaceRootAccessor;
    }

    /// <summary>Executes a Maieutics command cell and returns its markdown answer plus
    /// whether this execution moved the foreground session. The addressing session scopes
    /// session-aware commands (%session current, %model use); a null context keeps the
    /// foreground semantics of the session-blind command endpoint.</summary>
    public async Task<(string Markdown, bool MovedForeground)> ExecuteCommandAsync(
        string text,
        AgentSessionId? session,
        CancellationToken cancellationToken)
    {
        var answer = await commandExecutor.ExecuteAsync(text, session, cancellationToken).ConfigureAwait(false);
        return (answer.Markdown, answer.MovedForeground);
    }

    /// <summary>Renders the status snapshot as markdown.</summary>
    /// <exception cref="FrontendFailureException">The status provider is unavailable.</exception>
    public string CaptureStatusMarkdown()
    {
        if (statusProvider is null)
            throw new FrontendFailureException(
                FrontendErrors.CommandError, "Status is not available in this host.");

        return MaieuticsStatusRenderer.Render(statusProvider.Capture());
    }

    /// <summary>Computes command completions for a UTF-16 cursor.</summary>
    /// <exception cref="FrontendFailureException">The model configuration is unavailable.</exception>
    public FrontendCompleteResponse Complete(FrontendCompleteRequest request)
    {
        if (runtimeConfiguration is null)
            throw new FrontendFailureException(
                FrontendErrors.CommandError, "Completion is not available in this host.");

        var completion = MaieuticsCommandLanguage.Complete(
            request.Text,
            request.Cursor,
            runtimeConfiguration.GetModelProfileSelection().Profiles,
            runtimeConfiguration.GetCachedAutomaticModelProfiles(),
            runtimeConfiguration.GetModelSourceIds());
        return new FrontendCompleteResponse(completion.Matches, completion.TokenStart, completion.TokenEnd);
    }

    /// <summary>Gets the process workspace root for capabilities; the accessor is live so a
    /// <c>%workspace use</c> switch is reflected without a restart.</summary>
    public string? WorkspaceRoot => workspaceRootAccessor?.Invoke();

    /// <summary>Gets the foreground session's wire description — the compatibility alias
    /// behind <c>GET /v1/agent/session</c> and the capabilities payload. Identity and turn
    /// count are read atomically; the title lookup follows the captured identity.</summary>
    public FrontendSessionInfo DescribeSession()
    {
        var (id, turns) = sessionManager.ForegroundInfo;
        return new FrontendSessionInfo(
            id.Value.ToString("N"),
            turns,
            sessionManager.PersistenceEnabled,
            sessionManager.FindDescriptor(id)?.Title);
    }

    /// <summary>Whether one session has a stored row, without mutating the live set
    /// (existence checks for socket handshakes).</summary>
    public bool SessionExists(string sessionId)
    {
        var id = ParseSessionId(sessionId);
        return sessionManager.FindDescriptor(id) is not null || sessionManager.IsLive(id);
    }

    /// <summary>Gets one session's wire description, resolving it lazily when it is stored
    /// but not live. Addressing a session never moves the foreground.</summary>
    /// <exception cref="FrontendFailureException">The session id is invalid or unknown.</exception>
    public FrontendSessionInfo DescribeSession(string sessionId)
    {
        var session = ResolveSession(sessionId);
        var descriptor = sessionManager.FindDescriptor(session.Id);
        return new FrontendSessionInfo(
            session.Id.Value.ToString("N"),
            session.GetTranscriptSnapshot().Turns.Length,
            sessionManager.PersistenceEnabled,
            descriptor?.Title);
    }

    /// <summary>Resolves the addressed session: live → use; stored → lazy-resume; otherwise
    /// a typed 404. This replaces the old active-session gate.</summary>
    private IAgentSession ResolveSession(string sessionId)
    {
        var id = ParseSessionId(sessionId);
        try
        {
            return sessionManager.Resolve(id);
        }
        catch (AgentSessionNotFoundException)
        {
            throw new FrontendFailureException(
                FrontendErrors.NotFound, $"No stored session matches '{sessionId}'.");
        }
        catch (ArgumentException exception)
        {
            throw new FrontendFailureException(FrontendErrors.InvalidRequest, exception.Message);
        }
    }

    /// <summary>Lists stored sessions across family databases.</summary>
    public IReadOnlyList<FrontendStoredSession> ListStoredSessions()
    {
        return sessionManager.ListStoredSessions()
            .Select(session => new FrontendStoredSession(
                session.Id.Value.ToString("N"),
                session.TurnCount,
                session.CreatedAt,
                session.LastActivityAt,
                session.Title,
                session.Preview,
                session.WorkspaceRoot,
                session.ParentSessionId?.Value.ToString("N"),
                session.ForkPointSeq))
            .ToArray();
    }

    /// <summary>Sets or clears one stored session's title. Not active-session-gated: renaming
    /// an old session from a tree view is the primary surface.</summary>
    /// <exception cref="FrontendFailureException">Persistence is disabled, the session id is
    /// invalid, or the title exceeds the length cap.</exception>
    public FrontendRenameResponse Rename(string sessionId, string title)
    {
        var id = ParseSessionId(sessionId);
        string? stored;
        try
        {
            stored = sessionManager.SetTitle(id, title);
        }
        catch (ArgumentException exception)
        {
            throw new FrontendFailureException(FrontendErrors.InvalidRequest, exception.Message);
        }

        return new FrontendRenameResponse(id.Value.ToString("N"), stored);
    }

    /// <summary>Starts a new session and makes it the foreground. The returned
    /// description is always the created session, even under concurrent switches.</summary>
    public FrontendSessionInfo StartNew()
    {
        var id = sessionManager.StartNew();
        return DescribeSession(id.Value.ToString("N"));
    }

    /// <summary>Forks a stored session and makes the fork the active session. Not
    /// active-session-gated: forking an old session from a notebook view is the primary
    /// surface (the run-rewind flow). An optional profile override switches the model
    /// profile first, so the fork's first turn runs on it (regenerate-with-model).</summary>
    /// <exception cref="FrontendFailureException">Persistence is disabled, the session id or
    /// fork point is invalid, the source is unknown, the referenced run never committed, or
    /// the requested profile is unknown.</exception>
    public async Task<FrontendForkResponse> ForkAsync(
        string sessionId,
        FrontendForkRequest request,
        CancellationToken cancellationToken)
    {
        var id = ParseSessionId(sessionId);

        // Resolve (but do not apply) the profile override up front so an
        // unknown profile is rejected before anything is created; the switch
        // itself happens only after the fork succeeded, so a failed fork
        // leaves no side effect behind.
        string? profileId = null;
        if (request.ProfileId is { } requestedProfile)
        {
            if (runtimeConfiguration is null)
                throw new FrontendFailureException(
                    FrontendErrors.InvalidRequest, "Model profile selection is not available in this host.");

            var selection = runtimeConfiguration.GetModelProfileSelection();
            var match = selection.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, requestedProfile, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new FrontendFailureException(
                    FrontendErrors.InvalidRequest, $"No model profile matches '{requestedProfile}'.");

            profileId = match.Id;
        }

        var forkPointSeq = ResolveForkPointSeq(id, request);
        try
        {
            var forkId = sessionManager.Fork(id, forkPointSeq);
            if (profileId is { } appliedProfile)
            {
                // The override is the fork's own, not the process selection: the
                // source session keeps answering on its profile.
                sessionManager.SetProfileOverride(forkId, appliedProfile);
            }

            var title = sessionManager.FindDescriptor(forkId)?.Title;
            return new FrontendForkResponse(forkId.Value.ToString("N"), title);
        }
        catch (AgentSessionNotFoundException)
        {
            throw new FrontendFailureException(
                FrontendErrors.NotFound, $"No stored session matches '{sessionId}'.");
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new FrontendFailureException(FrontendErrors.InvalidRequest, exception.Message);
        }
        catch (ArgumentException exception)
        {
            throw new FrontendFailureException(FrontendErrors.InvalidRequest, exception.Message);
        }
    }

    /// <summary>Lists the selectable model profiles (read-only) so frontends can offer
    /// model pickers without scraping command output.</summary>
    /// <exception cref="FrontendFailureException">The model configuration is unavailable.</exception>
    public IReadOnlyList<FrontendModelProfile> ListModelProfiles()
    {
        if (runtimeConfiguration is null)
            throw new FrontendFailureException(
                FrontendErrors.CommandError, "Model profiles are not available in this host.");

        return runtimeConfiguration.GetModelProfileSelection()
            .Profiles
            .Select(profile => new FrontendModelProfile(
                profile.Id, profile.Provider, profile.Model, profile.IsSelected))
            .ToArray();
    }

    /// <summary>Converts the fork request's run id or sequence into the number of the source's
    /// committed turns the fork keeps. A run id names the turn the fork rewinds to, so the
    /// fork re-runs that turn as its own first one. Run ids are accepted in any textual
    /// representation (matching the command surface).</summary>
    private int ResolveForkPointSeq(AgentSessionId id, FrontendForkRequest request)
    {
        if (request.Seq is { } seq) return seq;

        if (!Guid.TryParse(request.RunId, out var runGuid) || runGuid == Guid.Empty)
            throw new FrontendFailureException(
                FrontendErrors.InvalidRequest, "The fork request must carry a runId or a seq.");

        var transcript = sessionManager.LoadStoredTranscript(id);
        if (transcript is null)
            throw new FrontendFailureException(
                FrontendErrors.NotFound, $"No stored session matches '{id.Value.ToString("N")}'.");

        var runId = new AgentRunId(runGuid);
        for (var index = 0; index < transcript.Turns.Length; index++)
        {
            if (transcript.Turns[index].RunId == runId) return index;
        }

        throw new FrontendFailureException(
            FrontendErrors.NotFound,
            $"No committed turn of '{id.Value.ToString("N")}' matches run '{request.RunId}'.");
    }

    /// <summary>Resumes a stored session and makes it active.</summary>
    /// <exception cref="FrontendFailureException">Persistence is disabled or the session is unknown.</exception>
    public FrontendSessionInfo Resume(string sessionId)
    {
        var id = ParseSessionId(sessionId);
        try
        {
            sessionManager.Resume(id);
        }
        catch (AgentSessionNotFoundException)
        {
            throw new FrontendFailureException(
                FrontendErrors.NotFound, $"No stored session matches '{sessionId}'.");
        }
        catch (ArgumentException exception)
        {
            throw new FrontendFailureException(FrontendErrors.InvalidRequest, exception.Message);
        }

        return DescribeSession(sessionId);
    }

    /// <summary>Prunes unreferenced objects.</summary>
    public int PruneObjects(string sessionId, int graceHours)
    {
        ResolveSession(sessionId);
        try
        {
            return sessionManager.PruneObjects(TimeSpan.FromHours(graceHours));
        }
        catch (ArgumentException exception)
        {
            throw new FrontendFailureException(FrontendErrors.InvalidRequest, exception.Message);
        }
    }

    /// <summary>Rebuilds the derived object view.</summary>
    public int RepairObjectView(string sessionId)
    {
        ResolveSession(sessionId);
        try
        {
            return sessionManager.RepairObjectView();
        }
        catch (ArgumentException exception)
        {
            throw new FrontendFailureException(FrontendErrors.InvalidRequest, exception.Message);
        }
    }

    /// <summary>Gets the authoritative committed history of a session, lazily resuming it
    /// when stored but not live.</summary>
    public FrontendTranscript GetTranscript(string sessionId)
    {
        var session = ResolveSession(sessionId);
        return FrontendTranscriptMapper.ToTranscript(session.GetTranscriptSnapshot());
    }

    /// <summary>
    ///     Submits one Agent turn. The run starts and its pump runs independently of the
    ///     calling request; the caller receives the run id immediately.
    /// </summary>
    /// <exception cref="FrontendFailureException">Concurrent turn, inactive session, or missing
    /// model configuration.</exception>
    public async Task<FrontendTurnAccepted> StartTurnAsync(string sessionId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FrontendFailureException(FrontendErrors.InvalidRequest, "The turn text must not be empty.");

        var session = ResolveSession(sessionId);
        ValidateTurnConfiguration();
        IAgentRun run;
        try
        {
            run = await session.StartTurnAsync(AgentTurn.FromText(text)).ConfigureAwait(false);
        }
        catch (AgentTurnInProgressException exception)
        {
            throw new FrontendFailureException(FrontendErrors.Busy, exception.Message);
        }
        catch (AgentException exception)
        {
            throw new FrontendFailureException(FrontendErrors.MapAgentException(exception), exception.Message);
        }

        var stream = FrontendRunStream.Create(session.Id, run, presentationRouter, logger);
        var scope = presentationRouter.Attach(session.Id, stream);
        stream.Start(scope);
        registry.Add(stream);

        HubFor(session.Id).Announce(stream);
        return new FrontendTurnAccepted(run.Id.Value.ToString("N"));
    }

    /// <summary>Resolves the stream of a run while it is still retained.</summary>
    public bool TryGetRun(string runId, out FrontendRunStream? stream)
    {
        stream = null;
        if (!Guid.TryParseExact(runId, "N", out var parsed)) return false;

        return registry.TryGet(new AgentRunId(parsed), out stream);
    }

    /// <summary>Cancels a run cooperatively and waits for its termination.</summary>
    public async Task CancelRunAsync(string runId, CancellationToken cancellationToken)
    {
        if (!TryGetRun(runId, out var stream) || stream is null)
            throw new FrontendFailureException(FrontendErrors.NotFound, $"No retained run matches '{runId}'.");

        try
        {
            await stream.CancelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller stopped waiting; the run-side cancellation continues independently.
        }
    }

    /// <summary>
    ///     Waits for the addressed session's next run to serve on its events WebSocket.
    ///     When <paramref name="previous" /> is not that session's latest announced run it
    ///     is returned immediately; otherwise the call blocks for the next announced run.
    /// </summary>
    public async Task<FrontendRunStream> WaitForRunAsync(
        AgentSessionId sessionId,
        FrontendRunStream? previous,
        CancellationToken cancellationToken)
    {
        var hub = HubFor(sessionId);
        var latest = hub.Latest;
        if (latest is not null && !ReferenceEquals(latest, previous))
        {
            hub.Drain(latest);
            return latest;
        }

        return await hub.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private SessionRunHub HubFor(AgentSessionId sessionId)
    {
        return hubs.GetOrAdd(sessionId.Value.ToString("N"), static _ => new SessionRunHub());
    }

    /// <summary>One session's run announcements: the latest stream for immediate
    /// attachment and a small bounded buffer the events socket blocks on between runs.
    /// The latest announcement supersedes older ones, so the buffer is drained on the
    /// fast path and overflow drops the oldest entries instead of growing without
    /// bound when no socket is listening.</summary>
    private sealed class SessionRunHub
    {
        private readonly Lock gate = new();
        private readonly Channel<FrontendRunStream> announcements =
            Channel.CreateBounded<FrontendRunStream>(new BoundedChannelOptions(8)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        private FrontendRunStream? latest;

        public FrontendRunStream? Latest
        {
            get
            {
                lock (gate)
                {
                    return latest;
                }
            }
        }

        public void Announce(FrontendRunStream stream)
        {
            lock (gate)
            {
                latest = stream;
            }

            announcements.Writer.TryWrite(stream);
        }

        /// <summary>Drops buffered announcements superseded by
        /// <paramref name="current" /> (or everything, when null).</summary>
        public void Drain(FrontendRunStream? current)
        {
            while (announcements.Reader.TryRead(out var item))
            {
                if (current is not null && ReferenceEquals(item, current)) continue;
            }
        }

        public async Task<FrontendRunStream> ReadAsync(CancellationToken cancellationToken)
        {
            var stream = await announcements.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Drain(stream);
            return stream;
        }
    }

    private static AgentSessionId ParseSessionId(string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out var parsed) || parsed == Guid.Empty)
            throw new FrontendFailureException(FrontendErrors.InvalidRequest, "The session id is not valid.");

        return new AgentSessionId(parsed);
    }

    private void ValidateTurnConfiguration()
    {
        if (runtimeConfiguration is not null &&
            runtimeConfiguration.GetModelProfileSelection().Profiles.Count == 0)
            throw new FrontendFailureException(
                FrontendErrors.ConfigurationError,
                "No model profile is configured. Configure a model before submitting an Agent turn.");
    }
}
