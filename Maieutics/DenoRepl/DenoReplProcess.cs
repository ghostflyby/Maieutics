using System.Diagnostics;
using System.Net;
using Maieutics.DenoExecution;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoRepl;

internal sealed record DenoReplProcessOptions(
    string Executable,
    string MainUrl,
    string ConfigFile,
    string LockFile,
    string ModuleDirectory,
    string WorkingDirectory,
    string IpcAddress,
    string SessionId,
    int Generation,
    string ClientUrl,
    string? WindowsPipeName,
    bool AutoInstallModuleGraph = true);

/// <summary>Thin adapter over <see cref="DenoRunProcess"/> for the Deno REPL child. Owns the
/// REPL-specific concerns — esbuild-wasm resolution, module-graph install, and the launch-time
/// control-channel flags and environment — and delegates launch, drain, exit observation, and
/// stop to the shared internal Deno process module (ADR 0018 §8).</summary>
internal sealed class DenoReplProcess : IAsyncDisposable
{
    private const string EsbuildWasmVersion = "0.25.12";
    private readonly DenoRunProcess inner;

    private DenoReplProcess(DenoRunProcess inner)
    {
        this.inner = inner;
    }

    internal int ProcessId => inner.ProcessId;

    internal Task Completion => inner.Completion;

    internal int? ExitCode => inner.ExitCode;

    internal Task<string> StandardError => inner.StandardError ?? Task.FromResult(string.Empty);

    public ValueTask DisposeAsync()
    {
        return inner.DisposeAsync();
    }

    internal static async Task<DenoReplProcess> StartAsync(
        DenoReplProcessOptions options,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        var esbuildWasm = await ResolveEsbuildWasmAsync(options, logger, cancellationToken).ConfigureAwait(false);
        if (esbuildWasm is null)
        {
            if (!options.AutoInstallModuleGraph)
                throw CreateMissingModuleGraphException();
            await InstallModuleGraphAsync(options, cancellationToken).ConfigureAwait(false);
            esbuildWasm = await ResolveEsbuildWasmAsync(options, logger, cancellationToken).ConfigureAwait(false)
                          ?? throw CreateMissingModuleGraphException();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.Executable,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-prompt");
        startInfo.ArgumentList.Add("--unstable-worker-options");
        startInfo.ArgumentList.Add($"--config={options.ConfigFile}");
        startInfo.ArgumentList.Add($"--lock={options.LockFile}");
        foreach (var argument in BuildPermissionArguments(options, esbuildWasm))
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(options.MainUrl);

        startInfo.Environment.Clear();
        startInfo.Environment[DenoReplEnvironment.IpcAddress] = options.IpcAddress;
        startInfo.Environment[DenoReplEnvironment.SessionId] = options.SessionId;
        startInfo.Environment[DenoReplEnvironment.Generation] = options.Generation.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment[DenoReplEnvironment.ClientModule] = options.ClientUrl;
        CopyEnvironment(startInfo, "DENO_DIR");
        CopyEnvironment(startInfo, "TMPDIR");
        CopyEnvironment(startInfo, "TMP");
        CopyEnvironment(startInfo, "TEMP");
        if (OperatingSystem.IsWindows())
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
                             ?? throw new InvalidOperationException("SystemRoot is not configured.");
            startInfo.Environment["SystemRoot"] = systemRoot;
            startInfo.Environment[DenoReplEnvironment.PipeName] = options.WindowsPipeName;
        }

        var inner = DenoRunProcess.Start(startInfo, InternalDenoProcessKind.DenoRepl, logger, true);
        logger.LogInformation(
            "Deno REPL session {SessionId} generation {Generation} started with pid {ProcessId}.",
            options.SessionId,
            options.Generation,
            inner.ProcessId);
        return new DenoReplProcess(inner);
    }

