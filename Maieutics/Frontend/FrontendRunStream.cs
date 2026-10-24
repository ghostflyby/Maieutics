using System.Text.Json;
using System.Threading.Channels;
using Maieutics.Agent;
using Microsoft.Extensions.Logging;

namespace Maieutics.Frontend;

/// <summary>
///     Owns one Agent run's frontend event stream: a single-consumer pump that drains
///     <see cref="IAgentRun.Events" />, appends every rendered frame to a bounded replay
///     buffer, and fans out to live WebSocket subscribers. The pump runs regardless of
///     connections, so a client that attaches late — or reconnects after a backpressure
///     disconnect — replays from <c>sinceSequence</c> (invariant 16: events are never
///     silently dropped; the only loss is explicit retention eviction, surfaced as a
///     <c>run.missing</c> frame). The pump disposes the run exactly once when the terminal
///     frame is published.
/// </summary>
internal sealed class FrontendRunStream : IAsyncDisposable, IFrontendPresentationTarget
{
    internal const int ReplayRetention = 4096;
    internal const int SubscriberQueueCapacity = 1024;
    private static readonly TimeSpan ShutdownCancelTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private readonly List<FrontendEventFrame> _replay = [];
    private readonly List<Subscriber> _subscribers = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AgentSessionId _sessionId;
    private readonly string _ownRunId;
    private readonly IAgentRun _run;
    private readonly FrontendDenoReplPresentationRouter? _presentationRouter;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _replayAppended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Internal test observability: completes on the next frame appended to the
    /// replay buffer. The completed source is swapped for a fresh one under the same lock
    /// as the append, so a captured task observes exactly one later append — the
    /// completion notification for "the replay buffer advanced".</summary>
    internal Task ReplayAppended
    {
        get
        {
            lock (_gate)
            {
                return _replayAppended.Task;
            }
        }
    }

