using System.Collections.Immutable;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>Configures subagent spawning for the runs of one Agent session.</summary>
public sealed record AgentSubagentOptions
{
    /// <summary>Gets how many levels of child runs a run may spawn below this session. Zero
    /// disables subagent spawning: no spawn context is attached to tool calls.</summary>
    public int MaxDepth { get; init; }

    /// <summary>Gets how many child runs one parent run may start in total. Children are
    /// counted for the whole run, not concurrently, and the count only resets when the run
    /// terminates.</summary>
    public int MaxChildrenPerTurn { get; init; } = 4;

    /// <summary>Gets the default wall-clock budget of one spawned child run, or zero when a
    /// child is unlimited unless its spawn spec overrides the duration.</summary>
    public TimeSpan MaxChildTurnDuration { get; init; } = TimeSpan.Zero;

    /// <summary>Gets how many detached (session-scoped) children one session may host in its
    /// lifetime. Detached children have no parent run — an orchestration surface outside the
    /// agent owns their lifetime — and settled ones stay addressable (their result is the
    /// product), so they keep counting toward this cap; it is the retained-state bound that
    /// replaces the per-turn budget.</summary>
    public int MaxDetachedChildren { get; init; } = 8;

    /// <summary>Gets the receiver of every spawned child run's events. Required while
    /// <see cref="MaxDepth" /> is positive: a child run's bounded event stream must be consumed
    /// from the moment the run starts, so subagents cannot be enabled without an event consumer.</summary>
    public IAgentSubagentEventSink? EventSink { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDepth);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxChildrenPerTurn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDetachedChildren, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxChildTurnDuration, TimeSpan.Zero);
        if (MaxDepth > 0 && EventSink is null)
            throw new InvalidOperationException(
                "Subagents with a positive depth require an event sink: a spawned run's bounded " +
                "event stream must stay consumed, or the child run stalls at channel capacity.");
    }
}

/// <summary>Receives the ordered events of spawned child runs. The host is the single consumer
/// of each child run's event stream and forwards every event here before the child can proceed;
/// a failing sink therefore terminates the child run instead of stalling it at channel capacity.</summary>
public interface IAgentSubagentEventSink
{
    /// <summary>Receives one event of one spawned child run, in the child's run-local sequence order.</summary>
    /// <param name="childSessionId">The session identity of the child run, distinct per spawned run.</param>
    /// <param name="agentEvent">The child run event.</param>
    /// <param name="cancellationToken">Cancels the forwarding; cancellation terminates the child run.</param>
    ValueTask OnSubagentEventAsync(
        AgentSessionId childSessionId,
        AgentEvent agentEvent,
        CancellationToken cancellationToken);
}

/// <summary>Optional lifecycle notifications for sinks that render child runs on their own
/// surfaces. The host invokes the members only when the configured event sink also implements
/// this interface. Ordering is the display-plane contract: <see cref="OnSubagentStartedAsync" />
/// fires before the child's first forwarded event, and <see cref="OnSubagentSettledAsync" />
/// fires strictly after the last forwarded event of a child that settled normally.</summary>
public interface IAgentSubagentLifecycleSink
{
    /// <summary>Notifies that a child run started and its event stream is about to flow.</summary>
    /// <param name="childSessionId">The child run's session identity.</param>
    /// <param name="childRunId">The child run identifier.</param>
    /// <param name="cancellationToken">Cancels the notification; the child run is unaffected.</param>
    ValueTask OnSubagentStartedAsync(
        AgentSessionId childSessionId,
        AgentRunId childRunId,
        CancellationToken cancellationToken);

    /// <summary>Notifies that a child run settled. Fired after the child's event stream has
    /// drained, so a rendering surface can close its view on the terminal notification.</summary>
    /// <param name="childSessionId">The child run's session identity.</param>
    /// <param name="childRunId">The child run identifier.</param>
    /// <param name="status">The child's terminal status.</param>
    /// <param name="cancellationToken">Cancels the notification; the child run is unaffected.</param>
    ValueTask OnSubagentSettledAsync(
        AgentSessionId childSessionId,
        AgentRunId childRunId,
        AgentSubagentStatus status,
        CancellationToken cancellationToken);
}

