using Maieutics.Permissions;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoRepl;

/// <summary>
///     The kernel-facing boundary through which session-keyed REPL policies are registered and
///     released (ADR 0020 decision 1). The kernel computes the REPL's effective policy and caches
///     it under the session id <b>before</b> the host derives the REPL; a <c>host.repl.spawned</c>
///     report then registers that policy with the permission broker for the child's pid. The
///     interface lives in the DenoRepl layer so the session factory depends on the authority
///     contract, not on the <c>PluginHostManager</c> implementation.
/// </summary>
internal interface IReplPolicyRegistrar
{
    /// <summary>Caches the kernel-computed REPL policy for a session, replacing any prior value
    /// (a restart's new generation wins).</summary>
    void RegisterReplPolicy(string sessionId, EffectivePolicy policy);

    /// <summary>Removes a session's cached REPL policy (session close or restart), so the old
    /// policy cannot leak into the next generation.</summary>
    void UnregisterReplPolicy(string sessionId);
}

/// <summary>
///     Pre-caches the <see cref="EffectivePolicy"/> of one Deno REPL session so a host-derived
///     REPL report can register the real policy with the permission broker. The policy must exist
///     <b>before</b> the host derives the REPL child: with <c>DENO_PERMISSION_BROKER_PATH</c>
///     active the child performs an explicit broker handshake for every permission check, so the
///     kernel has to be able to register the true policy the moment a <c>host.repl.spawned</c>
///     report arrives — a policy computed on report would be too late and the broker would
///     register <see cref="EffectivePolicy.Default"/> instead. The kernel derives the same policy
///     for the REPL regardless of who spawns it (the esbuild-wasm resolution is shared with
///     <see cref="DenoReplProcess"/>), so the pre-cache is exactly the authority the host enforces.
/// </summary>
internal static class DenoReplPolicyCache
{
    /// <summary>Computes the session's REPL policy (esbuild-wasm resolution + baseline overlay)
    /// and pre-caches it under the session id before the REPL child starts, so a host-derived
    /// REPL report for the same session registers the true policy. Recomputes on restart so a new
    /// generation observes the current working directory (the cached entry is replaced). A failure
    /// to resolve esbuild-wasm leaves the session without a cached policy — the session start
    /// itself proceeds and a later report degrades explicitly.</summary>
    internal static async Task<EffectivePolicy?> PrepareAsync(
        IReplPolicyRegistrar? registrar,
        string executable,
        string moduleDirectory,
        string workingDirectory,
        string configFile,
        string lockFile,
        string ipcAddress,
        string? windowsPipeName,
        string sessionId,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        EffectivePolicy? policy;
        try
        {
            policy = await DenoReplPolicyBuilder.BuildAsync(
                    executable,
                    moduleDirectory,
                    workingDirectory,
                    configFile,
                    lockFile,
                    ipcAddress,
                    windowsPipeName,
                    logger,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not resolve the effective REPL policy for session '{SessionId}'; " +
                "a host-derived REPL report will fall back to the default policy.",
                sessionId);
            return null;
        }

        if (policy is null)
        {
            logger.LogWarning(
                "The esbuild-wasm payload is not resolved for session '{SessionId}'; " +
                "a host-derived REPL report will fall back to the default policy.",
                sessionId);
            return null;
        }

        Cache(registrar, sessionId, policy);
        return policy;
    }

    /// <summary>Caches a kernel-computed policy for one session. Replaces any prior value so a
    /// restart's new generation wins.</summary>
    internal static void Cache(IReplPolicyRegistrar? registrar, string sessionId, EffectivePolicy policy)
    {
        registrar?.RegisterReplPolicy(sessionId, policy);
    }

    /// <summary>Clears a session's cached policy and its registration, so a closed or restarted
    /// session's old policy cannot leak into the next generation (the next start re-caches).</summary>
    internal static void Clear(IReplPolicyRegistrar? registrar, string sessionId)
    {
        registrar?.UnregisterReplPolicy(sessionId);
    }
}
