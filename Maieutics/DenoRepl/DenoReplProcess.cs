using System.Diagnostics;
using Maieutics.DenoExecution;
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
    DenoPermissionBroker Broker,
    bool AutoInstallModuleGraph = true);

/// <summary>Thin adapter over <see cref="DenoRunProcess"/> for the Deno REPL child. Owns the
/// REPL-specific concerns — esbuild-wasm resolution, module-graph install, and the launch-time
/// control-channel environment — and delegates launch, drain, exit observation, and stop to the
/// shared internal Deno process module (ADR 0018 §8). The broker is the single permission
/// authority: the child launches with <c>DENO_PERMISSION_BROKER_PATH</c> and no <c>--allow-*</c>
/// flags, and the policy registered at spawn carries the control-channel and module-graph grants.</summary>
internal sealed class DenoReplProcess : IAsyncDisposable
{
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
        var policy = await DenoReplPolicyBuilder.BuildAsync(
                options.Executable,
                options.ModuleDirectory,
                options.WorkingDirectory,
                options.ConfigFile,
                options.LockFile,
                options.IpcAddress,
                options.WindowsPipeName,
                logger,
                cancellationToken)
            .ConfigureAwait(false);
        if (policy is null)
        {
            if (!options.AutoInstallModuleGraph)
                throw DenoReplPolicyBuilder.CreateMissingModuleGraphException();
            await InstallModuleGraphAsync(options, cancellationToken).ConfigureAwait(false);
            policy = await DenoReplPolicyBuilder.BuildAsync(
                    options.Executable,
                    options.ModuleDirectory,
                    options.WorkingDirectory,
                    options.ConfigFile,
                    options.LockFile,
                    options.IpcAddress,
                    options.WindowsPipeName,
                    logger,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw DenoReplPolicyBuilder.CreateMissingModuleGraphException();
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
        startInfo.ArgumentList.Add(options.MainUrl);

        startInfo.Environment.Clear();
        var environment = DenoReplEnvironment.Build(
            options.IpcAddress,
            options.SessionId,
            options.Generation,
            options.ClientUrl,
            options.WindowsPipeName);
        environment[DenoReplEnvironment.BrokerAddress] = options.Broker.Address;
        foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;

        var inner = DenoRunProcess.Start(
            startInfo,
            InternalDenoProcessKind.DenoRepl,
            logger,
            options.Broker,
            policy,
            captureStandardError: true);
        logger.LogInformation(
            "Deno REPL session {SessionId} generation {Generation} started with pid {ProcessId}.",
            options.SessionId,
            options.Generation,
            inner.ProcessId);
        return new DenoReplProcess(inner);
    }

    internal Task StopAsync()
    {
        return inner.StopAsync();
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
}

internal static class DenoReplEnvironment
{
    internal const string IpcAddress = "MAIEUTICS_REPL_IPC";
    internal const string SessionId = "MAIEUTICS_REPL_SESSION";
    internal const string Generation = "MAIEUTICS_REPL_GENERATION";
    internal const string ClientModule = "MAIEUTICS_REPL_CLIENT";
    internal const string PipeName = "MAIEUTICS_REPL_PIPE";
    internal const string Credential = "MAIEUTICS_REPL_CREDENTIAL";
    internal const string BrokerAddress = "DENO_PERMISSION_BROKER_PATH";

    /// <summary>
    ///     Builds the complete REPL child environment both derivation paths use (the kernel-derived
    ///     process launch and the <c>host.repl.derive</c> payload env). The set is authoritative
    ///     (B5a): MAIEUTICS_REPL_IPC / SESSION / GENERATION / CLIENT, the inherited DENO_DIR / TMP*
    ///     cache variables, and on Windows SystemRoot + MAIEUTICS_REPL_PIPE. The kernel-derived path
    ///     adds <see cref="BrokerAddress"/> (DENO_PERMISSION_BROKER_PATH) at launch; the
    ///     host-derived path deliberately omits it — the host appends its own forwarded broker path
    ///     (B5a env contract) and never takes it from the kernel env.
    /// </summary>
    internal static Dictionary<string, string> Build(
        string ipcAddress,
        string sessionId,
        int generation,
        string clientModule,
        string? windowsPipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ipcAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientModule);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IpcAddress] = ipcAddress,
            [SessionId] = sessionId,
            [Generation] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [ClientModule] = clientModule
        };
        CopyEnvironment(environment, "DENO_DIR");
        CopyEnvironment(environment, "TMPDIR");
        CopyEnvironment(environment, "TMP");
        CopyEnvironment(environment, "TEMP");
        if (OperatingSystem.IsWindows())
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
                             ?? throw new InvalidOperationException("SystemRoot is not configured.");
            environment["SystemRoot"] = systemRoot;
            environment[PipeName] = windowsPipeName
                ?? throw new InvalidOperationException(
                    "The Windows named-pipe bootstrap is required for the REPL child environment.");
        }

        return environment;
    }

    private static void CopyEnvironment(Dictionary<string, string> target, string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            target[name] = value;
    }
}