/// <summary>Describes one child run to spawn. The child inherits the parent run's model client
/// and limits and receives only the explicitly provided instructions and tool allowlist.</summary>
public sealed record AgentSubagentSpec
{
    /// <summary>Gets the system instructions of the child run, or null when the child runs
    /// without instructions. The parent's system prompt is deliberately not inherited.</summary>
    public string? Instructions { get; init; }

    /// <summary>Gets the composed task input of the child run.</summary>
    public required string Input { get; init; }

    /// <summary>Gets the parent-registered tool names the child may use, or null for the
    /// parent's complete tool registry. An empty list means the child runs without tools.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Gets the wall-clock budget of the child run, overriding
    /// <see cref="AgentSubagentOptions.MaxChildTurnDuration" /> when set.</summary>
    public TimeSpan? MaxTurnDuration { get; init; }
}

/// <summary>Starts child runs owned by one parent run. Attached to Agent tool arguments next to
/// <see cref="AgentToolContext" /> when subagents are enabled for the session.</summary>
public interface IAgentSubagentSpawner
{
    /// <summary>Gets the owning session identifier.</summary>
    AgentSessionId SessionId { get; }

    /// <summary>Gets the parent run that owns every spawned child.</summary>
    AgentRunId ParentRunId { get; }

    /// <summary>Starts one child run as a complete Agent run with its own profile lease,
    /// event sequence, and tool registry subset.</summary>
    /// <param name="spec">The composed child run description.</param>
    /// <param name="cancellationToken">
    ///     Cancels starting and is linked into the child's lifetime: cancelling this token
    ///     cancels the child run. The token is typically the spawning tool call's token, so the
    ///     child dies with the parent turn's budget or cancellation.
    /// </param>
    /// <param name="onChildSessionCreated">
    ///     Invoked with the child session identity after the child session exists but before
    ///     its turn starts — the hook for the spawning surface to register permission scope
    ///     so the child never executes outside its inherited policy.
    /// </param>
    /// <returns>A handle whose <see cref="IAgentSubagentHandle.Completion" /> never faults;
    /// terminal failure and cancellation map into the result status.</returns>
    /// <exception cref="AgentSubagentBudgetExceededException">The parent run exhausted its
    /// per-run child budget.</exception>
    ValueTask<IAgentSubagentHandle> StartChildAsync(
        AgentSubagentSpec spec,
        CancellationToken cancellationToken = default,
        Action<AgentSessionId>? onChildSessionCreated = null);

