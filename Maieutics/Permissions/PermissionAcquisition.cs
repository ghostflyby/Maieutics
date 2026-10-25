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
/// through; overrides never persist and die with the process. Subagent child runs inherit by
/// snapshot (ADR 0030 decision 3): a spawn copies the parent's override at that moment into the
/// child's own slot, so a user-imposed session deny cannot be bypassed by delegating work to a
/// child run — and, symmetrically, a parent's later tightening or loosening does not reach
/// already-spawned children, matching the capture-once rule (AGENTS.md invariant 19). The
/// registration map stays bounded because child runs leave no join callback at this layer; its
/// eviction only affects the diagnostic map, never a child's captured override.</summary>
internal sealed class PermissionOverrideRegistry
{
    private const int ChildScopeCapacity = 256;

    /// <summary>Bound for captured child-snapshot override slots. Nothing releases a reaped
    /// child's slot (the Agent runtime exposes no completion callback), so the FIFO evicts the
    /// oldest snapshots. The bound assumes reaped runs have ended and never acquire again —
    /// beyond 1024 concurrent un-reaped children the oldest captured snapshot may drop, which
    /// can widen that child's policy; no surface sets a child's override directly, so an evicted
    /// entry can only be a spent or captured snapshot, never a user-authored one.</summary>
    private const int ChildSnapshotCapacity = 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<AgentSessionId, PermissionLayer> _overrides = [];
    private readonly Dictionary<AgentSessionId, AgentSessionId> _childScopes = [];
    private readonly Queue<AgentSessionId> _childScopeOrder = new();
    private readonly Queue<AgentSessionId> _childSnapshotOrder = new();

    public void Set(AgentSessionId sessionId, PermissionLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        lock (_gate)
        {
            _overrides[sessionId] = layer;
        }
    }

    public void Clear(AgentSessionId sessionId)
    {
        lock (_gate)
        {
            _overrides.Remove(sessionId);
        }
    }

    public PermissionLayer? TryGet(AgentSessionId sessionId)
    {
        lock (_gate)
        {
            return _overrides.TryGetValue(sessionId, out var own) ? own : null;
        }
    }

    /// <summary>Registers a spawned child run's session under its owning session and captures the
    /// parent's override at that moment into the child's own slot. The child's own slot stays
    /// authoritative once set — a later Set for the child (no surface currently does) or a parent
    /// change after the spawn does not re-capture.</summary>
    public void RegisterChildScope(AgentSessionId childSessionId, AgentSessionId parentSessionId)
    {
        lock (_gate)
        {
            if (_overrides.TryGetValue(parentSessionId, out var parentLayer) &&
                _overrides.TryAdd(childSessionId, parentLayer))
            {
                _childSnapshotOrder.Enqueue(childSessionId);
                while (_childSnapshotOrder.Count > ChildSnapshotCapacity)
                    _overrides.Remove(_childSnapshotOrder.Dequeue());
            }

            if (!_childScopes.TryAdd(childSessionId, parentSessionId))
                return;
            _childScopeOrder.Enqueue(childSessionId);
            while (_childScopeOrder.Count > ChildScopeCapacity)
                _childScopes.Remove(_childScopeOrder.Dequeue());
        }
    }

    /// <summary>Diagnostic snapshot of the live child-scope registrations, oldest first.</summary>
    internal IReadOnlyList<(AgentSessionId Child, AgentSessionId Parent)> ChildScopes
    {
        get
        {
            lock (_gate)
            {
                return [.. _childScopeOrder.Select(id => (id, _childScopes[id]))];
            }
        }
    }

    /// <summary>Releases one child-scope registration and the override snapshot it captured; the
    /// spawning surface calls this when it reaps the child. Unknown registrations are ignored.</summary>
    public void ReleaseChildScope(AgentSessionId childSessionId)
    {
        lock (_gate)
        {
            if (!_childScopes.Remove(childSessionId)) return;
            _overrides.Remove(childSessionId);
            var remaining = _childScopeOrder.Where(id => !id.Equals(childSessionId)).ToArray();
            _childScopeOrder.Clear();
            foreach (var id in remaining)
                _childScopeOrder.Enqueue(id);
        }
    }
}
