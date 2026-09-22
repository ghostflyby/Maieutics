using Maieutics.Agent;
using Maieutics.Execution;

namespace Maieutics.Permissions;

/// <summary>One optional layer of the permission overlay. Layers are merged in strict order
/// (built-in baseline, app-wide defaults, workspace profile, session override) and each may
/// contribute per-kind grants and denials (ADR 0018 §2). A layer is the declarative shape; the
/// effective policy is computed once per owning scope by <see cref="PermissionLayerStore"/>.</summary>
internal sealed record PermissionLayer
{
    internal static PermissionLayer Empty { get; } = new();

    internal IReadOnlyDictionary<PermissionKind, PermissionKindRules> Kinds { get; init; } =
        new Dictionary<PermissionKind, PermissionKindRules>();
}

/// <summary>Supplies the configuration-owned layers of the overlay for the current configuration
/// generation. Implemented by the configuration layer, which owns the reload pipeline: the app
/// defaults bind from <c>Maieutics:Permissions</c>, the workspace profile is the last-known-good
/// <c>permissions.json</c> beside the active maieutics.json, and session overrides live in the
/// in-memory registry (ADR 0018 Phase 5). Every member may return null when the layer
/// contributes nothing — a null layer is skipped, never composed as an empty overlay step.</summary>
internal interface IPermissionLayerSource
{
    PermissionLayer? AppDefaults { get; }

    PermissionLayer? WorkspaceProfile { get; }

    PermissionLayer? GetSessionOverride(AgentSessionId sessionId);
}

/// <summary>Computes the effective permission of one owning scope from the full acquisition path
/// (ADR 0018 Phase 5): the consumer's built-in baseline overlaid with the configuration-owned
/// layers in invariant-20 order (app-wide defaults, workspace profile, session override; denials
/// win). The layer source resolves lazily — the configuration object that implements it runs
/// binding work in its own constructor, so resolving it during composition-root construction
/// would re-enter the service provider from inside that constructor. The variable table expands
/// every pattern at build time so a malformed token fails the acquisition loudly before any
/// launch instead of silently widening a grant.</summary>
internal sealed class PermissionPolicyAcquirer(
    Func<IPermissionLayerSource> layers,
    IPermissionVariableSource variables)
{
    public EffectivePolicy Acquire(PermissionLayer baseline, AgentSessionId? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(variables);

        var source = layers();
        var composed = new List<PermissionLayer>(4) { baseline };
        if (source.AppDefaults is { } appDefaults) composed.Add(appDefaults);
        if (source.WorkspaceProfile is { } workspaceProfile) composed.Add(workspaceProfile);
        if (sessionId is { } id && source.GetSessionOverride(id) is { } sessionOverride)
            composed.Add(sessionOverride);

        return PermissionLayerStore.Build(composed, new VariableTable(variables));
    }
}

/// <summary>In-memory per-Agent-session override layers, the session override of the four-layer
/// overlay (ADR 0018 Phase 5). The registry is the seam a user-facing command surface sets
/// through; overrides never persist and die with the process. Subagent child runs inherit their
/// parent session's override through a bounded child-scope map (ADR 0030 decision 3): a spawn
/// registers the child under its parent, and lookups walk the chain, so a user-imposed session
/// deny cannot be bypassed by delegating work to a child run. The map is capped — oldest
/// registrations are evicted — because child runs leave no join callback at this layer; a late
/// launch after eviction composes the plain overlay, which is never wider than the parent's own
/// configuration-derived layers.</summary>
internal sealed class PermissionOverrideRegistry
{
    private const int ChildScopeCapacity = 256;
    private const int MaximumScopeChainDepth = 8;

    private readonly Lock gate = new();
    private readonly Dictionary<AgentSessionId, PermissionLayer> overrides = [];
    private readonly Dictionary<AgentSessionId, AgentSessionId> childScopes = [];
    private readonly Queue<AgentSessionId> childScopeOrder = new();

    public void Set(AgentSessionId sessionId, PermissionLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        lock (gate)
        {
            overrides[sessionId] = layer;
        }
    }

    public void Clear(AgentSessionId sessionId)
    {
        lock (gate)
        {
            overrides.Remove(sessionId);
        }
    }

    public PermissionLayer? TryGet(AgentSessionId sessionId)
    {
        lock (gate)
        {
            if (overrides.TryGetValue(sessionId, out var own)) return own;
            return WalkParentChainLocked(sessionId);
        }
    }

    /// <summary>Registers a spawned child run's session under its owning session, so permission
    /// lookups for the child compose the parent's override. The child's own override slot stays
    /// authoritative when set — no surface currently sets overrides for child sessions — and
    /// the chain walk is depth-capped against registration cycles.</summary>
    public void RegisterChildScope(AgentSessionId childSessionId, AgentSessionId parentSessionId)
    {
        lock (gate)
        {
            if (!childScopes.TryAdd(childSessionId, parentSessionId))
                return;
            childScopeOrder.Enqueue(childSessionId);
            while (childScopeOrder.Count > ChildScopeCapacity)
            {
                var evicted = childScopeOrder.Dequeue();
                childScopes.Remove(evicted);
            }
        }
    }

    /// <summary>Diagnostic snapshot of the live child-scope registrations, oldest first.</summary>
    internal IReadOnlyList<(AgentSessionId Child, AgentSessionId Parent)> ChildScopes
    {
        get
        {
            lock (gate)
            {
                return [.. childScopeOrder.Select(id => (id, childScopes[id]))];
            }
        }
    }

    /// <summary>Releases one child-scope registration; the spawning surface calls this when it
    /// reaps the child. Unknown registrations are ignored.</summary>
    public void ReleaseChildScope(AgentSessionId childSessionId)
    {
        lock (gate)
        {
            if (!childScopes.Remove(childSessionId)) return;
            var remaining = childScopeOrder.Where(id => !id.Equals(childSessionId)).ToArray();
            childScopeOrder.Clear();
            foreach (var id in remaining)
                childScopeOrder.Enqueue(id);
        }
    }

    private PermissionLayer? WalkParentChainLocked(AgentSessionId sessionId)
    {
        var current = sessionId;
        for (var depth = 0; depth < MaximumScopeChainDepth; depth++)
        {
            if (!childScopes.TryGetValue(current, out var parent)) return null;
            if (overrides.TryGetValue(parent, out var layer)) return layer;
            current = parent;
        }

        return null;
    }
}
