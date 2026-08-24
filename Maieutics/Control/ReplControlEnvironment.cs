namespace Maieutics.Control;

internal static class ReplControlEnvironment
{
    /// <summary>Unix domain socket address of the REPL control channel.</summary>
    public const string IpcAddress = "MAIEUTICS_REPL_IPC";

    /// <summary>File URL of the materialized Deno REPL client module.</summary>
    public const string ClientModule = "MAIEUTICS_REPL_CLIENT";

    /// <summary>Session id the REPL child belongs to, used by the transport hello handshake.</summary>
    public const string SessionId = "MAIEUTICS_REPL_SESSION";

    /// <summary>Plugin host process identity, used by the plugin host hello handshake.</summary>
    public const string PluginHostId = "MAIEUTICS_PLUGIN_HOST_ID";

    /// <summary>Path of the kernel-written plugin configuration file.</summary>
    public const string PluginConfig = "MAIEUTICS_PLUGIN_CONFIG";

    /// <summary>File URL of the materialized plugin SDK module.</summary>
    public const string PluginSdk = "MAIEUTICS_PLUGIN_SDK";

    /// <summary>File URL of the materialized plugin worker entry module.</summary>
    public const string PluginWorkerEntry = "MAIEUTICS_PLUGIN_WORKER_ENTRY";

    /// <summary>
    ///     The host environment variables derived from the launch options: the broker address is
    ///     forwarded to the plugin host under <see cref="BrokerAddress"/> when a broker is
    ///     configured (ADR 0020). Used by <c>PluginHostProcess</c> at launch and by tests to
    ///     assert the env contract without spawning a process.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> FromHostOptions(Plugins.PluginHostProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Broker is { } broker
            ? new Dictionary<string, string?> { [BrokerAddress] = broker.Address }
            : new Dictionary<string, string?>();
    }

    /// <summary>Named pipe name for the Windows credential bootstrap.</summary>
    public const string PipeName = "MAIEUTICS_REPL_PIPE";

    /// <summary>
    ///     Address of the process-wide <c>DenoPermissionBroker</c>, carried to the plugin host so
    ///     it can forward <c>DENO_PERMISSION_BROKER_PATH</c> to a REPL process it derives (ADR
    ///     0020). This deliberately is <em>not</em> <c>DENO_PERMISSION_BROKER_PATH</c>: the host
    ///     itself must not consult the broker (it runs with full launch-time grants and no policy
    ///     is registered for it), so the kernel hands the address over under a separate name and
    ///     the host applies it to the REPL child only.
    /// </summary>
    public const string BrokerAddress = "MAIEUTICS_PERMISSION_BROKER";
}
