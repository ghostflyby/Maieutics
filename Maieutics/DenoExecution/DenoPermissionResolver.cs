using Maieutics.Permissions;

namespace Maieutics.DenoExecution;

/// <summary>Resolves one permission request from an internal Deno child against its effective
/// policy. Semantics (ADR 0018 §9): an exact allow match grants; an exact deny match denies with the
/// policy reason; everything else denies by default — unsolicited requests never escalate and the
/// broker never prompts. Deny-wins follows the store's overlay rule.</summary>
internal static class DenoPermissionResolver
{
    internal static DenoBrokerDecision Resolve(EffectivePolicy policy, string permission, string value)
    {
        var kind = ParseKind(permission);
        if (kind is null)
            return DenoBrokerDecision.Deny($"Unknown permission kind '{permission}'.");

        var rules = policy.For(kind.Value);
        if (MatchesAny(rules.Deny, value) || rules.DenyAll)
            return DenoBrokerDecision.Deny(DenyReason(kind.Value, value));

        if (MatchesAny(rules.Allow, value) || rules.AllowAll)
            return DenoBrokerDecision.Allow();

        return DenoBrokerDecision.Deny(DenyReason(kind.Value, value));
    }

    private static PermissionKind? ParseKind(string permission)
    {
        return permission switch
        {
            "read" => PermissionKind.Read,
            "write" => PermissionKind.Write,
            "net" => PermissionKind.Net,
            "env" => PermissionKind.Env,
            "run" => PermissionKind.Run,
            "ffi" => PermissionKind.Ffi,
            "sys" => PermissionKind.Sys,
            "import" => PermissionKind.Import,
            _ => null
        };
    }

    private static bool MatchesAny(IReadOnlyList<string> patterns, string value)
    {
        foreach (var pattern in patterns)
            if (pattern.Length == 0 || value.StartsWith(pattern, StringComparison.Ordinal))
                return true;

        return false;
    }

    private static string DenyReason(PermissionKind kind, string value)
    {
        return $"Requires {PermissionKinds.GetName(kind)} access to {value}";
    }
}
