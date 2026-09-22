using Maieutics.Agent;

namespace Maieutics.Frontend;

/// <summary>The display-plane surface for spawned child runs (ADR 0030 decisions 7 and 8):
/// renders every child event and lifecycle notification into frontend frames and forwards
/// them live into the attached parent run's stream, so a child's activity flows on the
/// session's events socket under the child's own runId. When no run stream is attached
/// (headless turns), events fall back to a bounded per-child retained buffer — a diagnostic
/// store, not a protocol surface; the child run's own event channel stays the semantic
/// stream. The tap also maps each live child run to its owning session so the run cancel
/// endpoint can address children.</summary>
internal sealed class SubagentEventBuffer : IAgentSubagentEventSink, IAgentSubagentLifecycleSink
{
    private const int EventsPerChildCapacity = 512;
    private const int RetainedChildCapacity = 32;

    private readonly Lock gate = new();
    private readonly Dictionary<AgentSessionId, Queue<AgentEvent>> children = new();
    private readonly Dictionary<AgentRunId, AgentSessionId> parentByChildRun = new();
    private FrontendRunStream? stream;

    /// <summary>Diagnostic counter of settled notifications this tap accepted; incremented
    /// before the terminal frame is published so a missing frame with a count of one points
    /// at the stream, not at the Agent host.</summary>
    internal int SettledNotifications { get; private set; }

    /// <summary>Attaches the parent run stream that child frames forward into, replacing any
    /// previous attachment. Children never outlive their parent run (join-before-complete),
    /// so a stale attachment publishes nothing; each new run re-attaches.</summary>
    internal void Attach(FrontendRunStream attachedStream)
    {
        ArgumentNullException.ThrowIfNull(attachedStream);
        lock (gate)
        {
            stream = attachedStream;
        }
    }

    /// <inheritdoc />
    public ValueTask OnSubagentStartedAsync(
        AgentSessionId childSessionId,
        AgentRunId childRunId,
        CancellationToken cancellationToken)
    {
        FrontendRunStream? target;
        lock (gate)
        {
            target = stream;
            if (target is not null)
                parentByChildRun[childRunId] = target.SessionId;
        }

        target?.PublishChildFrame(new FrontendEventFrame(
            "run.started",
            RunId: childRunId.Value.ToString("N")));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnSubagentEventAsync(
        AgentSessionId childSessionId,
        AgentEvent agentEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        FrontendRunStream? target;
        lock (gate)
        {
            target = stream;
            // Without a live stream the events fall back to the bounded per-child retained
            // buffer; with one, the stream's own replay retention takes over.
            if (target is null)
                RetainLocked(childSessionId, agentEvent);
        }

        if (target is null)
            return ValueTask.CompletedTask;

        var rendered = AgentEventFrameMapper.Render(agentEvent);
        if (rendered is not null)
            target.PublishChildFrame(rendered);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnSubagentSettledAsync(
        AgentSessionId childSessionId,
        AgentRunId childRunId,
        AgentSubagentStatus status,
        CancellationToken cancellationToken)
    {
        FrontendRunStream? target;
        lock (gate)
        {
            target = stream;
            parentByChildRun.Remove(childRunId);
            SettledNotifications++;
        }

        // A child's terminal frame carries no sequence, mirroring the parent stream's
        // lifecycle frames; a cancelled child is a normal outcome, not a failure code.
        var terminalFrame = status == AgentSubagentStatus.Completed
            ? new FrontendEventFrame("run.completed", RunId: childRunId.Value.ToString("N"))
            : new FrontendEventFrame(
                "run.failed",
                RunId: childRunId.Value.ToString("N"),
                Code: status == AgentSubagentStatus.Cancelled ? "cancelled" : "task_failed",
                Message: $"The subagent run settled as {status}.");
        target?.PublishChildFrame(terminalFrame);
        return ValueTask.CompletedTask;
    }

    /// <summary>Resolves the owning session of one live child run, for the run cancel
    /// endpoint; null when no live child run is attached to a frontend stream.</summary>
    internal bool TryGetParentSession(AgentRunId childRunId, out AgentSessionId parentSessionId)
    {
        lock (gate)
        {
            return parentByChildRun.TryGetValue(childRunId, out parentSessionId);
        }
    }

    /// <summary>Enumerates the child sessions with retained fallback events, oldest insertion
    /// first. Children forwarded live into a run stream are not retained here.</summary>
    internal IReadOnlyList<AgentSessionId> KnownChildren
    {
        get
        {
            lock (gate)
            {
                return [.. children.Keys];
            }
        }
    }

    /// <summary>Reads one child's retained fallback events in arrival order, or an empty list
    /// when the child is unknown or its buffer was evicted.</summary>
    internal IReadOnlyList<AgentEvent> Read(AgentSessionId childSessionId)
    {
        lock (gate)
        {
            return children.TryGetValue(childSessionId, out var buffer) ? [.. buffer] : [];
        }
    }

    private void RetainLocked(AgentSessionId childSessionId, AgentEvent agentEvent)
    {
        if (!children.TryGetValue(childSessionId, out var buffer))
        {
            EvictOldestChildLocked();
            buffer = new Queue<AgentEvent>(EventsPerChildCapacity);
            children[childSessionId] = buffer;
        }

        while (buffer.Count >= EventsPerChildCapacity)
            buffer.Dequeue();
        buffer.Enqueue(agentEvent);
    }

    private void EvictOldestChildLocked()
    {
        if (children.Count < RetainedChildCapacity)
            return;

        var oldest = children.Keys.First();
        children.Remove(oldest);
    }
}
