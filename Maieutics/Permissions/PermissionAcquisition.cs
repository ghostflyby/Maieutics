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
/// through; overrides never persist and die with the process.</summary>
internal sealed class PermissionOverrideRegistry
{
    private readonly Lock gate = new();
    private readonly Dictionary<AgentSessionId, PermissionLayer> overrides = [];

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
            return overrides.TryGetValue(sessionId, out var layer) ? layer : null;
        }
    }
}