    private void ReplayAppendedLocked()
    {
        _replayAppended.TrySetResult();
        _replayAppended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private IAsyncDisposable? _presentationScope;
    private long _oldestSequence;
    private long _latestSequence;
    private int _startState;
    private int _settleState;
    private int _disposeState;
    private bool _completed;
    private bool _publishClosed;

    private FrontendRunStream(
        AgentSessionId sessionId,
        IAgentRun run,
        FrontendDenoReplPresentationRouter? presentationRouter,
        IAsyncDisposable? presentationScope,
        ILogger logger)
    {
        this._sessionId = sessionId;
        this._run = run;
        _ownRunId = run.Id.Value.ToString("N");
        this._presentationRouter = presentationRouter;
        this._presentationScope = presentationScope;
        this._logger = logger;
    }

    /// <summary>Creates the stream for a run without starting its pump, so the presentation
    /// scope can attach to it before events flow.</summary>
    internal static FrontendRunStream Create(
        AgentSessionId sessionId,
        IAgentRun run,
        FrontendDenoReplPresentationRouter? presentationRouter,
        ILogger logger)
    {
        return new FrontendRunStream(sessionId, run, presentationRouter, null, logger);
    }

    /// <summary>Starts the pump. The stream owns the run's disposal and detaches the
    /// presentation scope once the terminal frame is published.</summary>
    internal void Start(IAsyncDisposable? scope)
    {
        if (Interlocked.CompareExchange(ref _startState, 1, 0) != 0)
            throw new InvalidOperationException("The frontend run stream is already started.");

        _presentationScope = scope;
        // The pump settles its own outcome (terminal publish, fault log, run disposal);
        // no external observation point observes the task itself.
        _ = Task.Run(RunPumpAsync);
    }

    /// <summary>Gets the owning run identifier.</summary>
    internal AgentRunId RunId => _run.Id;

    /// <summary>Gets the owning session identifier.</summary>
    internal AgentSessionId SessionId => _sessionId;

    internal IAgentRun Run => _run;

    /// <summary>Waits for the run to terminate; failures and cancellation surface here.</summary>
    internal Task<AgentRunResult> Completion => _run.Completion;

    /// <summary>
    ///     Waits for the stream's full settlement: terminal frames published, the run
    ///     disposed, and the presentation scope detached. Completes strictly after
    ///     <see cref="Completion" /> — the pump's teardown detaches the presentation
    ///     router's per-session sink last, so a follow-up turn on the same session (which
    ///     attaches that sink while wiring its run stream) can only start after this task
    ///     completes. Never faults.
    /// </summary>
    internal Task Settled => _disposal.Task;

    /// <summary>Requests cooperative cancellation and waits for the run to terminate.</summary>
    internal Task CancelAsync(CancellationToken cancellationToken) => _run.CancelAsync(cancellationToken);

    /// <summary>
    ///     Attaches a subscriber, returning the frames retained since
    ///     <paramref name="sinceSequence" /> and the live queue that continues from the
    ///     snapshot. Both are taken under one lock, so the concatenation of the snapshot and
    ///     the queue is exactly once and in order. When the requested sequence precedes
    ///     retained history the snapshot opens with a single <c>run.missing</c> frame
    ///     followed by whatever the buffer still holds. For a completed run the entire
    ///     replay travels as the snapshot — the bounded queue has no reader yet, so writing
    ///     retained frames into it would silently drop everything past its capacity — and
    ///     the queue completes immediately.
    /// </summary>
    internal (IReadOnlyList<FrontendEventFrame> Initial, Channel<FrontendEventFrame> Channel) Subscribe(
        long sinceSequence)
    {
        var channel = Channel.CreateBounded<FrontendEventFrame>(SubscriberQueueCapacity);
        var subscriber = new Subscriber(channel);
        IReadOnlyList<FrontendEventFrame> initial;
        int subscriberCount;
        lock (_gate)
        {
            _subscribers.Add(subscriber);
            subscriberCount = _subscribers.Count;
            if (_completed)
            {
                // The pump already published its terminal frame; complete the queue so the
                // subscriber's drain loop ends instead of waiting forever.
                channel.Writer.TryComplete();
            }

            initial = BuildSnapshot(sinceSequence);
        }

        _logger.LogDebug(
            "A subscriber attached to run {RunId}; {SubscriberCount} now subscribed.",
            _run.Id,
            subscriberCount);
        return (initial, channel);
    }

    /// <summary>Failure-diagnostics view of the retained frames: one line per frame with
    /// type and sequence (capped), read under the replay lock.</summary>
    internal IReadOnlyList<string> DescribeFrames()
    {
        lock (_gate)
        {
            var lines = new List<string>(_replay.Count);
            foreach (var frame in _replay)
            {
                if (lines.Count >= 32)
                {
                    lines.Add($"… ({_replay.Count - 32} more)");
                    break;
                }

                lines.Add($"#{frame.Sequence}: {frame.Type}");
            }

            return lines;
        }
    }

    /// <summary>Builds the replay snapshot for a subscriber resuming from
    /// <paramref name="sinceSequence" /> under the gate.</summary>
    private List<FrontendEventFrame> BuildSnapshot(long sinceSequence)
    {
        List<FrontendEventFrame> snapshot = [];
        // A resuming client whose next expected frame precedes the oldest retained sequence
        // cannot rebuild the run from the buffer; run.missing tells it to refetch from the
        // transcript endpoint instead of silently rendering a truncated run.
        if (sinceSequence > 0 && _latestSequence > sinceSequence &&
            (_oldestSequence == 0 || sinceSequence + 1 < _oldestSequence))
            snapshot.Add(MissingFrame());

        snapshot.AddRange(_replay.Where(frame => IsReplayable(frame, sinceSequence)));
        return snapshot;
    }

    /// <summary>Removes a subscriber when its WebSocket closes.</summary>
    internal void Unsubscribe(Channel<FrontendEventFrame> channel)
    {
        int subscriberCount;
        lock (_gate)
        {
            for (var index = _subscribers.Count - 1; index >= 0; index--)
                if (ReferenceEquals(_subscribers[index].Channel, channel))
                {
                    _subscribers[index].Channel.Writer.TryComplete();
                    _subscribers.RemoveAt(index);
                }

            subscriberCount = _subscribers.Count;
        }

        _logger.LogDebug(
            "A subscriber detached from run {RunId}; {SubscriberCount} remain subscribed.",
            _run.Id,
            subscriberCount);
    }

    /// <summary>Publishes a REPL presentation frame that carries no run-local sequence.
    /// The documented <c>input.request</c> frame is flat (requestId/prompt/password at the
    /// top level), while the sink hands the typed payload through the generic presentation
    /// seam, so the run stream lifts every field onto the frame. A field this lift forgets is
    /// silently dropped from the wire, so it mirrors the record.</summary>
    public void PublishPresentation(
        string type,
        string? displayId,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        _ = TryPublishPresentationFrame(type, displayId, data, cancellationToken);
    }

    /// <summary>Publishes a REPL presentation frame, returning false when the stream already
    /// published the run's terminal pair. The frame could then only be ordered behind
    /// <c>run.status: idle</c>, so the publisher must be told the presentation is not live:
    /// the approval and elicitation presenters deny or cancel on that answer instead of
    /// waiting for a request no client will ever be offered.</summary>
    internal bool TryPublishPresentationFrame(
        string type,
        string? displayId,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (type == "input.request" &&
            data.Deserialize(FrontendJsonContext.Default.FrontendInputRequest) is { } request)
        {
            return PublishCore(
                new FrontendEventFrame(
                    type,
                    RequestId: request.RequestId,
                    Prompt: request.Prompt,
                    Password: request.Password,
                    Schema: request.Schema,
                    ServerId: request.ServerId,
                    Kind: request.Kind,
                    Tool: request.Tool,
                    Reason: request.Reason),
                trackSequence: true);
        }

        return PublishCore(new FrontendEventFrame(type, DisplayId: displayId, Data: data), trackSequence: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            await _disposal.Task.ConfigureAwait(false);
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _disposal.Task.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    /// <summary>Cancels the run so shutdown does not wait on a provider stream. The host's
    /// shutdown hook is synchronous, so the cancellation request is observed here rather
    /// than awaited by the caller.</summary>
    internal void BeginShutdown()
    {
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
            _logger.LogWarning(exception, "Cancelling run {RunId} for shutdown faulted.", _run.Id);
        }
    }

    private async Task RunPumpAsync()
    {
        _logger.LogDebug("Frontend event pump started for run {RunId}.", _run.Id);
        try
        {
            Publish(new FrontendEventFrame("run.started", RunId: _ownRunId));
            Publish(new FrontendEventFrame("run.status", State: "busy"));
            var truncated = false;
            await foreach (var agentEvent in _run.Events
                               .WithCancellation(_lifetime.Token)
                               .ConfigureAwait(false))
            {
                if (agentEvent is AgentTurnTruncated) truncated = true;
                if (agentEvent is AgentToolStarted startedEvent)
                    _presentationRouter?.OpenCall(_sessionId, startedEvent.CallId);
                var rendered = AgentEventFrameMapper.Render(agentEvent);
                if (rendered is not null) Publish(rendered);
            }

            PublishTerminal(await _run.Completion.ConfigureAwait(false), truncated);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Shutdown; CancelForShutdownAsync records the cancellation source.
            await CancelForShutdownAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            // The run itself was cancelled (frontend cancel endpoint); the completion task
            // already carried the failure, so only the pump's own observation path lands here.
            _logger.LogInformation(
                exception,
                "Frontend run {RunId} of session {SessionId} was cancelled during execution; publishing run.failed with code {Code}.",
                _run.Id,
                _sessionId,
                RunFailedCodes.Cancelled);
            PublishTerminalPair(new FrontendEventFrame(
                "run.failed",
                RunId: _run.Id.Value.ToString("N"),
                Code: RunFailedCodes.Cancelled,
                Message: exception.Message));
            await SettleRunAsync().ConfigureAwait(false);
        }
        catch (AgentException exception)
        {
            var code = FrontendErrors.MapAgentException(exception);
            // The run faulted, so the pump could not drain it to a terminal result. This
            // record is the only place the typed cause is observable: Maieutics.Agent emits
            // no log records of its own by design (docs/logging.md), the wire frame carries
            // the code but only to attached clients, and the run settles as "failed" with no
            // further detail. Without it a failed run is a silent 4xx/timeout to the caller.
            _logger.LogWarning(
                exception,
                "Frontend run {RunId} of session {SessionId} failed with agent code {Code}; publishing run.failed.",
                _run.Id,
                _sessionId,
                code);
            PublishTerminalPair(new FrontendEventFrame(
                "run.failed",
                RunId: _run.Id.Value.ToString("N"),
                Code: code,
                Message: exception.Message));
            await SettleRunAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The frontend event pump for run {RunId} failed unexpectedly.", _run.Id);
            PublishTerminalPair(new FrontendEventFrame(
                "run.failed",
                RunId: _run.Id.Value.ToString("N"),
                Code: RunFailedCodes.AgentError,
                Message: "The agent turn failed."));
            await SettleRunAsync().ConfigureAwait(false);
        }
        finally
        {
            // Safety net for every exit path: the pump owns the run's settlement, so a run
            // that never completed (its producer parked in the bounded events channel)
            // would hold the session's turn gate and this stream's disposal forever.
            await SettleRunAsync().ConfigureAwait(false);
            MarkCompleted();
            try
            {
                if (_presentationScope is not null) await _presentationScope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A failed detach must not skip the settlement signal below: follow-up
                // turns wait for it before they can attach the session's presentation
                // sink again.
                _logger.LogWarning(
                    exception,
                    "Detaching the presentation scope for run {RunId} failed.",
                    _run.Id);
            }

            _disposal.TrySetResult();
        }
    }

    /// <summary>
    ///     Settles and disposes the owned run exactly once. The producer parks in the run's
    ///     bounded events channel and can only unblock through cancellation, so a pump that
    ///     exits before the run completed — an unanticipated failure, or shutdown racing a
    ///     wedged provider — cancels it under the teardown budget and disposes it; otherwise
    ///     the producer, the completion task, and the session's turn gate leak forever.
    ///     Idempotent: concurrent and repeated calls fall through to the same single
    ///     settlement.
    /// </summary>
    private async Task SettleRunAsync()
    {
        if (Interlocked.Exchange(ref _settleState, 1) != 0) return;
        if (!_run.Completion.IsCompleted)
        {
            // The pump exited without the run reaching a terminal state: either an
            // unanticipated failure, or shutdown racing a wedged provider. The sweep below
            // cancels it so the session's single-run gate is released — which means the
            // "failed"/"cancelled" outcome a caller observes may be this cancellation, not
            // the run's own result. Record it, or a settled-as-failed run is indistinguishable
            // from a provider fault in the logs.
            _logger.LogWarning(
                "Frontend run {RunId} of session {SessionId} was still incomplete when the pump stopped draining it ({Status}); cancelling it to release the session's turn gate. The resulting outcome is this settlement, not the run's own result.",
                _run.Id,
                _sessionId,
                DescribeCompletion(_run.Completion));
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                budget.CancelAfter(ShutdownCancelTimeout);
                await _run.CancelAsync(budget.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Cancelling run {RunId} after the pump stopped draining it did not settle.",
                    _run.Id);
            }
        }

        await ObserveCompletionAsync().ConfigureAwait(false);
        _logger.LogDebug(
            "Frontend run {RunId} settled with outcome {Outcome}{FailureCode}.",
            _run.Id,
            DescribeCompletion(_run.Completion),
            DescribeFailure(_run.Completion));
        try
        {
            await _run.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The run's terminal outcome already reached subscribers; disposal observation
            // must not escape the pump's finally.
            _logger.LogWarning(exception, "Disposing run {RunId} after the pump completed failed.", _run.Id);
        }
    }

    private async Task CancelForShutdownAsync()
    {
        _logger.LogInformation(
            "Frontend run {RunId} of session {SessionId} is being cancelled because the host is shutting down; publishing run.failed with code {Code}.",
            _run.Id,
            _sessionId,
            RunFailedCodes.Cancelled);
        PublishTerminalPair(new FrontendEventFrame(
            "run.failed",
            RunId: _run.Id.Value.ToString("N"),
            Code: RunFailedCodes.Cancelled,
            Message: "The host is shutting down."));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            timeout.CancelAfter(ShutdownCancelTimeout);
            await _run.CancelAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Cancelling run {RunId} during shutdown did not settle.", _run.Id);
        }

        await ObserveCompletionAsync().ConfigureAwait(false);
    }

