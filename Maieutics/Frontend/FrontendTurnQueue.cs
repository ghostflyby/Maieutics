using Maieutics.Agent;
using Maieutics.Commands;
using Microsoft.Extensions.Logging;

namespace Maieutics.Frontend;

/// <summary>
///     Server-side per-session queue of Agent turn items (ADR 0025). Each session owns a
///     bounded FIFO (capacity <see cref="Capacity" />) and at most one worker loop; the
///     worker dequeues the head, submits it through
///     <see cref="FrontendSessionService.StartTurnAsync" /> — the same path a direct
///     <c>POST /turns</c> takes, so the session's single-run gate stays the only
///     serialization point (invariant 4) — waits for the run to settle, and continues.
///     Direct turn submissions keep their busy rejection; when a direct submission races
///     the worker's dequeue, the worker waits the in-flight run out (bounded) and retries.
///     Queue state is server state: it survives client disconnects and reloads, and every
///     mutation publishes the session's full state as a <c>queue.updated</c> event frame
///     (no sequence number, idempotent full replacement). Phase 1 drops pending items on
///     session shutdown or queue disposal; durability across server restarts (SQLite) is
///     deferred.
/// </summary>
internal sealed class FrontendTurnQueue : IAsyncDisposable
{
    /// <summary>Bounded FIFO: at most this many items may be queued per session; an
    /// enqueue whose committed total would exceed it is rejected all-or-nothing.</summary>
    internal const int Capacity = 64;

    /// <summary>One POST carries at most one full queue's worth of items.</summary>
    internal const int MaxBatchItems = Capacity;

    /// <summary>Submission retries after the first busy failure when a direct turn raced
    /// the dequeue; the session's in-flight run is waited out between attempts.</summary>
    private const int BusyRetryLimit = 3;

    private static readonly TimeSpan BusyWaitBudget = TimeSpan.FromSeconds(60);

    /// <summary>The executable creates every session with default options, so the default
    /// input cap is the same limit a direct turn submission enforces at run start.</summary>
    private static readonly int MaxItemCharacters = new AgentSessionOptions().MaxInputCharacters;

    private readonly FrontendSessionService service;
    private readonly MaieuticsAgentSessionManager sessionManager;
    private readonly ILogger logger;
    private readonly Lock gate = new();
    private readonly Dictionary<string, SessionQueue> sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposeState;

    public FrontendTurnQueue(
        FrontendSessionService service,
        MaieuticsAgentSessionManager sessionManager,
        ILogger<FrontendTurnQueue> logger)
    {
        this.service = service;
        this.sessionManager = sessionManager;
        this.logger = logger;
    }

    /// <summary>Enqueues 1..64 turn texts atomically (all or nothing) and starts the
    /// session's worker when one is not already running. Positions in the answer are
    /// 1-based pending indices in run order.</summary>
    /// <exception cref="FrontendFailureException">The session is unresolvable, the batch
    /// shape or an item text is invalid, or the capacity would be exceeded.</exception>
    public FrontendQueueEnqueueResponse Enqueue(string sessionId, IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        service.EnsureSessionResolved(sessionId);
        if (texts.Count == 0 || texts.Count > MaxBatchItems)
            throw new FrontendFailureException(
                FrontendErrors.InvalidRequest,
                $"The queue accepts between 1 and {MaxBatchItems} items per request.");

        foreach (var text in texts) ValidateItemText(text);

        var enqueuedAt = DateTimeOffset.UtcNow;
        var items = texts
            .Select(text => new QueuedItem(Guid.NewGuid().ToString("N"), text, enqueuedAt))
            .ToArray();
        var state = GetOrAdd(sessionId);
        List<FrontendQueueEnqueuedItem> response;
        lock (state.Gate)
        {
            // All-or-nothing: a batch that would exceed the capacity is rejected whole.
            if (state.Items.Count + items.Length > Capacity)
            {
                logger.LogDebug(
                    "Rejected a turn queue batch of {Count} item(s) for session {SessionId}: {QueuedCount} of {Capacity} already queued.",
                    items.Length,
                    sessionId,
                    state.Items.Count,
                    Capacity);
                throw new FrontendFailureException(
                    FrontendErrors.QueueFull,
                    $"The session's turn queue is full ({state.Items.Count} queued of {Capacity}).");
            }

            response = new List<FrontendQueueEnqueuedItem>(items.Length);
            foreach (var item in items)
            {
                state.Items.Add(item);
                response.Add(new FrontendQueueEnqueuedItem(item.Id, state.Items.Count));
            }

            EnsureLeaseLocked(sessionId, state);
            EnsureWorkerLocked(sessionId, state);
        }

        logger.LogDebug(
            "Enqueued {Count} turn queue item(s) for session {SessionId} ({Capacity} item capacity).",
            items.Length,
            sessionId,
            Capacity);
        PublishChange(state);
        return new FrontendQueueEnqueueResponse(response);
    }

