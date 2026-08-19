using Maieutics.Execution;

namespace Maieutics.Permissions;

/// <summary>One optional layer of the permission overlay. Layers are merged in strict order
/// (built-in baseline, app-wide defaults, workspace profile, session override) and each may
/// contribute per-kind grants and denials (ADR 0018 §2). A layer is the declarative shape; the
/// effective policy is computed once per owning scope by <see cref="PermissionLayerStore"/>.</summary>
internal sealed record PermissionLayer
{
    internal IReadOnlyDictionary<PermissionKind, PermissionKindRules> Kinds { get; init; } =
        new Dictionary<PermissionKind, PermissionKindRules>();
}

/// <summary>Computes the effective permission for a scope by overlaying layers in order.
/// Semantics: denials always win over grants; within one kind, later allowlists and deny lists are
/// appended to earlier ones and <c>AllowAll</c>/<c>DenyAll</c> are preserved. Patterns with
/// variable tokens are expanded at build time against the scope's <see cref="VariableTable"/> so a
/// layer typo fails loudly before any launch instead of silently widening a grant.</summary>
internal static class PermissionLayerStore
{
    /// <summary>Builds the effective policy from <paramref name="layers"/> (already ordered from
    /// most general to most specific). All patterns in all layers are expanded against the
    /// variable table here; a malformed or unresolvable token throws <see cref="PermissionException"/>.</summary>
    internal static EffectivePolicy Build(
        IReadOnlyList<PermissionLayer> layers,
        VariableTable variables)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(variables);

        var merged = new Dictionary<PermissionKind, PermissionKindRules>();
        foreach (var layer in layers)
        {
            if (layer is null) throw new ArgumentException("A permission layer cannot be null.", nameof(layers));

            foreach (var (kind, rules) in layer.Kinds)
            {
                ArgumentNullException.ThrowIfNull(rules);
                var current = merged.TryGetValue(kind, out var existing) ? existing : PermissionKindRules.Empty;
                merged[kind] = Overlay(current, rules, variables);
            }
        }

        return new EffectivePolicy(merged, variables);
    }

    private static PermissionKindRules Overlay(
        PermissionKindRules current,
        PermissionKindRules next,
        VariableTable variables)
    {
        var allow = new List<string>(current.Allow.Count + next.Allow.Count);
        allow.AddRange(current.Allow);
        allow.AddRange(next.Allow.Select(Expand));

        var deny = new List<string>(current.Deny.Count + next.Deny.Count);
        deny.AddRange(current.Deny);
        deny.AddRange(next.Deny.Select(Expand));

        return new PermissionKindRules
        {
            AllowAll = current.AllowAll || next.AllowAll,
            DenyAll = current.DenyAll || next.DenyAll,
            Allow = allow,
            Deny = deny
        };

        string Expand(string pattern)
        {
            return variables.Expand(pattern);
        }
    }
}