    private async Task ObserveCompletionAsync()
    {
        try
        {
            await _run.Completion.ConfigureAwait(false);
        }
        catch
        {
            // The terminal frame already carries the failure; this only observes the task.
        }
    }

    private static string DescribeCompletion(Task<AgentRunResult> completion)
    {
        return completion.Status switch
        {
            TaskStatus.RanToCompletion => "completed",
            TaskStatus.Faulted => "failed",
            TaskStatus.Canceled => "cancelled",
            _ => "pending"
        };
    }

    /// <summary>Renders the run's typed failure as <c> (code=…)</c> so a settled-as-failed
    /// line names the protocol code directly, or an empty string when nothing failed.
    /// <see cref="Task.Exception" /> never throws, so this is safe on any status.</summary>
    private static string DescribeFailure(Task<AgentRunResult> completion)
    {
        if (completion.Exception is not { } aggregate) return string.Empty;
        var cause = aggregate.GetBaseException();
        var code = cause is AgentException agentException
            ? FrontendErrors.MapAgentException(agentException)
            : RunFailedCodes.AgentError;
        return $" (code={code}, cause={cause.GetType().Name})";
    }

    private void PublishTerminal(AgentRunResult result, bool truncated)
    {
        PublishTerminalPair(new FrontendEventFrame(
            "run.completed",
            RunId: _run.Id.Value.ToString("N"),
            Truncated: result.Truncated || truncated,
            Model: result.ModelIdentity is { } identity
                ? new FrontendModelIdentity(identity.ProfileId.Value, identity.Provider, identity.Model)
                : null,
            Usage: result.Usage is { } usage
                ? new FrontendUsage(
                    usage.InputTokenCount ?? 0,
                    usage.OutputTokenCount ?? 0,
                    usage.TotalTokenCount)
                : null));
    }

