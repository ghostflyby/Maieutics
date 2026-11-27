using Maieutics.Permissions;

namespace Maieutics.Processes;

/// <summary>The executable gate for kernel-owned process starts. Explicit deny rules take
/// precedence over grants. An explicit allowlist restricts the requested executable, while
/// an absent allowlist permits it subject to the denials. The sandbox and any required
/// approval are decided separately. Native Deno permission requests use the broker.</summary>
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
