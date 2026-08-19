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

    /// <summary>Named pipe name for the Windows credential bootstrap.</summary>
    public const string PipeName = "MAIEUTICS_REPL_PIPE";

    /// <summary>Address of the Deno permission broker (unix socket path on unix, named pipe on Windows),
    /// passed to internal Deno children so they reach the broker via <c>DENO_PERMISSION_BROKER_PATH</c>.</summary>
    public const string BrokerAddress = "DENO_PERMISSION_BROKER_PATH";
}