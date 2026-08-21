using System.Diagnostics;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.Permissions;
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
    string HostConfigFile);

/// <summary>Thin adapter over <see cref="DenoRunProcess"/> for the out-of-process plugin host:
/// launches the host <c>deno run</c> process with the control channel address and the
/// kernel-written plugin configuration, and delegates drain, exit observation, and stop to the
/// shared internal Deno process module (ADR 0018 §8). The host process runs with full Deno
/// permissions: it is trusted orchestration code that spawns per-plugin workers, and each worker
/// is narrowed by its own <c>deno.permissions</c> options (which cannot exceed the parent's
/// grants — Deno rejects escalation at spawn). The host therefore carries no broker and no
/// per-plugin grant union; worker isolation is enforced by the worker options alone.</summary>
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

    private readonly DenoRunProcess inner;

    private PluginHostProcess(DenoRunProcess inner)
    {
        this.inner = inner;
    }

    public int ProcessId => inner.ProcessId;

    public Task Completion => inner.Completion;

    public int? ExitCode => inner.ExitCode;

    public ValueTask DisposeAsync()
    {
        return inner.DisposeAsync();
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
        // The host is trusted orchestration code and spawns permission-scoped
        // workers; it launches with every Deno permission kind granted so a
        // worker's declared grants can never exceed the parent's (Deno refuses
        // escalation at spawn). The workers themselves narrow to the plugin's
        // manifest permissions via their own deno.permissions options.
        startInfo.ArgumentList.Add("--allow-read");
        startInfo.ArgumentList.Add("--allow-write");
        startInfo.ArgumentList.Add("--allow-net");
        startInfo.ArgumentList.Add("--allow-env");
        startInfo.ArgumentList.Add("--allow-run");
        startInfo.ArgumentList.Add("--allow-ffi");
        startInfo.ArgumentList.Add("--allow-sys");
        startInfo.ArgumentList.Add("--allow-import");
        startInfo.ArgumentList.Add("--no-prompt");
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

        var inner = DenoRunProcess.Start(
            startInfo,
            InternalDenoProcessKind.PluginHost,
            logger);
        logger.LogInformation("Plugin host started with pid {ProcessId}.", inner.ProcessId);
        return new PluginHostProcess(inner);
    }

    public Task StopAsync()
    {
        return inner.StopAsync();
    }
}
