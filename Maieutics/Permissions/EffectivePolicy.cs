namespace Maieutics.Permissions;

/// <summary>Immutable composed permission snapshot for one owning scope: per-kind grants and
/// denials after the layer overlay, plus the variable table used to expand them (ADR 0018 §5).
/// Process-launch modules consume only this snapshot; nothing else recomputes or re-derives the
/// effective permission of a scope. One snapshot is captured per owning scope and never changes
/// mid-operation (AGENTS.md invariant 19/20).</summary>
internal sealed record EffectivePolicy(
    IReadOnlyDictionary<PermissionKind, PermissionKindRules> Kinds,
    VariableTable Variables)
{
    /// <summary>Empty policy with no variables; renders no Deno flags and yields the default
    /// environment allowlist. The empty policy is the temporary stand-in until Phase 2/3 wire the
    /// real acquisition path (ADR 0018 Phase 0 keeps observable behavior unchanged).</summary>
    internal static EffectivePolicy Default { get; } = new(
        new Dictionary<PermissionKind, PermissionKindRules>(),
        new VariableTable(new EmptyPermissionVariableSource()));

    private sealed class EmptyPermissionVariableSource : Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }

    /// <summary>Returns the composed rules for one kind, or empty rules when no layer contributed
    /// anything for it. The empty case renders no flags, which Deno treats as deny-by-default.</summary>
    internal PermissionKindRules For(PermissionKind kind)
    {
        return Kinds.TryGetValue(kind, out var rules) ? rules : PermissionKindRules.Empty;
    }
}
