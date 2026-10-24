using System.Diagnostics;
using Maieutics.DenoExecution;
using Maieutics.Agent;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoRepl;

/// <summary>
///     Computes the <see cref="EffectivePolicy"/> the kernel authorizes for one Deno REPL
///     session, independent of who derives the REPL child (ADR 0020 decision 1). The policy is the
///     layered overlay built by <see cref="PermissionBaseline.ForDenoRepl"/> (built-in baseline,
///     app-wide defaults, project/workspace profile, session override; denials always win) plus the
///     esbuild-wasm read grant, which is resolved by a short <c>deno eval</c> (I/O). The kernel is
///     the permission authority: the policy is computed here, pre-cached by session id before the
///     host derives the REPL, and registered with the permission broker for the child's pid; the
///     host is only the enforcement point.
/// </summary>
internal static class DenoReplPolicyBuilder
{
    private const string EsbuildWasmVersion = "0.25.12";

    /// <summary>
    ///     Resolves the cached <c>esbuild.wasm</c> payload and computes the REPL's effective
    ///     policy. The esbuild resolution runs a short <c>deno eval</c> (I/O), so this method is
    ///     asynchronous; the policy computation itself is synchronous. Returns null when the
    ///     esbuild-wasm payload cannot be resolved (module graph absent or corrupt) — callers
    ///     install the module graph and retry, or surface the gap explicitly.
    /// </summary>
    internal static async Task<EffectivePolicy?> BuildAsync(
        string executable,
        string moduleDirectory,
        string workingDirectory,
        string configFile,
        string lockFile,
        string ipcAddress,
        string? windowsPipeName,
        PermissionPolicyAcquirer acquirer,
        AgentSessionId? ownerSessionId,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(configFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(ipcAddress);
        ArgumentNullException.ThrowIfNull(logger);
        var esbuildWasm = await ResolveEsbuildWasmAsync(
                executable,
                configFile,
                lockFile,
                logger,
                cancellationToken)
            .ConfigureAwait(false);
        if (esbuildWasm is null) return null;
        return Build(moduleDirectory, workingDirectory, configFile, lockFile, ipcAddress, esbuildWasm, windowsPipeName, acquirer, ownerSessionId);
    }

    /// <summary>Synchronous policy computation from an already-resolved esbuild-wasm path.</summary>
    internal static EffectivePolicy Build(
        string moduleDirectory,
        string workingDirectory,
        string configFile,
        string lockFile,
        string ipcAddress,
        string esbuildWasm,
        string? windowsPipeName,
        PermissionPolicyAcquirer acquirer,
        AgentSessionId? ownerSessionId)
    {
        var baseline = PermissionBaseline.ForDenoReplLayer(
            moduleDirectory,
            workingDirectory,
            configFile,
            lockFile,
            ipcAddress,
            esbuildWasm,
            windowsPipeName);
        // The full acquisition path (ADR 0018 Phase 5): the launch-time baseline overlaid with
        // the configuration-owned layers; the override lookup scopes by the owning Agent session.
        var composed = acquirer.Acquire(baseline, ownerSessionId);
        // The instruction-surface overlay (ADR 0032): a cell writing an instruction file through
        // the direct Deno path must meet the same gate the kernel tools apply, and the broker
        // only understands path denies — so every instruction surface the composed policy does
        // not explicitly allow becomes a write deny.
        return DeriveInstructionDenyOverlay(composed, workingDirectory);
    }

    /// <summary>Composes the derived write-deny overlay for instruction surfaces onto an already
    /// composed policy. The surfaces are enumerated from the working directory at acquisition
    /// time (bounded, reparse points skipped): every existing AGENTS.md at any depth and every
    /// file under <c>.agents/</c>. Files created later under a yet-unwalked tree are the
    /// documented residual — the frontend-attached path is fully gated by the kernel tools.</summary>
    internal static EffectivePolicy DeriveInstructionDenyOverlay(EffectivePolicy composed, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(composed);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var rules = composed.For(PermissionKind.Instructions);
        if (rules.AllowAll) return composed;

        var denied = new List<string>();
        foreach (var surface in EnumerateInstructionSurfaces(workingDirectory))
        {
            var allowed = rules.Allow.Any(pattern => PermissionMatching.MatchesPath(pattern, surface));
            if (!allowed) denied.Add(surface);
        }

        if (denied.Count == 0) return composed;

        var overlay = new PermissionLayer
        {
            Kinds = new Dictionary<PermissionKind, PermissionKindRules>
            {
                [PermissionKind.Write] = new() { Deny = denied }
            }
        };
        return PermissionLayerStore.Build(
            [
                new PermissionLayer { Kinds = composed.Kinds },
                overlay
            ],
            VariableTable.Expanded);
    }

    /// <summary>Yields the physical paths of the instruction surfaces that exist under the
    /// working directory: every file beneath <c>.agents/</c> and every <c>AGENTS.md</c> at any
    /// depth. The walk is bounded (a surface set beyond the cap is the documented residual) and
    /// never crosses reparse points.</summary>
    private static IEnumerable<string> EnumerateInstructionSurfaces(string workingDirectory)
    {
        const int maximumEntries = 10_000;
        var visited = 0;
        var pending = new Stack<string>([workingDirectory]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (++visited > maximumEntries) yield break;
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push(entry.FullName);
                    continue;
                }

                if (entry.Name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
                    IsUnderAgentsDirectory(entry.FullName, workingDirectory))
                    yield return entry.FullName;
            }
        }
    }

    private static bool IsUnderAgentsDirectory(string fullPath, string workingDirectory)
    {
        var root = Path.GetFullPath(workingDirectory);
        var relative = Path.GetRelativePath(root, fullPath);
        return relative.StartsWith(".agents", StringComparison.Ordinal) &&
               (relative.Length == ".agents".Length ||
                relative[".agents".Length] == Path.DirectorySeparatorChar ||
                relative[".agents".Length] == Path.AltDirectorySeparatorChar);
    }

    internal static InvalidOperationException CreateMissingModuleGraphException()
    {
        return new InvalidOperationException(
            $"The cached esbuild-wasm {EsbuildWasmVersion} payload is missing. " +
            "Install the Deno REPL module graph before starting Maieutics.");
    }

    private static async Task<string?> ResolveEsbuildWasmAsync(
        string executable,
        string configFile,
        string lockFile,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Mirror Aves' own resolution (eval-engine.ts): import.meta.resolve yields the exact
        // cached esbuild.wasm URL. No DENO_DIR probing and no cache-layout assumptions; when
        // the module graph is absent the eval fails and a warm install populates it.
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("eval");
        startInfo.ArgumentList.Add($"--config={configFile}");
        startInfo.ArgumentList.Add($"--lock={lockFile}");
        startInfo.ArgumentList.Add("console.log(import.meta.resolve('npm:esbuild-wasm/esbuild.wasm'))");
        // Kill-on-cancellation: a cancelled policy build must not orphan the eval child.
        var run = await DenoHelperProcess.RunAsync(
                startInfo,
                "to locate esbuild-wasm.",
                cancellationToken)
            .ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            logger.LogDebug("esbuild-wasm resolution failed ({ExitCode}): {Error}", run.ExitCode, run.StandardError.Trim());
            return null;
        }

        var output = run.StandardOutput;

        if (!Uri.TryCreate(output.Trim(), UriKind.Absolute, out var wasmUrl) ||
            !string.Equals(wasmUrl.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"esbuild-wasm resolved to an unexpected location: {output.Trim()}");

        // This is exactly the URL Aves reads inside the REPL child, so the --allow-read grant
        // uses its local path verbatim; no canonicalization is performed here.
        return File.Exists(wasmUrl.LocalPath) ? wasmUrl.LocalPath : null;
    }
}
