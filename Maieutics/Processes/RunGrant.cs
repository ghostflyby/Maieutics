using Maieutics.Permissions;

namespace Maieutics.Processes;

/// <summary>The single <c>run</c> launch gate for general process starts: terminal PTY children
/// (ADR 0018 §7) and MCP server children judge identically because they call this predicate
/// instead of carrying private copies of the rule, and a future launch path joins by calling it
/// too. The <c>run</c> default is unrestricted (ADR 0018 §7 decision 2): the gate engages only
/// when a scope's policy contributes an explicit allowlist or a denial, and an explicit allowlist
/// admits the command itself or a granted directory prefix (path boundary — a grant of a directory
/// never admits a sibling name).</summary>
internal static class RunGrant
{
    /// <summary>Whether the effective policy permits starting <paramref name="command"/>.</summary>
    internal static bool Allows(EffectivePolicy policy, string command)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var run = policy.For(PermissionKind.Run);
        if (run.AllowAll || (run.Allow.Count == 0 && run.Deny.Count == 0 && !run.DenyAll))
            return true;

        foreach (var allowed in run.Allow)
        {
            if (PermissionMatching.MatchesPath(allowed, command))
                return true;
        }

        return false;
    }
}