    private FrontendEventFrame MissingFrame()
    {
        return new FrontendEventFrame("run.missing", RunId: _run.Id.Value.ToString("N"));
    }

    private bool IsReplayable(FrontendEventFrame frame, long sinceSequence)
    {
        // A child run's frames ride this stream for display only: replay them from retention
        // best-effort — live delivery and terminal frames are exact, while a client that
        // missed child history refetches the report from the spawn tool's persisted result
        // (ADR 0030 decision 8). The parent run's own frames follow the strict contract.
        if (frame.RunId is { } frameRun && frameRun != _ownRunId)
            return true;
        if (sinceSequence <= 0) return true;
        if (frame.Sequence is { } sequence) return sequence > sinceSequence;

        // Unsequenced frames are presentation, not run history. A pending request is the one
        // kind a reconnecting client must still see: without it the request is answerable in
        // principle but invisible in practice, so the user cannot decide and the request can
        // only time out. Replaying an already-answered request is harmless — its answer
        // endpoint is idempotent and answers 404, which clients absorb silently.
        return frame.Type is "run.completed" or "run.failed" or "run.missing" or "input.request";
    }

    private void MarkCompleted()
    {
        lock (_gate)
        {
            _completed = true;
            // Belt and braces for every pump exit path: the terminal pair is the last frame
            // pair, so nothing may be appended after the stream stopped draining its run.
            _publishClosed = true;
            // Complete live queues so subscriber loops advance to the next run; late
            // subscribers still replay the buffer through Subscribe.
            foreach (var subscriber in _subscribers) subscriber.Channel.Writer.TryComplete();

            _subscribers.Clear();
            // Completion advances every replay-derived observation (the snapshot's
            // channel completion included), so it notifies the same signal an append
            // does — a waiter parked awaiting the next append must wake for this.
            ReplayAppendedLocked();
        }
    }