    private static IReadOnlyList<string> BuildPermissionArguments(DenoReplProcessOptions options, string esbuildWasm)
    {
        var environmentNames = new List<string>
        {
            DenoReplEnvironment.IpcAddress,
            DenoReplEnvironment.SessionId,
            DenoReplEnvironment.Generation,
            DenoReplEnvironment.ClientModule,
            // Provider secret names are readable so Deno.env.get does not fail, but the values are
            // never injected into the child environment; evaluated cells observe them as undefined.
            "OPENAI_API_KEY"
        };
        var readablePaths = new List<string>
        {
            options.ModuleDirectory,
            options.WorkingDirectory,
            options.ConfigFile,
            options.LockFile,
            esbuildWasm
        };

        var fixedGrants = new List<string> { $"--allow-env={string.Join(',', environmentNames)}" };
        if (OperatingSystem.IsWindows())
        {
            var pipeName = options.WindowsPipeName
                           ?? throw new PlatformNotSupportedException(
                               "The Windows named-pipe bootstrap is not configured.");
            fixedGrants.Add($"--allow-net={RequireWindowsLoopbackAddress(options.IpcAddress)}");
            // Deno 2.9.5 ignores the path argument of --allow-ffi (verified: an exact file or
            // directory grant still rejects Deno.dlopen), so the Windows named-pipe credential
            // bootstrap requires the unsuffixed form. The grant is Windows-only and the child
            // launch arguments remain fully controlled by the kernel. Re-verify on Windows
            // before narrowing (ADR 0018 §10; Maieutics/AGENTS.md).
            fixedGrants.Add("--allow-ffi");
        }
        else
        {
            var socketPath = Path.GetFullPath(options.IpcAddress);
            fixedGrants.Add($"--allow-net=unix:{socketPath},localhost:80");
            readablePaths.Add(socketPath);
            fixedGrants.Add($"--allow-write={socketPath}");
        }

        fixedGrants.Add($"--allow-read={string.Join(',', readablePaths.Distinct(StringComparer.Ordinal))}");
        return DenoPermissionArguments.Build(EffectivePolicy.Default, fixedGrants);
    }

    internal Task StopAsync()
    {
        return inner.StopAsync();
    }

    private static async Task<string?> ResolveEsbuildWasmAsync(
        DenoReplProcessOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Mirror Aves' own resolution (eval-engine.ts): import.meta.resolve yields the exact
        // cached esbuild.wasm URL. No DENO_DIR probing and no cache-layout assumptions; when
        // the module graph is absent the eval fails and a warm install populates it.
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("eval");
        startInfo.ArgumentList.Add($"--config={options.ConfigFile}");
        startInfo.ArgumentList.Add($"--lock={options.LockFile}");
        startInfo.ArgumentList.Add("console.log(import.meta.resolve('npm:esbuild-wasm/esbuild.wasm'))");
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                $"Could not start '{options.Executable}' to locate esbuild-wasm.");
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

    private static InvalidOperationException CreateMissingModuleGraphException()
    {
        return new InvalidOperationException(
            $"The cached esbuild-wasm {EsbuildWasmVersion} payload is missing. " +
            "Install the Deno REPL module graph before starting Maieutics.");
    }

    private static async Task InstallModuleGraphAsync(
        DenoReplProcessOptions options,
        CancellationToken cancellationToken)
    {
        // No --allow-* flags are needed: Deno loads and downloads the initial module graph
        // without consulting the permission system. The child inherits the environment and
        // Deno decides its own cache location, matching where Aves reads esbuild-wasm.
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Executable,
            WorkingDirectory = options.ModuleDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("cache");
        startInfo.ArgumentList.Add($"--config={options.ConfigFile}");
        startInfo.ArgumentList.Add($"--lock={options.LockFile}");
        startInfo.ArgumentList.Add(options.MainUrl);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                $"Could not start '{options.Executable}' to install the Deno REPL module graph.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        _ = await standardOutput.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Installing the Deno REPL module graph failed with exit code {process.ExitCode}. " +
                $"stderr: {error.Trim()}");
    }

    private static string RequireWindowsLoopbackAddress(string address)
    {
        if (!Uri.TryCreate($"http://{address}", UriKind.Absolute, out var uri) ||
            !IPAddress.TryParse(uri.Host, out var ipAddress) ||
            !IPAddress.IsLoopback(ipAddress) || uri.Port <= 0)
            throw new InvalidOperationException("The Windows REPL endpoint must be a concrete loopback host and port.");
        return $"{ipAddress}:{uri.Port}";
    }

    private static void CopyEnvironment(ProcessStartInfo startInfo, string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            startInfo.Environment[name] = value;
    }
}

internal static class DenoReplEnvironment
{
    internal const string IpcAddress = "MAIEUTICS_REPL_IPC";
    internal const string SessionId = "MAIEUTICS_REPL_SESSION";
    internal const string Generation = "MAIEUTICS_REPL_GENERATION";
    internal const string ClientModule = "MAIEUTICS_REPL_CLIENT";
    internal const string PipeName = "MAIEUTICS_REPL_PIPE";
    internal const string Credential = "MAIEUTICS_REPL_CREDENTIAL";
}
