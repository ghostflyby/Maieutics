using Maieutics.Permissions;

namespace Maieutics.DenoExecution;

/// <summary>Builds the <c>--allow-*</c> / <c>--deny-*</c> argument list for an internal Deno child
/// from an <see cref="EffectivePolicy"/> plus the launch-time fixed grants the child needs to reach
/// its control channel and module graph (ADR 0018 Phase 1). The renderer in
/// <c>Maieutics.Permissions</c> is the pure per-kind function; this type owns the fixed grants and
/// emits the flag strings in a stable order.</summary>
internal static class DenoPermissionArguments
{
    /// <summary>Renders the policy flags for <paramref name="policy"/> and then the fixed
    /// <paramref name="fixedGrants"/>. Each entry is a Deno flag (for example
    /// <c>--allow-read=/x</c> or <c>--allow-net=localhost:80</c>) already formatted by the
    /// policy renderer or supplied verbatim by the fixed list.</summary>
    internal static IReadOnlyList<string> Build(
        EffectivePolicy policy,
        IReadOnlyList<string>? fixedGrants = null)
    {
        var policyFlags = DenoPermissionRenderer.Render(policy);
        if (fixedGrants is null || fixedGrants.Count == 0) return policyFlags;

        var combined = new List<string>(policyFlags.Count + fixedGrants.Count);
        combined.AddRange(policyFlags);
        combined.AddRange(fixedGrants);
        return combined;
    }
}
