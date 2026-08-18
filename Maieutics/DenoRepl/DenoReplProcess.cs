using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
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

internal sealed class DenoReplProcess : IAsyncDisposable
{
    private const int DrainBufferCharacters = 4096;
    private const int MaximumLoggedCharactersPerStream = 32 * 1024;
    private const string EsbuildWasmVersion = "0.25.12";
    private readonly Lock gate = new();
    private readonly Process process;
    private readonly int processId;
    private readonly TaskCompletionSource<string> standardError =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int exitCode = int.MinValue;
    private Task? stopping;

    private DenoReplProcess(Process process, ILogger logger)
    {
        this.process = process;
        processId = process.Id;
        var stdoutDrain = DrainAsync(process.StandardOutput, "stdout", logger, processId, null);
        var stderrDrain = DrainAsync(process.StandardError, "stderr", logger, processId, standardError);
        Completion = ObserveCompletionAsync(stdoutDrain, stderrDrain);
    }

    internal int ProcessId => processId;

    internal Task Completion { get; }

    internal int? ExitCode
    {
        get
        {
            var value = Volatile.Read(ref exitCode);
            return value == int.MinValue ? null : value;
        }
    }

    internal Task<string> StandardError => standardError.Task;

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }

    internal static async Task<DenoReplProcess> StartAsync(
        DenoReplProcessOptions options,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        var denoDirectory = ResolveDenoDirectory();
        var esbuildWasm = ResolveEsbuildWasm(denoDirectory);
        if (!File.Exists(esbuildWasm))
        {
            if (!options.AutoInstallModuleGraph)
                throw CreateMissingModuleGraphException(esbuildWasm);
            await InstallModuleGraphAsync(options, denoDirectory, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(esbuildWasm))
                throw CreateMissingModuleGraphException(esbuildWasm);
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

        if (OperatingSystem.IsWindows())
        {
            var pipeName = options.WindowsPipeName
                           ?? throw new PlatformNotSupportedException(
                               "The Windows named-pipe bootstrap is not configured.");
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
                             ?? throw new InvalidOperationException("SystemRoot is not configured.");
            var kernel32 = Path.Combine(systemRoot, "System32", "kernel32.dll");
            environmentNames.Add(DenoReplEnvironment.PipeName);
            environmentNames.Add(DenoReplEnvironment.Credential);
            environmentNames.Add("SystemRoot");
            startInfo.ArgumentList.Add($"--allow-net={RequireWindowsLoopbackAddress(options.IpcAddress)}");
            startInfo.ArgumentList.Add($"--allow-ffi={kernel32}");
            startInfo.Environment[DenoReplEnvironment.PipeName] = pipeName;
            startInfo.Environment["SystemRoot"] = systemRoot;
        }
        else
        {
            var socketPath = Path.GetFullPath(options.IpcAddress);
            startInfo.ArgumentList.Add($"--allow-net=unix:{socketPath},localhost:80");
            readablePaths.Add(socketPath);
            startInfo.ArgumentList.Add($"--allow-write={socketPath}");
        }

        startInfo.ArgumentList.Add($"--allow-env={string.Join(',', environmentNames)}");
        startInfo.ArgumentList.Add($"--allow-read={string.Join(',', readablePaths.Distinct(StringComparer.Ordinal))}");
        startInfo.ArgumentList.Add(options.MainUrl);

        startInfo.Environment.Clear();
        startInfo.Environment[DenoReplEnvironment.IpcAddress] = options.IpcAddress;
        startInfo.Environment[DenoReplEnvironment.SessionId] = options.SessionId;
        startInfo.Environment[DenoReplEnvironment.Generation] = options.Generation.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment[DenoReplEnvironment.ClientModule] = options.ClientUrl;
        startInfo.Environment["DENO_DIR"] = denoDirectory;
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

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("The Deno REPL process could not be started.");
        logger.LogInformation(
            "Deno REPL session {SessionId} generation {Generation} started with pid {ProcessId}.",
            options.SessionId,
            options.Generation,
            process.Id);
        return new DenoReplProcess(process, logger);
    }

    internal Task StopAsync()
    {
        lock (gate)
        {
            return stopping ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await Task.Yield();
        try
        {
            if (!Completion.IsCompleted && !process.HasExited) process.Kill(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
        }

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch
        {
        }

        process.Dispose();
    }

    private static string ResolveEsbuildWasm(string denoDirectory)
    {
        return ResolveRealPath(Path.Combine(
            denoDirectory,
            "npm",
            "registry.npmjs.org",
            "esbuild-wasm",
            EsbuildWasmVersion,
            "esbuild.wasm"));
    }

    private static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        return directory is null
            ? full
            : Path.Combine(ResolveRealDirectory(directory), Path.GetFileName(full));
    }

    private static string ResolveRealDirectory(string path)
    {
        // Resolve symlinks only along the existing ancestor chain; the non-existent tail
        // (the module graph is installed on first use) is appended verbatim.
        var tail = new List<string>();
        var current = new DirectoryInfo(path);
        while (current is not null && !current.Exists)
        {
            tail.Insert(0, current.Name);
            current = current.Parent;
        }

        if (current is null) return path;

        var parts = new List<string>();
        while (current is not null)
        {
            parts.Insert(0, current.Name);
            current = current.Parent;
        }

        // Deno checks file permissions against the canonicalized path (realpath), so every
        // symlinked ancestor must be resolved in the grant too; /tmp on macOS is /private/tmp.
        var resolved = parts[0];
        for (var index = 1; index < parts.Count; index++)
        {
            var candidate = Path.Combine(resolved, parts[index]);
            var info = new DirectoryInfo(candidate);
            if (info.Exists && info.ResolveLinkTarget(true) is { } link)
                resolved = link.FullName;
            else
                resolved = candidate;
        }

        return tail.Count == 0 ? resolved : Path.Combine([resolved, .. tail]);
    }

    private static InvalidOperationException CreateMissingModuleGraphException(string esbuildWasm)
    {
        return new InvalidOperationException(
            $"The cached esbuild-wasm {EsbuildWasmVersion} payload is missing. " +
            "Install the Deno REPL module graph before starting Maieutics.");
    }

    private static async Task InstallModuleGraphAsync(
        DenoReplProcessOptions options,
        string denoDirectory,
        CancellationToken cancellationToken)
    {
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
        startInfo.Environment["DENO_DIR"] = denoDirectory;

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

    private static string ResolveDenoDirectory()
    {
        if (Environment.GetEnvironmentVariable("DENO_DIR") is { Length: > 0 } configured)
            return Path.GetFullPath(configured);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "deno");
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Caches", "deno");
        if (Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cache)
            return Path.Combine(cache, "deno");
        return Path.Combine(home, ".cache", "deno");
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

    private static async Task DrainAsync(
        TextReader reader,
        string streamName,
        ILogger logger,
        int processId,
        TaskCompletionSource<string>? capturedOutput)
    {
        var buffer = ArrayPool<char>.Shared.Rent(DrainBufferCharacters);
        var remainingLogBudget = logger.IsEnabled(LogLevel.Debug) ? MaximumLoggedCharactersPerStream : 0;
        var captured = capturedOutput is null ? null : new StringBuilder();
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, DrainBufferCharacters)).ConfigureAwait(false);
                if (read == 0) break;
                if (captured is not null && captured.Length < MaximumLoggedCharactersPerStream)
                {
                    var count = Math.Min(read, MaximumLoggedCharactersPerStream - captured.Length);
                    captured.Append(buffer, 0, count);
                }
                if (remainingLogBudget <= 0) continue;
                var loggedCount = Math.Min(read, remainingLogBudget);
                var output = new string(buffer, 0, loggedCount);
                if (!string.IsNullOrWhiteSpace(output))
                    logger.LogDebug(
                        "Deno REPL {ProcessId} {StreamName}: {Output}",
                        processId,
                        streamName,
                        output);
                remainingLogBudget -= loggedCount;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            logger.LogDebug(exception, "Deno REPL {ProcessId} {StreamName} drain ended before EOF.", processId, streamName);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
            capturedOutput?.TrySetResult(captured?.ToString() ?? string.Empty);
        }
    }

    private async Task ObserveCompletionAsync(Task stdoutDrain, Task stderrDrain)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);
        Volatile.Write(ref exitCode, process.ExitCode);
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
