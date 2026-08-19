namespace Maieutics.Permissions;

/// <summary>Renders an <see cref="EffectivePolicy"/> to the exact <c>--allow-*</c> / <c>--deny-*</c>
/// argument set Deno 2.x accepts (verified locally against Deno 2.9.5; ADR 0018 §6). The renderer
/// performs no path canonicalization: Deno matches on the literal requested path, so grants are
/// the same strings the child will request. A kind with empty rules renders nothing, which Deno
/// treats as deny-by-default.</summary>
internal static class DenoPermissionRenderer
{
    internal static IReadOnlyList<string> Render(EffectivePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var arguments = new List<string>(PermissionKindCount * 2);
        foreach (var kind in Enum.GetValues<PermissionKind>())
        {
            var rules = policy.For(kind);
            if (rules.AllowAll) arguments.Add($"--allow-{PermissionKinds.GetName(kind)}");
            else if (rules.Allow.Count > 0) arguments.Add($"--allow-{PermissionKinds.GetName(kind)}={Join(rules.Allow)}");

            if (rules.DenyAll) arguments.Add($"--deny-{PermissionKinds.GetName(kind)}");
            else if (rules.Deny.Count > 0) arguments.Add($"--deny-{PermissionKinds.GetName(kind)}={Join(rules.Deny)}");
        }

        return arguments;
    }

    private static string Join(IReadOnlyList<string> values)
    {
        return string.Join(',', values);
    }

    private const int PermissionKindCount = 8;
}
