using Maieutics.Agent;

namespace Maieutics.Frontend;

/// <summary>The display-plane sink for spawned child runs: a bounded, per-child retained log
/// of the events the host pump forwards. This is a retained diagnostic store, not a protocol
/// surface — the child run's own event channel stays the semantic stream, and retention bounds
/// follow the repository's bounded-retained-log rule rather than the protocol backpressure
/// rule. Phase 2b's frontend reads this buffer to render and replay child activity; until then
/// it exists so subagent spawning can be enabled without stalling children at channel
/// capacity.</summary>
internal sealed class SubagentEventBuffer : IAgentSubagentEventSink
{
    private const int EventsPerChildCapacity = 512;
    private const int RetainedChildCapacity = 32;

    private readonly Lock gate = new();
    private readonly Dictionary<AgentSessionId, Queue<AgentEvent>> children = new();

    public ValueTask OnSubagentEventAsync(
        AgentSessionId childSessionId,
        AgentEvent agentEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        lock (gate)
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

        return ValueTask.CompletedTask;
    }

    /// <summary>Enumerates the child sessions with retained events, oldest insertion first.</summary>
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

    /// <summary>Reads one child's retained events in arrival order, or an empty list when the
    /// child is unknown or its buffer was evicted.</summary>
    internal IReadOnlyList<AgentEvent> Read(AgentSessionId childSessionId)
    {
        lock (gate)
        {
            return children.TryGetValue(childSessionId, out var buffer) ? [.. buffer] : [];
        }
    }

    private void EvictOldestChildLocked()
    {
        if (children.Count < RetainedChildCapacity)
            return;

        var oldest = children.Keys.First();
        children.Remove(oldest);
    }
}