    /// <summary>Waits for one child of the current parent run to reach a terminal state and
    /// returns its result. Waiting on a child that already terminated returns immediately.
    /// Only children of the current parent run are addressable: the parent run's terminal
    /// path joins and forgets its children, and later runs refetch reports from the parent
    /// transcript instead.</summary>
    /// <param name="childRunId">The run identifier the spawn returned.</param>
    /// <param name="timeout">
    ///     How long to wait for the child's terminal state; <see cref="Timeout.InfiniteTimeSpan" />
    ///     waits unboundedly within the parent turn's own budget.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait, leaving the child run untouched.</param>
    /// <returns>The child's terminal result.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive or infinite.</exception>
    /// <exception cref="AgentSubagentNotFoundException">
    ///     No child of the current parent run matches the identifier.
    /// </exception>
    /// <exception cref="AgentSubagentWaitTimeoutException">
    ///     The child did not reach a terminal state within the timeout.
    /// </exception>
    ValueTask<AgentSubagentResult> WaitChildAsync(
        AgentRunId childRunId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels one child of the current parent run and waits for its termination.
    /// Cancelling an already-terminated child returns its result unchanged, so the operation
    /// is idempotent.</summary>
    /// <param name="childRunId">The run identifier the spawn returned.</param>
    /// <param name="cancellationToken">Cancels waiting for the child's termination; the
    /// cancellation request itself stays issued.</param>
    /// <returns>The child's terminal result.</returns>
    /// <exception cref="AgentSubagentNotFoundException">
    ///     No child of the current parent run matches the identifier.
    /// </exception>
    ValueTask<AgentSubagentResult> CancelChildAsync(
        AgentRunId childRunId,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides access to the subagent spawn context of the current Agent tool call.</summary>
public static class AgentSubagentContext
{
    /// <summary>Gets the spawn context attached to the Agent tool arguments.</summary>
    /// <param name="arguments">The function arguments of the running tool call.</param>
    /// <exception cref="InvalidOperationException">
    ///     The function is not running inside a Maieutics Agent tool call with subagents enabled.
    /// </exception>
    public static IAgentSubagentSpawner GetRequired(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Context?.TryGetValue(typeof(IAgentSubagentSpawner), out var value) == true &&
            value is IAgentSubagentSpawner spawner)
            return spawner;

        throw new InvalidOperationException(
            "The AI function is not running inside a Maieutics Agent tool call with subagents enabled.");
    }
}

/// <summary>One started child run and its terminal result.</summary>
public interface IAgentSubagentHandle
{
    /// <summary>Gets the child session identity.</summary>
    AgentSessionId SessionId { get; }

    /// <summary>Gets the child run identifier.</summary>
    AgentRunId RunId { get; }

    /// <summary>Gets the child run's terminal result. The task never faults: provider failure
    /// and cancellation map into <see cref="AgentSubagentResult.Status" />.</summary>
    Task<AgentSubagentResult> Completion { get; }
}

/// <summary>The terminal status of one spawned child run.</summary>
public enum AgentSubagentStatus
{
    /// <summary>The child run committed a complete turn.</summary>
    Completed,

    /// <summary>The child run was cancelled by its linked lifetime, its wall-clock budget, or
    /// a failing event sink.</summary>
    Cancelled,

    /// <summary>The child run failed without committing a turn.</summary>
    Failed
}

/// <summary>The terminal result of one spawned child run.</summary>
public sealed record AgentSubagentResult
{
    /// <summary>Initializes a subagent result with its terminal status.</summary>
    /// <param name="sessionId">The child session identity.</param>
    /// <param name="runId">The child run identity.</param>
    /// <param name="status">The terminal status of the child run.</param>
    /// <param name="report">The final assistant text of a completed child run.</param>
    /// <param name="truncated">Whether the child turn exhausted its budget before a validated answer.</param>
    /// <param name="usage">The token usage the provider reported for the child run.</param>
    /// <param name="failure">
    ///     The raw terminal exception for trusted callers. It must never be forwarded to
    ///     model-visible output verbatim; render a typed, safe envelope at the tool boundary.
    /// </param>
    public AgentSubagentResult(
        AgentSessionId sessionId,
        AgentRunId runId,
        AgentSubagentStatus status,
        string? report,
        bool truncated,
        UsageDetails? usage,
        Exception? failure)
    {
        if (sessionId.Value == Guid.Empty)
            throw new ArgumentException("Agent session identifiers cannot be empty.", nameof(sessionId));
        if (runId.Value == Guid.Empty)
            throw new ArgumentException("Agent run identifiers cannot be empty.", nameof(runId));

        SessionId = sessionId;
        RunId = runId;
        Status = status;
        Report = report;
        Truncated = truncated;
        Usage = usage;
        Failure = failure;
    }

    /// <summary>Gets the child session identity.</summary>
    public AgentSessionId SessionId { get; }

    /// <summary>Gets the child run identity.</summary>
    public AgentRunId RunId { get; }

    /// <summary>Gets the terminal status of the child run.</summary>
    public AgentSubagentStatus Status { get; }

    /// <summary>Gets the final assistant text of a completed child run.</summary>
    public string? Report { get; }

    /// <summary>Gets whether the child turn exhausted its budget before a validated answer.</summary>
    public bool Truncated { get; }

    /// <summary>Gets the token usage the provider reported for the child run.</summary>
    public UsageDetails? Usage { get; }

    /// <summary>Gets the raw terminal exception of a failed or cancelled child run.</summary>
    public Exception? Failure { get; }
}

/// <summary>Owns the child runs of one Agent session. Children are tracked per parent run and
/// terminated deterministically when the parent run terminates; the host holds no state between
/// runs. The session-level owner creates one host and derives per-run spawners from it.</summary>
internal sealed class AgentSubagentHost(
    AgentSessionId sessionId,
    IAgentRunProfileProvider profileProvider,
    IAgentObjectStore? objectStore)
{
    private readonly IAgentRunProfileProvider profileProvider = profileProvider;

    private readonly IAgentObjectStore? objectStore = objectStore;

    private readonly Lock gate = new();
    private readonly Dictionary<AgentRunId, List<ChildRecord>> childrenByParent = [];
    private readonly List<ChildRecord> detached = [];
    private int detachedReserved;

    /// <summary>Starts a detached (session-scoped) child run for an orchestration surface
    /// outside any agent run — the control channel's model-orchestration endpoints. A detached
    /// child has no parent run: nothing joins it at a turn boundary, its lifetime is the
    /// process or an explicit cancel, and <see cref="MaxDetachedChildren"/> bounds how many it
    /// hosts. It is otherwise a complete child run — own profile lease, event sequence, and
    /// tool registry — addressable through the same registry and task plane.</summary>
    /// <param name="spec">The composed child run description.</param>
    /// <param name="baseOptions">
    ///     The session-level options the child derives from: the subagent configuration
    ///     (depth, budgets, sink) must be present, and the child's effective options are this
    ///     record narrowed by the spec.
    /// </param>
    /// <param name="availableTools">
    ///     The tool registry the child's allowlist resolves against — for an orchestration
    ///     surface, the owning process's production tool set.
    /// </param>
    /// <param name="cancellationToken">Cancels starting; the child's lifetime is session-scoped
    /// and independent afterwards.</param>
    /// <exception cref="AgentSubagentBudgetExceededException">The session hosts its detached
    /// cap of children.</exception>
    public async ValueTask<IAgentSubagentHandle> StartDetachedChildAsync(
        AgentSubagentSpec spec,
        AgentSessionOptions baseOptions,
        IReadOnlyList<AIFunction> availableTools,
        CancellationToken cancellationToken = default,
        Action<AgentSessionId>? onChildSessionCreated = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Input);
        if (spec.MaxTurnDuration is { } duration && duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(spec), duration, "The subagent turn duration cannot be negative.");

        var subagents = baseOptions.Subagents ??
            throw new InvalidOperationException("Subagents are not configured for this session's runs.");

        // Detached spawns arrive on concurrent control-channel requests, so the cap must be
        // reserved atomically before the await and rolled back if the spawn fails.
        var reservation = Interlocked.Increment(ref detachedReserved);
        if (reservation > subagents.MaxDetachedChildren)
        {
            Interlocked.Decrement(ref detachedReserved);
            throw new AgentSubagentBudgetExceededException(
                nameof(AgentSubagentOptions.MaxDetachedChildren), subagents.MaxDetachedChildren);
        }

        var eventSink = subagents.EventSink ??
            throw new InvalidOperationException(
                "Subagents with a positive depth require an event sink.");
        var linkedCts = new CancellationTokenSource();
        AgentSession childSession;
        IAgentRun childRun;
        try
        {
            var childOptions = (baseOptions with
            {
                SystemPrompt = spec.Instructions,
                MaxTurnDuration = spec.MaxTurnDuration ?? subagents.MaxChildTurnDuration,
                Subagents = subagents with { MaxDepth = subagents.MaxDepth - 1 }
            });
            childSession = new AgentSession(
                new OptionsOverrideProfileProvider(
                    profileProvider, childOptions, ResolveTools(spec.Tools, availableTools)),
                objectStore: objectStore);
            onChildSessionCreated?.Invoke(childSession.Id);
            childRun = await childSession
                .StartTurnAsync(AgentTurn.FromText(spec.Input), linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            linkedCts.Dispose();
            throw;
        }

        var record = new ChildRecord(childSession.Id, childRun, linkedCts);
        lock (gate)
        {
            detached.Add(record);
        }

        record.Start(eventSink);
        return record;
    }

    /// <summary>Waits for one detached child to reach a terminal state and returns its result.</summary>
    /// <exception cref="AgentSubagentNotFoundException">No detached child matches the identifier.</exception>
    /// <exception cref="AgentSubagentWaitTimeoutException">The child stayed unsettled past the timeout.</exception>
    public async ValueTask<AgentSubagentResult> WaitDetachedAsync(
        AgentRunId childRunId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(timeout), timeout, "The subagent wait timeout must be positive or infinite.");

        var record = FindDetached(childRunId) ??
            throw new AgentSubagentNotFoundException(childRunId);
        try
        {
            return await record.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new AgentSubagentWaitTimeoutException(childRunId, timeout, exception);
        }
    }

    /// <summary>Cancels one detached child and returns its terminal result; idempotent on an
    /// already-terminal child.</summary>
    /// <exception cref="AgentSubagentNotFoundException">No detached child matches the identifier.</exception>
    public async ValueTask<AgentSubagentResult> CancelDetachedAsync(
        AgentRunId childRunId,
        CancellationToken cancellationToken = default)
    {
        var record = FindDetached(childRunId) ??
            throw new AgentSubagentNotFoundException(childRunId);
        if (!record.Completion.IsCompleted)
            await record.Run.CancelAsync(cancellationToken).ConfigureAwait(false);
        return await record.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits for one child of this session — run-owned or detached — to reach a
    /// terminal state and returns its result. The session-wide addressing form the control
    /// channel's orchestration endpoints use (ADR 0031).</summary>
    /// <exception cref="AgentSubagentNotFoundException">No live child matches the identifier.</exception>
    /// <exception cref="AgentSubagentWaitTimeoutException">The child stayed unsettled past the timeout.</exception>
    public async ValueTask<AgentSubagentResult> WaitChildByIdAsync(
        AgentRunId childRunId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(timeout), timeout, "The subagent wait timeout must be positive or infinite.");

        var record = FindChild(childRunId) ??
            throw new AgentSubagentNotFoundException(childRunId);
        try
        {
            return await record.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new AgentSubagentWaitTimeoutException(childRunId, timeout, exception);
        }
    }

    /// <summary>Cancels one child of this session — run-owned or detached — and returns its
    /// terminal result; idempotent on an already-terminal child.</summary>
    /// <exception cref="AgentSubagentNotFoundException">No live child matches the identifier.</exception>
    public async ValueTask<AgentSubagentResult> CancelChildByIdAsync(
        AgentRunId childRunId,
        CancellationToken cancellationToken = default)
    {
        var record = FindChild(childRunId) ??
            throw new AgentSubagentNotFoundException(childRunId);
        if (!record.Completion.IsCompleted)
            await record.Run.CancelAsync(cancellationToken).ConfigureAwait(false);
        return await record.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private ChildRecord? FindDetached(AgentRunId childRunId)
    {
        lock (gate)
        {
            return detached.FirstOrDefault(record => record.RunId == childRunId);
        }
    }

    private static IReadOnlyList<AIFunction> ResolveTools(
        IReadOnlyList<string>? allowlist,
        IReadOnlyList<AIFunction> available)
    {
        if (allowlist is null)
            return available.ToArray();

        var byName = available
            .GroupBy(static function => function.Name)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var resolved = new List<AIFunction>(allowlist.Count);
        foreach (var name in allowlist)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!byName.TryGetValue(name, out var function))
                throw new ArgumentException(
                    $"The subagent tool allowlist names '{name}', which is not registered on the owning scope.",
                    nameof(allowlist));
            resolved.Add(function);
        }

        return resolved;
    }

