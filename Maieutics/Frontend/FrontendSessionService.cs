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
    private readonly ILogger logger;
    private readonly Lock gate = new();
    private readonly Channel<FrontendRunStream> runAnnouncements =
        Channel.CreateUnbounded<FrontendRunStream>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
    private FrontendRunStream? latestRun;

    public FrontendSessionService(
        MaieuticsAgentSessionManager sessionManager,
        MaieuticsCommandExecutor commandExecutor,
        FrontendDenoReplPresentationRouter presentationRouter,
        ILogger<FrontendSessionService> logger,
        IMaieuticsRuntimeConfiguration? runtimeConfiguration = null,
        MaieuticsStatusProvider? statusProvider = null)
    {
        this.sessionManager = sessionManager;
        this.commandExecutor = commandExecutor;
        this.presentationRouter = presentationRouter;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.runtimeConfiguration = runtimeConfiguration;
        this.statusProvider = statusProvider;
    }

    /// <summary>Executes a Maieutics command cell and returns its markdown answer.</summary>
    public Task<string> ExecuteCommandAsync(string text, CancellationToken cancellationToken)
    {
        return commandExecutor.ExecuteAsync(text, cancellationToken);
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

    /// <summary>Gets the active session's wire description.</summary>
    public FrontendSessionInfo DescribeSession()
    {
        return new FrontendSessionInfo(
            sessionManager.Id.Value.ToString("N"),
            sessionManager.GetTranscriptSnapshot().Turns.Length,
            sessionManager.PersistenceEnabled);
    }

    /// <summary>Lists stored sessions across family databases.</summary>
    public IReadOnlyList<FrontendStoredSession> ListStoredSessions()
    {
        return sessionManager.ListStoredSessions()
            .Select(session => new FrontendStoredSession(
                session.Id.Value.ToString("N"),
                session.TurnCount,
                session.CreatedAt,
                session.LastActivityAt))
            .ToArray();
    }

    /// <summary>Starts a new session and makes it active.</summary>
    public FrontendSessionInfo StartNew()
    {
        sessionManager.StartNew();
        return DescribeSession();
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

        ResetRunAnnouncements();
        return DescribeSession();
    }

    /// <summary>Prunes unreferenced objects.</summary>
    public int PruneObjects(string sessionId, int graceHours)
    {
        EnsureActive(sessionId);
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
        EnsureActive(sessionId);
        try
        {
            return sessionManager.RepairObjectView();
        }
        catch (ArgumentException exception)
        {
            throw new FrontendFailureException(FrontendErrors.InvalidRequest, exception.Message);
        }
    }

    /// <summary>Gets the authoritative committed history of a session.</summary>
    public FrontendTranscript GetTranscript(string sessionId)
    {
        EnsureActive(sessionId);
        return FrontendTranscriptMapper.ToTranscript(sessionManager.GetTranscriptSnapshot());
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

        EnsureActive(sessionId);
        ValidateTurnConfiguration();
        IAgentRun run;
        try
        {
            run = await sessionManager.StartTurnAsync(AgentTurn.FromText(text)).ConfigureAwait(false);
        }
        catch (AgentTurnInProgressException exception)
        {
            throw new FrontendFailureException(FrontendErrors.Busy, exception.Message);
        }
        catch (AgentException exception)
        {
            throw new FrontendFailureException(FrontendErrors.MapAgentException(exception), exception.Message);
        }

        var stream = FrontendRunStream.Create(sessionManager.Id, run, presentationRouter, logger);
        var scope = presentationRouter.Attach(sessionManager.Id, stream);
        stream.Start(scope);
        registry.Add(stream);

        lock (gate)
        {
            latestRun = stream;
        }

        runAnnouncements.Writer.TryWrite(stream);
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
    ///     Waits for the session's run to serve on an events WebSocket. When
    ///     <paramref name="previous" /> is not the latest announced run it is returned
    ///     immediately; otherwise the call blocks for the next announced run.
    /// </summary>
    public async Task<FrontendRunStream> WaitForRunAsync(
        AgentSessionId sessionId,
        FrontendRunStream? previous,
        CancellationToken cancellationToken)
    {
        EnsureActive(sessionId.Value.ToString("N"));
        FrontendRunStream? latest;
        lock (gate)
        {
            latest = latestRun;
        }

        if (latest is not null && !ReferenceEquals(latest, previous)) return latest;

        return await runAnnouncements.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ResetRunAnnouncements()
    {
        lock (gate)
        {
            latestRun = null;
        }

        while (runAnnouncements.Reader.TryRead(out _))
        {
        }
    }

    private void EnsureActive(string sessionId)
    {
        if (!string.Equals(sessionId, sessionManager.Id.Value.ToString("N"), StringComparison.Ordinal))
            throw new FrontendFailureException(
                FrontendErrors.SessionNotActive,
                "Turns and session queries are served by the active session.");
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