    private void Publish(FrontendEventFrame frame)
    {
        _ = PublishCore(frame, trackSequence: true);
    }

    /// <summary>Publishes a child run's frame into this stream's replay and live queues
    /// without touching the parent run's sequence bookkeeping: child sequences are
    /// child-run-local, so the stream's oldest/latest tracking and the resume gap check stay
    /// parent-scoped. Child frames replay best-effort from retention (ADR 0030 decision 8);
    /// live delivery is never dropped (invariant 16). A child frame offered after the parent's
    /// terminal pair is refused like any other: its producer is a display tap, not a run the
    /// stream still serves.</summary>
    internal void PublishChildFrame(FrontendEventFrame frame)
    {
        _ = PublishCore(frame, trackSequence: false);
    }

    /// <summary>Publishes the run's terminal pair — the terminal frame followed by
    /// <c>run.status idle</c> — and closes the stream to every later frame. Both halves travel
    /// under one lock hold, so a concurrent publisher cannot be ordered between them, and the
    /// fence is set before the lock is released, so a publisher that raced the pair observes
    /// the refusal instead of a frame behind <c>run.status idle</c> (invariant 7). Idempotent
    /// per stream: a path that reached a second terminal pair would otherwise append a
    /// duplicate pair behind the first.</summary>
    private void PublishTerminalPair(FrontendEventFrame terminalFrame)
    {
        lock (_gate)
        {
            if (_publishClosed) return;

            PublishLocked(terminalFrame, trackSequence: true);
            PublishLocked(new FrontendEventFrame("run.status", State: "idle"), trackSequence: true);
            _publishClosed = true;
        }

        DisconnectOverflowingSubscribers();
    }