    /// <summary>Removes one queued item. The running item is not removable: clients cancel
    /// the run instead, and the queue clears the item when the run settles.</summary>
    /// <exception cref="FrontendFailureException">The session is unresolvable, the item is
    /// running (<c>item_running</c>), or no queued item matches the id.</exception>
    public void Remove(string sessionId, string itemId)
    {
        service.EnsureSessionResolved(sessionId);
        var state = GetOrAdd(sessionId);
        var removed = false;
        lock (state.Gate)
        {
            var index = state.Items.FindIndex(item => item.Id == itemId);
            if (index >= 0)
            {
                state.Items.RemoveAt(index);
                ReleaseLeaseWhenDrainedLocked(state);
                removed = true;
            }
            else if (state.Running is { } running && running.ItemId == itemId)
            {
                throw new FrontendFailureException(
                    FrontendErrors.ItemRunning,
                    "The queue item is running; cancel the run instead.");
            }
            else
            {
                throw new FrontendFailureException(
                    FrontendErrors.NotFound,
                    $"No queued item matches '{itemId}'.");
            }
        }

        if (removed)
            logger.LogDebug("Removed turn queue item {ItemId} of session {SessionId}.", itemId, sessionId);

        PublishChange(state);
    }

    /// <summary>Clears every queued item; the running item is untouched and completes
    /// normally. Clearing an empty queue is a no-op and publishes nothing.</summary>
    public void Clear(string sessionId)
    {
        service.EnsureSessionResolved(sessionId);
        var state = GetOrAdd(sessionId);
        bool removed;
        var removedCount = 0;
        lock (state.Gate)
        {
            removed = state.Items.Count > 0;
            if (removed)
            {
                removedCount = state.Items.Count;
                state.Items.Clear();
                ReleaseLeaseWhenDrainedLocked(state);
            }
        }

        if (removed)
        {
            logger.LogDebug(
                "Cleared {Count} turn queue item(s) for session {SessionId}.",
                removedCount,
                sessionId);
            PublishChange(state);
        }
    }

    /// <summary>Gets the session's full queue state with item texts (the GET snapshot
    /// shape). Does not resolve the session — callers decide their own typed 404.</summary>
    public FrontendQueueSnapshot Snapshot(string sessionId)
    {
        return BuildSnapshot(sessionId, includeText: true).Snapshot;
    }

    /// <summary>Gets the queue state for a <c>queue.updated</c> frame: item ids only
    /// (clients map ids to cells locally), plus the state version the frame reflects.</summary>
    public (FrontendQueueSnapshot Snapshot, long Version) FrameSnapshot(string sessionId)
    {
        return BuildSnapshot(sessionId, includeText: false);
    }

