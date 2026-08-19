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
    string HostConfigFile,
    PluginHostProcessGrants Grants,
    DenoPermissionBroker Broker);

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

/// <summary>Thin adapter over <see cref="DenoRunProcess"/> for the out-of-process plugin host:
/// launches the restricted <c>deno run</c> process with the control channel address and the
/// kernel-written plugin configuration, and delegates drain, exit observation, and stop to the
/// shared internal Deno process module (ADR 0018 §8). The broker is the single permission
/// authority: the host launches with <c>DENO_PERMISSION_BROKER_PATH</c> and no <c>--allow-*</c>
/// flags, and the policy registered at spawn carries the plugin grants plus the host's own
/// control-channel grants.</summary>
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
        startInfo.ArgumentList.Add(options.HostModuleUrl);

        startInfo.EnvironmentVariables[ReplControlEnvironment.IpcAddress] = options.SocketPath;
        startInfo.EnvironmentVariables[ReplControlEnvironment.PluginHostId] = options.HostId;
        startInfo.EnvironmentVariables[ReplControlEnvironment.PluginConfig] = options.ConfigPath;
        startInfo.EnvironmentVariables[ReplControlEnvironment.PluginSdk] = options.SdkUrl;
        startInfo.EnvironmentVariables[ReplControlEnvironment.PluginWorkerEntry] = options.WorkerEntryUrl;
        startInfo.EnvironmentVariables[ReplControlEnvironment.BrokerAddress] = options.Broker.Address;
        foreach (var name in AllowedEnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) startInfo.EnvironmentVariables[name] = value;
        }

        var inner = DenoRunProcess.Start(
            startInfo,
            InternalDenoProcessKind.PluginHost,
            logger,
            options.Broker,
            BuildPolicy(options));
        logger.LogInformation("Plugin host started with pid {ProcessId}.", inner.ProcessId);
        return new PluginHostProcess(inner);
    }

    private static EffectivePolicy BuildPolicy(PluginHostProcessOptions options)
    {
        // The plugin grants (the union of each plugin's manifest permissions) overlay the built-in
        // baseline's control-channel grants. The broker resolves every request against this
        // composed policy, so the plugins' own grants (e.g. read "./") are enforced.
        var baseline = PermissionBaseline.ForPluginHost(
            options.ConfigPath,
            options.HostModuleUrl,
            options.SocketPath,
            options.SdkUrl,
            options.WorkerEntryUrl,
            options.HostId);
        var kinds = new Dictionary<PermissionKind, PermissionKindRules>(baseline.Kinds)
        {
            [PermissionKind.Read] = MergeKinds(baseline.For(PermissionKind.Read), options.Grants.ReadAll, options.Grants.Read),
            [PermissionKind.Write] = MergeKinds(baseline.For(PermissionKind.Write), options.Grants.WriteAll, options.Grants.Write),
            [PermissionKind.Net] = MergeKinds(baseline.For(PermissionKind.Net), options.Grants.NetAll, options.Grants.Net),
            [PermissionKind.Env] = MergeKinds(baseline.For(PermissionKind.Env), options.Grants.EnvAll, options.Grants.Env),
            [PermissionKind.Import] = MergeKinds(baseline.For(PermissionKind.Import), options.Grants.ImportAll, options.Grants.Import)
        };
        return new EffectivePolicy(kinds, baseline.Variables);
    }

    private static PermissionKindRules MergeKinds(
        PermissionKindRules baseline,
        bool grantAll,
        IReadOnlyList<string> grants)
    {
        var allow = new List<string>(baseline.Allow);
        allow.AddRange(grants.Distinct(StringComparer.Ordinal));
        return new PermissionKindRules
        {
            AllowAll = baseline.AllowAll || grantAll,
            DenyAll = baseline.DenyAll,
            Allow = allow,
            Deny = baseline.Deny
        };
    }

    public Task StopAsync()
    {
        return inner.StopAsync();
    }
}
