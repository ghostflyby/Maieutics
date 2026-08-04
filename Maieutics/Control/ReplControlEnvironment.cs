namespace Maieutics.Control;

internal static class ReplControlEnvironment
{
    /// <summary>Unix domain socket address of the REPL control channel.</summary>
    public const string IpcAddress = "MAIEUTICS_REPL_IPC";

    /// <summary>File URL of the materialized Deno REPL client module.</summary>
    public const string ClientModule = "MAIEUTICS_REPL_CLIENT";

    /// <summary>Session id the REPL child belongs to, used by the transport hello handshake.</summary>
    public const string SessionId = "MAIEUTICS_REPL_SESSION";
}