    /// <summary>Waits for the session's next queue mutation, returning the version that
    /// completed the wait. A caller that already observed <paramref name="observedVersion" />
    /// gets a completed task when any newer state exists, so a socket that opens (or
    /// finishes serving a run) after a mutation still receives the current state.</summary>
    public async Task<long> WaitForChangeAsync(
        string sessionId,
        long observedVersion,
        CancellationToken cancellationToken)
    {
        var state = GetOrAdd(sessionId);
        Task wait;
        lock (state.Gate)
        {
            if (state.Version > observedVersion)
            {
                return state.Version;
            }

            wait = state.Signal.Task.WaitAsync(cancellationToken);
        }

        await wait.ConfigureAwait(false);
        lock (state.Gate)
        {
            return state.Version;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            await disposal.Task.ConfigureAwait(false);
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        SessionQueue[] states;
        lock (gate)
        {
            states = sessions.Values.ToArray();
        }

        foreach (var state in states)
        {
            WorkerRun? worker;
            lock (state.Gate)
            {
                worker = state.Worker;
            }

            if (worker is null) continue;

            try
            {
                await worker.Completion.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The worker's own catch paths handle their failures; this observes.
                logger.LogWarning(exception, "A turn queue worker faulted during disposal.");
            }
            finally
            {
                worker.Cancellation.Dispose();
            }
        }

        lock (gate)
        {
            foreach (var state in states)
            {
                // A state without a worker must not hold a pin lease either.
                ReleaseLeaseLocked(state);
            }

            sessions.Clear();
        }

        lifetime.Dispose();
        disposal.TrySetResult();
    }

    private static void ValidateItemText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FrontendFailureException(
                FrontendErrors.InvalidRequest,
                "The queue item text must not be empty.");

        // Command cells stay client-orchestrated on the turn endpoint; they never queue.
        if (MaieuticsCommandLanguage.IsCommandCell(text))
            throw new FrontendFailureException(
                FrontendErrors.InvalidRequest,
                "Commands are submitted on the turn endpoint, not the queue.");

        if (text.Length > MaxItemCharacters)
            throw new FrontendFailureException(
                FrontendErrors.InputTooLarge,
                $"The queue item text must be at most {MaxItemCharacters} characters.");
    }

    private SessionQueue GetOrAdd(string sessionId)
    {
        lock (gate)
        {
            if (sessions.TryGetValue(sessionId, out var existing)) return existing;

            var created = new SessionQueue();
            sessions[sessionId] = created;
            return created;
        }
    }

    private (FrontendQueueSnapshot Snapshot, long Version) BuildSnapshot(
        string sessionId,
        bool includeText)
    {
        var state = GetOrAdd(sessionId);
        lock (state.Gate)
        {
            var items = state.Items
                .Select(item => includeText
                    ? new FrontendQueueItem(item.Id, item.Text, item.EnqueuedAt)
                    : new FrontendQueueItem(item.Id))
                .ToArray();
            var snapshot = new FrontendQueueSnapshot(
                sessionId,
                state.Running is { } running
                    ? new FrontendQueueRunningItem(running.ItemId, running.RunId)
                    : null,
                items,
                Capacity);
            return (snapshot, state.Version);
        }
    }

    /// <summary>Signals one queue mutation. The wake signal is replaced before the
    /// previous one completes, so waiters observe at most one wake per wait and the
    /// full-state snapshot they send can only be newer, never stale.</summary>
    private static void PublishChange(SessionQueue state)
    {
        TaskCompletionSource completed;
        lock (state.Gate)
        {
            state.Version++;
            completed = state.Signal;
            state.Signal = SessionQueue.CreateSignal();
        }

        completed.TrySetResult();
    }

    private void EnsureLeaseLocked(string sessionId, SessionQueue state)
    {
        if (state.Lease is not null) return;

        if (!Guid.TryParseExact(sessionId, "N", out var parsed) || parsed == Guid.Empty) return;

        // Cooperative capacity management (ADR 0025): a session with queued work pins its
        // live registration against LRU eviction. Eviction would be survivable through
        // lazy-resume, but queued work is active interest, so the queue holds one lease
        // while the session has queue work and releases it when the queue drains.
        state.Lease = sessionManager.TryPinSession(new AgentSessionId(parsed));
    }

    private void ReleaseLeaseLocked(SessionQueue state)
    {
        if (state.Lease is not { } lease) return;

        state.Lease = null;
        lease.Dispose();
    }