    /// <summary>Creates the per-run spawn context attached to the run's tool calls.</summary>
    public Spawner CreateSpawner(
        AgentRunId parentRunId,
        ImmutableDictionary<string, AIFunction> parentTools,
        AgentSessionOptions parentOptions)
    {
        return new Spawner(this, sessionId, parentRunId, parentTools, parentOptions);
    }

    /// <summary>Looks up a child of one parent run by its run identifier, or null when the
    /// run has no such child.</summary>
    public ChildRecord? FindChild(AgentRunId parentRunId, AgentRunId childRunId)
    {
        lock (gate)
        {
            return childrenByParent.TryGetValue(parentRunId, out var children)
                ? children.FirstOrDefault(record => record.RunId == childRunId)
                : null;
        }
    }

    /// <summary>Looks up a child of this session by its run identifier regardless of which
    /// parent run spawned it, or null when no live child matches. This is the addressing form
    /// of the task plane, whose URIs carry the session and the child run but no parent run.</summary>
    public ChildRecord? FindChild(AgentRunId childRunId)
    {
        lock (gate)
        {
            return childrenByParent.Values
                .SelectMany(static children => children)
                .Concat(detached)
                .FirstOrDefault(record => record.RunId == childRunId);
        }
    }

    /// <summary>Enumerates every live child of this session, across all parent runs plus the
    /// detached (session-scoped) children. The enumeration snapshots under the gate; callers
    /// see records whose terminal state may already have settled.</summary>
    public IReadOnlyList<ChildRecord> ListChildren()
    {
        lock (gate)
        {
            return childrenByParent.Values
                .SelectMany(static children => children)
                .Concat(detached)
                .ToArray();
        }
    }

