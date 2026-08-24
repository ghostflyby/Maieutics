using System.Diagnostics;
using Maieutics.DenoExecution;
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
        return Build(moduleDirectory, workingDirectory, configFile, lockFile, ipcAddress, esbuildWasm, windowsPipeName);
    }

    /// <summary>Synchronous policy computation from an already-resolved esbuild-wasm path.</summary>
    internal static EffectivePolicy Build(
        string moduleDirectory,
        string workingDirectory,
        string configFile,
        string lockFile,
        string ipcAddress,
        string esbuildWasm,
        string? windowsPipeName = null)
    {
        return PermissionBaseline.ForDenoRepl(
            moduleDirectory,
            workingDirectory,
            configFile,
            lockFile,
            ipcAddress,
            esbuildWasm,
            windowsPipeName);
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
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                $"Could not start '{executable}' to locate esbuild-wasm.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            logger.LogDebug("esbuild-wasm resolution failed ({ExitCode}): {Error}", process.ExitCode, error.Trim());
            return null;
        }

        if (!Uri.TryCreate(output.Trim(), UriKind.Absolute, out var wasmUrl) ||
            !string.Equals(wasmUrl.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"esbuild-wasm resolved to an unexpected location: {output.Trim()}");

        // This is exactly the URL Aves reads inside the REPL child, so the --allow-read grant
        // uses its local path verbatim; no canonicalization is performed here.
        return File.Exists(wasmUrl.LocalPath) ? wasmUrl.LocalPath : null;
    }
}