    private bool PublishCore(FrontendEventFrame frame, bool trackSequence)
    {
        lock (_gate)
        {
            // A closed stream refuses frames outright: the run's terminal pair is its last
            // frame pair, and a client resuming with sinceSequence rebuilds the run from this
            // replay buffer, so a frame appended after it would be rendered as output of a run
            // that already ended (invariant 7). The refusal is also the publisher's answer that
            // the presentation is no longer live: the approval and elicitation presenters deny
            // or cancel on it instead of waiting for a request no client will ever be offered.
            if (_publishClosed) return false;

            PublishLocked(frame, trackSequence);
        }

        DisconnectOverflowingSubscribers();
        return true;
    }

    /// <summary>Appends one frame to the replay buffer and the live subscriber queues. The
    /// caller holds <see cref="_gate" />.</summary>
    private void PublishLocked(FrontendEventFrame frame, bool trackSequence)
    {
        if (trackSequence && frame.Sequence is { } sequence)
        {
            if (_oldestSequence == 0) _oldestSequence = sequence;
            if (sequence > _latestSequence) _latestSequence = sequence;
        }

        _replay.Add(frame);
        ReplayAppendedLocked();
        if (_replay.Count > ReplayRetention)
        {
            var evicted = _replay[0];
            _replay.RemoveAt(0);
            // Retention evicts from the front of the buffer, so the oldest retained
            // sequence must follow the eviction: if it kept the all-time minimum,
            // resumed clients behind the eviction would look current and silently
            // receive a truncated replay (invariant 16's only permitted loss must
            // surface as run.missing). Child frames are skipped: their sequences live
            // in their own run-local spaces.
            if (trackSequence &&
                evicted.Sequence is { } evictedSequence && evictedSequence == _oldestSequence)
            {
                _oldestSequence = 0;
                foreach (var retained in _replay)
                    if (retained.RunId == _ownRunId && retained.Sequence is { } retainedSequence)
                    {
                        _oldestSequence = retainedSequence;
                        break;
                    }
            }
        }

        foreach (var subscriber in _subscribers)
            if (!subscriber.Channel.Writer.TryWrite(frame))
                subscriber.Faulted = true;
    }

    /// <summary>Closes overflowing subscribers outside the lock: backpressure disconnects
    /// instead of dropping frames, and the client reconnects with its last observed sequence.
    /// The queue completes with a marker exception after its buffered frames, so the handler
    /// closes the socket as a backpressure disconnect rather than a normal run end.</summary>
    private void DisconnectOverflowingSubscribers()
    {
        List<Subscriber>? overflowing = null;
        lock (_gate)
        {
            for (var index = _subscribers.Count - 1; index >= 0; index--)
                if (_subscribers[index].Faulted)
                {
                    overflowing ??= [];
                    overflowing.Add(_subscribers[index]);
                    _subscribers[index].Channel.Writer.TryComplete(
                        new FrontendBackpressureException("The frontend subscriber queue overflowed."));
                    _subscribers.RemoveAt(index);
                }
        }

        if (overflowing is not null)
            _logger.LogWarning(
                "Disconnected {Count} frontend subscriber(s) of run {RunId} for backpressure.",
                overflowing.Count,
                _run.Id);
    }

    private sealed class Subscriber(Channel<FrontendEventFrame> channel)
    {
        internal Channel<FrontendEventFrame> Channel { get; } = channel;

        internal bool Faulted { get; set; }
    }

    /// <summary>Completes a subscriber's queue with this marker so its WebSocket handler can
    /// close the socket as a backpressure disconnect instead of a normal run end.</summary>
    internal sealed class FrontendBackpressureException : ChannelClosedException
    {
        internal FrontendBackpressureException(string message)
            : base(message)
        {
        }
    }
}

/// <summary>Keeps every run's frontend stream addressable while it is replayable.</summary>
internal sealed class FrontendRunRegistry
{
    private const int RetainedCompletedStreams = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<AgentRunId, FrontendRunStream> _streams = [];
    private readonly Queue<AgentRunId> _completionOrder = [];