    /// <summary>Terminates and observes every child of one parent run. Children the parent turn
    /// left unsettled are cancelled before joining, so the parent run's terminal path always
    /// observes their termination; the primary terminal cause of the parent run is never masked.</summary>
    public async ValueTask TerminateChildrenAsync(AgentRunId parentRunId)
    {
        ChildRecord[] records;
        lock (gate)
        {
            if (!childrenByParent.Remove(parentRunId, out var pending))
                return;
            records = [.. pending];
        }

        foreach (var record in records)
        {
            if (record.Completion.IsCompleted)
                continue;
            await record.Run.CancelAsync(CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var record in records)
        {
            try
            {
                await record.Completion.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The mapping task maps every terminal cause into a result and never faults.
            }

            try
            {
                await record.PumpTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Observed: a failing sink already terminated the child run, whose result
                // surfaces the cancellation to the spawning tool.
            }

            record.LinkedCts.Dispose();
        }
    }

    /// <summary>The per-run spawn context handed to tool calls. Spawning tool calls of one run
    /// are serial (the runtime invokes tools serially), so the budget check and registration
    /// cannot interleave for one parent run.</summary>
    internal sealed class Spawner(
        AgentSubagentHost host,
        AgentSessionId sessionId,
        AgentRunId parentRunId,
        ImmutableDictionary<string, AIFunction> parentTools,
        AgentSessionOptions parentOptions) : IAgentSubagentSpawner
    {
        public AgentSessionId SessionId => sessionId;

        public AgentRunId ParentRunId => parentRunId;

        public async ValueTask<IAgentSubagentHandle> StartChildAsync(
            AgentSubagentSpec spec,
            CancellationToken cancellationToken = default,
            Action<AgentSessionId>? onChildSessionCreated = null)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentException.ThrowIfNullOrWhiteSpace(spec.Input);
            if (spec.MaxTurnDuration is { } duration && duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(
                    nameof(spec), duration, "The subagent turn duration cannot be negative.");

            var subagents = parentOptions.Subagents ??
                throw new InvalidOperationException(
                    "Subagents are not configured for this session's runs.");

            List<ChildRecord> children;
            lock (host.gate)
            {
                children = host.childrenByParent.TryGetValue(parentRunId, out var existing)
                    ? existing
                    : host.childrenByParent[parentRunId] = [];
                if (children.Count >= subagents.MaxChildrenPerTurn)
                    throw new AgentSubagentBudgetExceededException(
                        nameof(AgentSubagentOptions.MaxChildrenPerTurn), subagents.MaxChildrenPerTurn);
            }

            var eventSink = subagents.EventSink ??
                throw new InvalidOperationException(
                    "Subagents with a positive depth require an event sink.");
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            AgentSession childSession;
            IAgentRun childRun;
            try
            {
                var childOptions = (parentOptions with
                {
                    SystemPrompt = spec.Instructions,
                    MaxTurnDuration = spec.MaxTurnDuration ?? subagents.MaxChildTurnDuration,
                    Subagents = subagents with { MaxDepth = subagents.MaxDepth - 1 }
                });
                childSession = new AgentSession(
                    new OptionsOverrideProfileProvider(
                        host.profileProvider, childOptions, ResolveTools(spec.Tools)),
                    objectStore: host.objectStore);
                onChildSessionCreated?.Invoke(childSession.Id);
                childRun = await childSession
                    .StartTurnAsync(AgentTurn.FromText(spec.Input), linkedCts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                linkedCts.Dispose();
                throw;
            }

            var record = new ChildRecord(childSession.Id, childRun, linkedCts);
            lock (host.gate)
            {
                children.Add(record);
            }

            record.Start(eventSink);
            return record;
        }

        public async ValueTask<AgentSubagentResult> WaitChildAsync(
            AgentRunId childRunId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(
                    nameof(timeout), timeout, "The subagent wait timeout must be positive or infinite.");

            var record = host.FindChild(parentRunId, childRunId) ??
                throw new AgentSubagentNotFoundException(childRunId);
            try
            {
                return await record.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new AgentSubagentWaitTimeoutException(childRunId, timeout, exception);
            }
        }

        public async ValueTask<AgentSubagentResult> CancelChildAsync(
            AgentRunId childRunId,
            CancellationToken cancellationToken = default)
        {
            var record = host.FindChild(parentRunId, childRunId) ??
                throw new AgentSubagentNotFoundException(childRunId);
            if (!record.Completion.IsCompleted)
                await record.Run.CancelAsync(cancellationToken).ConfigureAwait(false);
            return await record.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private IReadOnlyList<AIFunction> ResolveTools(IReadOnlyList<string>? allowlist)
        {
            if (allowlist is null)
                return parentTools.Values.ToArray();

            var resolved = new List<AIFunction>(allowlist.Count);
            foreach (var name in allowlist)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                if (!parentTools.TryGetValue(name, out var function))
                    throw new ArgumentException(
                        $"The subagent tool allowlist names '{name}', which is not registered on the parent run.",
                        nameof(AgentSubagentSpec.Tools));
                resolved.Add(function);
            }

            return resolved;
        }
    }

    internal sealed class ChildRecord(
        AgentSessionId childSessionId,
        IAgentRun run,
        CancellationTokenSource linkedCts) : IAgentSubagentHandle
    {
        private readonly TaskCompletionSource<AgentSubagentResult> completionSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentSessionId SessionId { get; } = childSessionId;

        public AgentRunId RunId { get; } = run.Id;

        public IAgentRun Run { get; } = run;

        public CancellationTokenSource LinkedCts { get; } = linkedCts;

        public Task<AgentSubagentResult> Completion => completionSource.Task;

        public Task MapperTask { get; private set; } = Task.CompletedTask;

        public Task PumpTask { get; private set; } = Task.CompletedTask;

        public void Start(IAgentSubagentEventSink sink)
        {
            MapperTask = MapCompletionAsync();
            PumpTask = PumpEventsAsync(sink);
        }

        private async Task MapCompletionAsync()
        {
            AgentSubagentResult result;
            try
            {
                var runResult = await Run.Completion.ConfigureAwait(false);
                result = new AgentSubagentResult(
                    SessionId,
                    RunId,
                    AgentSubagentStatus.Completed,
                    ExtractReport(runResult.AssistantMessage),
                    runResult.Truncated,
                    runResult.Usage,
                    null);
            }
            catch (OperationCanceledException exception)
            {
                result = new AgentSubagentResult(
                    SessionId, RunId, AgentSubagentStatus.Cancelled, null, false, null, exception);
            }
            catch (Exception exception)
            {
                result = new AgentSubagentResult(
                    SessionId, RunId, AgentSubagentStatus.Failed, null, false, null, exception);
            }

            completionSource.TrySetResult(result);
        }

        private async Task PumpEventsAsync(IAgentSubagentEventSink sink)
        {
            var lifecycle = sink as IAgentSubagentLifecycleSink;
            try
            {
                if (lifecycle is not null)
                    await lifecycle
                        .OnSubagentStartedAsync(SessionId, RunId, LinkedCts.Token)
                        .ConfigureAwait(false);

                await foreach (var agentEvent in Run.Events
                                   .WithCancellation(LinkedCts.Token)
                                   .ConfigureAwait(false))
                {
                    await sink.OnSubagentEventAsync(SessionId, agentEvent, LinkedCts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The host is the child run's only event consumer: a failing sink would stall
                // the child at channel capacity, so its failure terminates the child run. The
                // mapped result surfaces the cancellation to the spawning tool.
                await Run.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!Run.Completion.IsCompleted)
            {
                // The linked lifetime fired while the child was still running — the spawning
                // tool call's budget or cancellation. A run never terminates from a caller
                // token alone, so the pump cancels the child run explicitly.
                await Run.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (lifecycle is not null)
            {
                // Fired after the event stream drained, so a surface can close its view on the
                // terminal notification. A notification failure escapes only into the join's
                // observation path (the pump task is awaited there); the child is already
                // settled and its result observed either way.
                var result = await Completion.ConfigureAwait(false);
                await lifecycle
                    .OnSubagentSettledAsync(SessionId, RunId, result.Status, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        private static string ExtractReport(ChatMessage assistantMessage)
        {
            return string.Concat(
                assistantMessage.Contents.OfType<TextContent>().Select(static content => content.Text));
        }
    }

    /// <summary>Wraps the parent's profile provider so a child run acquires its own lease —
    /// its own client and generation lifetime — while running under the derived child options
    /// and the resolved child tool subset instead of whatever the provider's profile carries.
    /// The child session is constructed without fixed tools: the overridden profile is the
    /// single source of its registry, so no name can collide.</summary>
    private sealed class OptionsOverrideProfileProvider(
        IAgentRunProfileProvider inner,
        AgentSessionOptions options,
        IReadOnlyList<AIFunction> tools) : IAgentRunProfileProvider
    {
        public async Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            var lease = await inner.AcquireAsync(cancellationToken).ConfigureAwait(false) ??
                        throw new InvalidOperationException("The Agent run profile provider returned a null lease.");
            var profile = lease.Profile;
            var adjusted = new AgentRunProfile(
                profile.ChatClient,
                options,
                profile.ModelIdentity,
                profile.Capabilities,
                profile.HostedCapabilities,
                tools,
                profile.HostedTools);
            return new OverrideLease(lease, adjusted);
        }

        private sealed class OverrideLease(IAgentRunProfileLease inner, AgentRunProfile profile)
            : IAgentRunProfileLease
        {
            public AgentRunProfile Profile { get; } = profile;

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
