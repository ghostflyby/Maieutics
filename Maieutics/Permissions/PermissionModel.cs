namespace Maieutics.Permissions;

/// <summary>The permission kinds the store models, matching the Deno permission surface so the
/// renderer and the future process-sandbox enforcer consume the same snapshot. Kinds a process
/// sandbox cannot express (env, import) stay explicit and are enforced by their owning layer
/// (AGENTS.md invariant 23; ADR 0018 §3).</summary>
internal enum PermissionKind
{
    Read,
    Write,
    Net,
    Env,
    Run,
    Ffi,
    Sys,
    Import
}

/// <summary>Grants and denials for one permission kind within one layer. A layer may contribute
/// nothing, positive grants (<see cref="AllowAll"/> or an allowlist), denials (<see cref="DenyAll"/>
/// or a blocklist), or both. Denials always win over grants regardless of layer order; the composed
/// policy keeps both lists and the enforcer (Deno flags, broker, or terminal exec check) applies
/// deny-wins, mirroring Deno's own <c>--deny-*</c> over <c>--allow-*</c> precedence (ADR 0018 §2).</summary>
internal sealed record PermissionKindRules
{
    internal static PermissionKindRules Empty { get; } = new();

    internal bool AllowAll { get; init; }

    internal bool DenyAll { get; init; }

    internal IReadOnlyList<string> Allow { get; init; } = [];

    internal IReadOnlyList<string> Deny { get; init; } = [];
}

/// <summary>Canonical kind names as Deno spells them in flags and config permissions keys
/// (read, write, net, env, run, ffi, sys, import). One source for renderer flags and for the
/// Phase 5 <c>permissions.json</c> kind keys.</summary>
internal static class PermissionKinds
{
    internal static string GetName(PermissionKind kind)
    {
        return kind switch
        {
            PermissionKind.Read => "read",
            PermissionKind.Write => "write",
            PermissionKind.Net => "net",
            PermissionKind.Env => "env",
            PermissionKind.Run => "run",
            PermissionKind.Ffi => "ffi",
            PermissionKind.Sys => "sys",
            PermissionKind.Import => "import",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown permission kind.")
        };
    }

    /// <summary>The one matching semantics of a kind, consumed by <see cref="PermissionMatching"/>
    /// so the broker resolver and every launch/workspace gate judge identically. Filesystem path
    /// kinds and executable <c>run</c> values match on a directory boundary; <c>net</c> hosts and
    /// <c>import</c> specifiers keep Deno's prefix matching; <c>env</c> and <c>sys</c> match whole
    /// names, mirroring Deno's by-name grants so one name never covers a longer one.</summary>
    internal static PermissionMatchMode MatchMode(PermissionKind kind)
    {
        return kind switch
        {
            PermissionKind.Read or PermissionKind.Write or PermissionKind.Ffi or PermissionKind.Run
                => PermissionMatchMode.PathBoundary,
            PermissionKind.Net or PermissionKind.Import => PermissionMatchMode.Prefix,
            PermissionKind.Env or PermissionKind.Sys => PermissionMatchMode.Exact,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown permission kind.")
        };
    }

    /// <summary>Whether a kind's patterns are filesystem paths (read, write, ffi) — the kinds for
    /// which a relative pattern is only definable in a file-anchored layer (the workspace profile
    /// resolves relatives against its own directory). App-wide defaults reject relative path
    /// patterns because the app config is anchored to no directory; <c>run</c> is excluded since
    /// its values are bare command names or absolute paths.</summary>
    internal static bool IsFilePathKind(PermissionKind kind)
    {
        return kind is PermissionKind.Read or PermissionKind.Write or PermissionKind.Ffi;
    }
}

/// <summary>The matching semantics one permission kind carries everywhere it is judged.</summary>
internal enum PermissionMatchMode
{
    /// <summary>The value equals the pattern or lives inside the pattern directory (pattern
    /// followed by a separator) — read, write, ffi, and run.</summary>
    PathBoundary,

    /// <summary>The value starts with the pattern — net hosts (a host grant covers its ports) and
    /// import specifiers (a specifier covers paths under it).</summary>
    Prefix,

    /// <summary>The value equals the pattern exactly — env and sys names.</summary>
    Exact
}

/// <summary>Typed failure for permission-model violations (malformed or unresolvable variable
/// patterns). Expected configuration failures stay typed and recoverable; they never crash a
/// receive loop or a launch path (AGENTS.md: expected tool failures remain typed).</summary>
internal sealed class PermissionException : Exception
{
    internal PermissionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
