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

    private readonly CancellationTokenSource lifetime = new();

    private readonly Process process;
    private bool stopped;

    private PluginHostProcess(Process process, Task completion)
    {
        this.process = process;
        this.Completion = completion;
    }

    public int ProcessId => process.Id;

    public Task Completion { get; }

    public int? ExitCode => process.HasExited ? process.ExitCode : null;

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
        _ = DrainAsync(process.StandardOutput, process.StandardError, logger, process.Id);
        var completion = process.WaitForExitAsync();
        logger.LogInformation("Plugin host started with pid {ProcessId}.", process.Id);
        return new PluginHostProcess(process, completion);
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

    public async Task StopAsync()
    {
        if (stopped) return;

        stopped = true;
        lifetime.Cancel();
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (InvalidOperationException)
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
    }

    private static async Task DrainAsync(
        TextReader standardOutput,
        TextReader standardError,
        ILogger logger,
        int processId)
    {
        var output = await standardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await standardError.ReadToEndAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(output))
            logger.LogDebug("Plugin host {ProcessId} stdout: {Output}", processId, output);

        if (!string.IsNullOrWhiteSpace(error))
            logger.LogDebug("Plugin host {ProcessId} stderr: {Error}", processId, error);
    }
}