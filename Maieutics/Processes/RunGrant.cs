using Maieutics.Permissions;

namespace Maieutics.Processes;

/// <summary>The single <c>run</c> launch gate for general process starts: terminal PTY children
/// (ADR 0018 §7) and MCP server children judge identically because they call this predicate
/// instead of carrying private copies of the rule, and a future launch path joins by calling it
/// too. Denials always win (AGENTS.md invariant 20): <c>DenyAll</c> and any deny match reject
/// before grants are consulted, so a deny entry subtracts from whatever the grants would otherwise
/// allow. An explicit allowlist restricts to the granted commands and directory prefixes (path
/// boundary — a grant of a directory never admits a sibling name); with no allowlist the kind
/// stays at its unrestricted default minus the denials already applied (ADR 0018 §7 decision 2).
/// The unrestricted default is the one deliberate difference from the broker's deny-by-default
/// for the same kind: general starts launch user-configured executables, while the broker judges
/// model-controlled code.</summary>
internal static class RunGrant
{
    /// <summary>Whether the effective policy permits starting <paramref name="command"/>. Deny
    /// rules are judged through the shared kind matcher, so the gate and the broker resolve the
    /// same pattern identically.</summary>
    internal static bool Allows(EffectivePolicy policy, string command)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var run = policy.For(PermissionKind.Run);
        if (run.DenyAll)
            return false;
        if (PermissionMatching.MatchesAny(PermissionKind.Run, run.Deny, command))
            return false;
        if (run.AllowAll)
            return true;
        if (run.Allow.Count == 0)
            return true;

        return PermissionMatching.MatchesAny(PermissionKind.Run, run.Allow, command);
    }
}
