using System.Buffers;
using System.Diagnostics;
using Maieutics.Control;
using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins;

internal sealed record PluginHostProcessOptions(
    string DenoExecutable,
    string HostModuleUrl,
    string SocketPath,
    string ConfigPath,
    string HostId,
    string SdkUrl,
    string WorkerEntryUrl,
    string HostConfigFile,
    PluginHostProcessGrants Grants);

/// <summary>Positive permission grants for the host process (the union of plugin grants).</summary>
internal sealed record PluginHostProcessGrants(
    bool ReadAll,
    IReadOnlyList<string> Read,
    bool WriteAll,
    IReadOnlyList<string> Write,
    bool NetAll,
    IReadOnlyList<string> Net,
    bool EnvAll,
    IReadOnlyList<string> Env,
    bool ImportAll,
    IReadOnlyList<string> Import);

/// <summary>
///     Owns the out-of-process plugin host: launches the restricted `deno run` process with the
///     control channel address and the kernel-written plugin configuration, and observes its exit.
/// </summary>
internal sealed class PluginHostProcess : IAsyncDisposable
{
    private const int DrainBufferCharacters = 4096;
    private const int MaximumLoggedCharactersPerStream = 32 * 1024;

    private static readonly string[] AllowedEnvironmentNames =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "TMPDIR",
        "TMP",
        "TEMP",
        "DENO_DIR",
        "XDG_CACHE_HOME",
        "LANG",
        "LC_ALL",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "SYSTEMROOT",
        "WINDIR",
        "COMSPEC",
        "PATHEXT"
    ];

    private readonly Lock gate = new();
    private readonly Process process;
    private readonly int processId;
    private int exitCode = int.MinValue;
    private Task? stopping;

    private PluginHostProcess(Process process, ILogger logger)
    {
        this.process = process;
        processId = process.Id;
        var stdoutDrain = DrainAsync(process.StandardOutput, "stdout", logger, processId);
        var stderrDrain = DrainAsync(process.StandardError, "stderr", logger, processId);
        Completion = ObserveCompletionAsync(stdoutDrain, stderrDrain);
    }

    public int ProcessId => processId;

    public Task Completion { get; }

    public int? ExitCode
    {
        get
        {
            var value = Volatile.Read(ref exitCode);
            return value == int.MinValue ? null : value;
        }
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }

    public static PluginHostProcess Start(
        PluginHostProcessOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.DenoExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--unstable-worker-options");
        startInfo.ArgumentList.Add($"--config={options.HostConfigFile}");
        AddGrant(startInfo, "--allow-read", options.Grants.ReadAll, options.Grants.Read);
        AddGrant(startInfo, "--allow-write", options.Grants.WriteAll, options.Grants.Write);
        AddGrant(startInfo, "--allow-net", options.Grants.NetAll, options.Grants.Net);
        AddGrant(startInfo, "--allow-env", options.Grants.EnvAll, options.Grants.Env);
        AddGrant(startInfo, "--allow-import", options.Grants.ImportAll, options.Grants.Import);
        startInfo.ArgumentList.Add(options.HostModuleUrl);

        startInfo.EnvironmentVariables[ReplControlEnvironment.IpcAddress] = options.SocketPath;
        startInfo.EnvironmentVariables[ReplControlEnvironment.PluginHostId] = options.HostId;
        startInfo.EnvironmentVariables[ReplControlEnvironment.PluginConfig] = options.ConfigPath;
        startInfo.EnvironmentVariables[ReplControlEnvironment.PluginSdk] = options.SdkUrl;
        startInfo.EnvironmentVariables[ReplControlEnvironment.PluginWorkerEntry] = options.WorkerEntryUrl;
        foreach (var name in AllowedEnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) startInfo.EnvironmentVariables[name] = value;
        }

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("The plugin host process could not be started.");
        logger.LogInformation("Plugin host started with pid {ProcessId}.", process.Id);
        return new PluginHostProcess(process, logger);
    }

    private static void AddGrant(
        ProcessStartInfo startInfo,
        string flag,
        bool allowAll,
        IReadOnlyList<string> values)
    {
        if (allowAll)
        {
            startInfo.ArgumentList.Add(flag);
            return;
        }

        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length > 0) startInfo.ArgumentList.Add($"{flag}={string.Join(",", distinct)}");
    }

    public Task StopAsync()
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
            // The process exited between the check and the kill.
        }

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch
        {
            // Exit observation must not mask shutdown.
        }

        process.Dispose();
    }

    private static async Task DrainAsync(
        TextReader reader,
        string streamName,
        ILogger logger,
        int processId)
    {
        var buffer = ArrayPool<char>.Shared.Rent(DrainBufferCharacters);
        var remainingLogBudget = logger.IsEnabled(LogLevel.Debug) ? MaximumLoggedCharactersPerStream : 0;
        var truncationLogged = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, DrainBufferCharacters)).ConfigureAwait(false);
                if (read == 0) break;

                if (remainingLogBudget == 0)
                {
                    if (!truncationLogged && logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "Plugin host {ProcessId} {StreamName} logging was truncated after {CharacterLimit} characters; remaining output is still drained.",
                            processId,
                            streamName,
                            MaximumLoggedCharactersPerStream);
                        truncationLogged = true;
                    }

                    continue;
                }

                var loggedCount = Math.Min(read, remainingLogBudget);
                var output = new string(buffer, 0, loggedCount);
                if (!string.IsNullOrWhiteSpace(output))
                    logger.LogDebug(
                        "Plugin host {ProcessId} {StreamName}: {Output}",
                        processId,
                        streamName,
                        output);
                remainingLogBudget -= loggedCount;
                if (loggedCount >= read) continue;

                logger.LogDebug(
                    "Plugin host {ProcessId} {StreamName} logging was truncated after {CharacterLimit} characters; remaining output is still drained.",
                    processId,
                    streamName,
                    MaximumLoggedCharactersPerStream);
                truncationLogged = true;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            logger.LogDebug(
                exception,
                "Plugin host {ProcessId} {StreamName} drain ended before EOF.",
                processId,
                streamName);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private async Task ObserveCompletionAsync(Task stdoutDrain, Task stderrDrain)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);
        Volatile.Write(ref exitCode, process.ExitCode);
    }
}
