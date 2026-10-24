namespace Maieutics.Permissions;

/// <summary>Computes the effective permission for a scope by overlaying layers in order.
/// Semantics: denials always win over grants; within one kind, later allowlists and deny lists are
/// appended to earlier ones and <c>AllowAll</c>/<c>DenyAll</c> are preserved. Patterns with
/// variable tokens are expanded at build time against the scope's <see cref="VariableTable"/> so a
/// layer typo fails loudly before any launch instead of silently widening a grant. A pattern that
/// is empty, expands to empty, expands to <c>*</c>, or contains a comma also fails the build: the
/// first would match everything, the second two are the allow-all flag's spelling and the Deno
/// flag grammar's list separator, neither of which is expressible as a list entry.</summary>
internal static class PermissionLayerStore
{
    /// <summary>Builds the effective policy from <paramref name="layers"/> (already ordered from
    /// most general to most specific). All patterns in all layers are expanded against the
    /// variable table here; a malformed or unresolvable token throws <see cref="PermissionException"/>.
    /// A pattern that is empty (or expands to empty) also fails the build: an empty path prefix
    /// would silently match everything, so the gap must be surfaced before any launch.</summary>
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
                merged[kind] = Overlay(kind, current, rules, variables);
            }
        }

        return new EffectivePolicy(merged, variables);
    }

    private static PermissionKindRules Overlay(
        PermissionKind kind,
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
            var expanded = variables.Expand(pattern);
            if (expanded.Length == 0)
                throw new PermissionException(
                    "permission_pattern_empty",
                    $"The permission pattern '{pattern}' expands to an empty value; " +
                    "an empty pattern would match everything and is rejected.");
            // The flag-grammar rejections (a stray '*', a comma inside a pattern) exist only
            // because the renderer joins a Deno kind's list into one comma-separated flag. The
            // kernel-internal instructions domain never renders, and its derived deny overlay
            // carries real workspace file names — a file literally named '*' or 'a,b' must not
            // fail the build.
            if (PermissionKinds.IsDenoKind(kind))
            {
                if (expanded == "*")
                    throw new PermissionException(
                        "permission_pattern_invalid",
                        $"The permission pattern '{pattern}' expands to '*'; allow-all and deny-all " +
                        "are expressed by their dedicated flags, never as a list entry.");
                if (expanded.Contains(','))
                    throw new PermissionException(
                        "permission_pattern_invalid",
                        $"The permission pattern '{pattern}' expands to a value containing a comma; " +
                        "Deno renders a kind's list as one comma-separated flag, so such a pattern " +
                        "cannot be expressed and is rejected.");
            }

            return expanded;
        }
    }
}