    /// <summary>Registers a running stream and prunes the oldest completed ones.</summary>
    internal void Add(FrontendRunStream stream)
    {
        lock (_gate)
        {
            _streams[stream.RunId] = stream;
        }

        // Watch completion outside the registry lock: the watch may complete synchronously
        // and re-enter the lock, and System.Threading.Lock is not reentrant.
        _ = TrackCompletionAsync(stream);
    }

    internal bool TryGet(AgentRunId runId, out FrontendRunStream? stream)
    {
        lock (_gate)
        {
            return _streams.TryGetValue(runId, out stream);
        }
    }

    /// <summary>Requests cooperative cancellation of every retained stream so process shutdown
    /// does not wait on a provider stream. Completed streams ignore the request.</summary>
    internal void BeginShutdown()
    {
        FrontendRunStream[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _streams.Values];
        }

        foreach (var stream in snapshot) stream.BeginShutdown();
    }

    private async Task TrackCompletionAsync(FrontendRunStream stream)
    {
        try
        {
            await stream.Completion.ConfigureAwait(false);
        }
        catch
        {
            // Completion failure is already surfaced to subscribers; pruning only observes.
        }

        List<FrontendRunStream>? evicted = null;
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream.RunId, out var current) || !ReferenceEquals(current, stream)) return;

            _completionOrder.Enqueue(stream.RunId);
            while (_completionOrder.Count > RetainedCompletedStreams)
            {
                var oldest = _completionOrder.Dequeue();
                if (_streams.Remove(oldest, out var removed))
                {
                    evicted ??= [];
                    evicted.Add(removed);
                }
            }
        }

        // Disposal happens outside the lock: disposing a completed stream never blocks, but
        // the lock must not be held across awaits.
        if (evicted is not null)
            foreach (var removed in evicted)
                await removed.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Maps typed Agent failures onto stable frontend protocol error codes.</summary>
internal static class FrontendErrors
{
    internal const string Busy = "agent_busy";
    internal const string NotFound = "not_found";
    internal const string SessionNotActive = "session_not_active";
    internal const string InvalidRequest = "invalid_request";
    internal const string CommandError = "command_error";
    internal const string ConfigurationError = "agent_configuration_error";
    internal const string InputTooLarge = "agent_input_too_large";
    internal const string QueueFull = "queue_full";
    internal const string ItemRunning = "item_running";
    internal const string SkillUnknown = "skill_unknown";
    internal const string SkillBudgetExceeded = "skill_budget_exceeded";
    internal const string McpPromptUnknown = "mcp_prompt_unknown";
    internal const string McpPromptUnavailable = "mcp_prompt_unavailable";
    internal const string McpPromptArgumentInvalid = "mcp_prompt_argument_invalid";
    internal const string McpPromptResultInvalid = "mcp_prompt_result_invalid";

    internal static string MapAgentException(AgentException exception)
    {
        return exception switch
        {
            AgentProviderException => "agent_provider_error",
            AgentInputLimitExceededException => InputTooLarge,
            AgentResponseLimitExceededException => "agent_response_too_large",
            AgentToolLimitExceededException => "agent_tool_limit_exceeded",
            AgentToolArgumentsException => "agent_tool_arguments_error",
            AgentToolInvocationException => "agent_tool_error",
            AgentTurnDurationExceededException => "agent_turn_duration_exceeded",
            AgentModelIterationLimitExceededException => "agent_model_iteration_limit",
            AgentModelCapabilityException => "agent_model_capability_error",
            AgentContentCompatibilityException => "agent_unsupported_response",
            AgentUnsupportedResponseException => "agent_unsupported_response",
            AgentTurnInProgressException => Busy,
            _ => RunFailedCodes.AgentError
        };
    }
}

/// <summary>
///     The <c>run.failed</c> codes the frontend emits that are not derived from an
///     <see cref="AgentException" />. Single-sourced so a log record and the wire frame it
///     describes can never disagree; the values are frontend protocol surface
///     (<c>docs/web-frontend-protocol.md</c>) and must not change.
/// </summary>
internal static class RunFailedCodes
{
    internal const string Cancelled = "run_cancelled";
    internal const string AgentError = "agent_error";
}
