namespace Maieutics.Permissions;

using System.Text.Json;

using Maieutics.Control;

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

    /// <summary>
    ///     Builds the <see cref="HostReplPermissions"/> static shell a <c>host.repl.derive</c>
    ///     instruction ships for worker-actor <c>spawnProcess</c>, from the effective REPL policy.
    ///     A kind renders as a JSON <c>true</c> (allow all), a JSON string array (allowlist), or
    ///     <see langword="null"/> (deny-by-default); denials drop the kind entirely because
    ///     worker-actor 0.4.0 renders no <c>--deny-*</c> flags. Returns null when the policy
    ///     grants nothing (the instruction omits the shell, which the host treats as its skeleton
    ///     default — a deliberate, surfaced fallback baseline).
    /// </summary>
    internal static HostReplPermissions? BuildHostReplPermissions(EffectivePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var read = RenderKind(policy, PermissionKind.Read);
        var write = RenderKind(policy, PermissionKind.Write);
        var net = RenderKind(policy, PermissionKind.Net);
        var env = RenderKind(policy, PermissionKind.Env);
        var run = RenderKind(policy, PermissionKind.Run);
        var ffi = RenderKind(policy, PermissionKind.Ffi);
        var sys = RenderKind(policy, PermissionKind.Sys);
        var import = RenderKind(policy, PermissionKind.Import);
        if (read is null && write is null && net is null && env is null && run is null &&
            ffi is null && sys is null && import is null)
            return null;

        return new HostReplPermissions(read, write, net, env, run, ffi, sys, import);
    }

    private static JsonElement? RenderKind(EffectivePolicy policy, PermissionKind kind)
    {
        var rules = policy.For(kind);
        if (rules.DenyAll || rules.Deny.Count > 0) return null;
        if (rules.AllowAll) return JsonSerializer.SerializeToElement(true, Plugins.PluginHostJsonContext.Default.Boolean);
        if (rules.Allow.Count == 0) return null;
        return JsonSerializer.SerializeToElement(
            [.. rules.Allow],
            Plugins.PluginHostJsonContext.Default.StringArray);
    }

    private static string Join(IReadOnlyList<string> values)
    {
        return string.Join(',', values);
    }

    private const int PermissionKindCount = 8;
}