    private void ReleaseLeaseWhenDrainedLocked(SessionQueue state)
    {
        if (state.Items.Count == 0 && state.Running is null) ReleaseLeaseLocked(state);
    }

    private void EnsureWorkerLocked(string sessionId, SessionQueue state)
    {
        if (state.Worker is not null || lifetime.IsCancellationRequested) return;

        // The worker task is created under the gate so a concurrent enqueue cannot start a
        // second worker for the same session; Task.Run only schedules, and the worker's
        // first action takes the same gate after this critical section releases.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completion = Task.Run(() => RunWorkerAsync(sessionId, state, cancellation));
        state.Worker = new WorkerRun(cancellation, completion);
    }

    /// <summary>
    ///     One session's serial worker: dequeue the head, submit it through
    ///     <see cref="FrontendSessionService.StartTurnAsync" />, wait for the run to
    ///     settle, repeat until the queue is empty. The run's own frames render its
    ///     outcome (run.completed / run.failed / cancel); the queue only clears the item.
    ///     Exactly one worker runs per session at a time; the next enqueue starts a new
    ///     one after this exits.
    /// </summary>
    private async Task RunWorkerAsync(string sessionId, SessionQueue state, CancellationTokenSource cancellation)
    {
        logger.LogDebug("Turn queue worker started for session {SessionId}.", sessionId);
        try
        {
            while (true)
            {
                QueuedItem item;
                lock (state.Gate)
                {
                    if (state.Items.Count == 0)
                    {
                        state.Worker = null;
                        ReleaseLeaseWhenDrainedLocked(state);
                        return;
                    }

                    item = state.Items[0];
                    state.Items.RemoveAt(0);
                }

                // Dequeue transition: the item left the pending queue. It is not visible
                // again until the submission handshake completes and it becomes running —
                // a DELETE in that window finds neither queued nor running item.
                PublishChange(state);
                logger.LogDebug(
                    "Turn queue item {ItemId} of session {SessionId} dequeued; the run is starting.",
                    item.Id,
                    sessionId);

                try
                {
                    var runId = await SubmitWithBusyRetryAsync(sessionId, item.Text, cancellation.Token)
                        .ConfigureAwait(false);
                    lock (state.Gate)
                    {
                        state.Running = new RunningItem(item.Id, runId);
                    }

                    PublishChange(state);
                    await AwaitRunSettledAsync(sessionId, runId, cancellation.Token).ConfigureAwait(false);
                    logger.LogDebug(
                        "Run {RunId} settled; turn queue item {ItemId} of session {SessionId} removed.",
                        runId,
                        item.Id,
                        sessionId);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception failure)
                {
                    // Expected per-item failure (busy retries exhausted, rejected input,
                    // provider or configuration error): typed, recoverable, and surfaced as
                    // the item's removal in the next frame; the queue keeps draining.
                    logger.LogWarning(
                        failure,
                        "The turn queue dropped item {ItemId} of session {SessionId}.",
                        item.Id,
                        sessionId);
                }
                finally
                {
                    lock (state.Gate)
                    {
                        if (state.Running is { } running && running.ItemId == item.Id) state.Running = null;

                        ReleaseLeaseWhenDrainedLocked(state);
                    }

                    PublishChange(state);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session shutdown or queue disposal (Phase 1): pending items are dropped and
            // a run already submitted keeps executing through its own run stream.
            DrainLocked(state);
            PublishChange(state);
        }
        catch (Exception failure)
        {
            // Unexpected worker fault: log it, drain the session's queue state, and let
            // the next enqueue start a fresh worker.
            logger.LogError(failure, "The turn queue worker for session {SessionId} failed.", sessionId);
            DrainLocked(state);
            PublishChange(state);
        }
        finally
        {
            logger.LogDebug("Turn queue worker stopped for session {SessionId}.", sessionId);
            cancellation.Dispose();
        }
    }

    private void DrainLocked(SessionQueue state)
    {
        lock (state.Gate)
        {
            state.Items.Clear();
            state.Running = null;
            state.Worker = null;
            ReleaseLeaseLocked(state);
        }
    }

    /// <summary>Submits one queued item, waiting out (bounded) and retrying when a direct
    /// <c>POST /turns</c> raced the dequeue and holds the single-run gate.</summary>
    private async Task<string> SubmitWithBusyRetryAsync(
        string sessionId,
        string text,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var accepted = await service
                    .StartTurnAsync(sessionId, text, cancellationToken)
                    .ConfigureAwait(false);
                return accepted.RunId;
            }
            catch (FrontendFailureException failure) when (
                failure.Code == FrontendErrors.Busy &&
                attempt <= BusyRetryLimit &&
                !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Turn submission for session {SessionId} raced the busy gate on attempt {Attempt} of {MaxRetries}; waiting for the in-flight run.",
                    sessionId,
                    attempt,
                    BusyRetryLimit);
                await WaitForInFlightRunAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Waits (bounded) for the session's in-flight run to settle so the next
    /// submission attempt can acquire the single-run gate. The racing submission
    /// announces its run immediately after reserving the gate, so the latest
    /// announcement is that run; when the latest announcement has already settled, the
    /// next attempt observes a free gate — blocking for a further announcement here
    /// would wait for this worker's own future submission, which cannot happen until
    /// this wait returns.</summary>
    private async Task WaitForInFlightRunAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(BusyWaitBudget);
        if (!Guid.TryParseExact(sessionId, "N", out var parsed) || parsed == Guid.Empty) return;

        if (!service.TryGetLatestRun(new AgentSessionId(parsed), out var target) || target is null)
        {
            // Nothing has ever been announced on the session, yet the gate is held: the
            // racing submission reserved the gate and announces within moments. Wait for
            // that announcement (a null previous never fast-paths over a later one).
            try
            {
                target = await service
                    .WaitForRunAsync(new AgentSessionId(parsed), null, budget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                return; // Budget expired; the retry observes whether the gate is free.
            }
        }

        if (target.Completion.IsCompleted) return;

        try
        {
            await target.Settled.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Budget expired or the queue is shutting down; the retry observes whether
            // the gate is free.
        }
    }

    /// <summary>Waits for one run's terminal boundary. The run's outcome is rendered by
    /// its own frames; the queue only needs the settle signal.</summary>
    private async Task AwaitRunSettledAsync(string sessionId, string runId, CancellationToken cancellationToken)
    {
        // StartTurnAsync registers the run's stream before returning, so the lookup
        // succeeds; a run that already left the bounded registry also already settled.
        if (!service.TryGetRun(runId, out var stream) || stream is null) return;

        try
        {
            // The stream's full settlement — terminal frames published and the
            // presentation scope detached — not just the run's completion: the next
            // submission attaches the session's presentation sink while wiring its run
            // stream, and that attach only succeeds once the previous stream detached.
            await stream.Settled.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Queue shutdown; the item's outcome is carried by its run's own frames.
        }
    }

    /// <summary>One session's queue state: the pending FIFO, the optional running item,
    /// the wake signal and state version, the lazy worker, and the eviction pin lease.</summary>
    private sealed class SessionQueue
    {
        public Lock Gate { get; } = new();

        public List<QueuedItem> Items { get; } = [];

        public RunningItem? Running { get; set; }

        /// <summary>Monotonic counter of published mutations; wake waiters compare it.</summary>
        public long Version { get; set; }

        public TaskCompletionSource Signal { get; set; } = CreateSignal();

        public WorkerRun? Worker { get; set; }

        public IDisposable? Lease { get; set; }

        public static TaskCompletionSource CreateSignal()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed record QueuedItem(string Id, string Text, DateTimeOffset EnqueuedAt);

    private sealed record RunningItem(string ItemId, string RunId);

    private sealed class WorkerRun(CancellationTokenSource cancellation, Task completion)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Completion { get; } = completion;
    }
}
