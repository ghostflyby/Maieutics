using Maieutics.Control;
using Maieutics.DenoRepl;
using Maieutics.Permissions;

namespace Maieutics.DenoExecution;

/// <summary>Builds the built-in baseline <see cref="EffectivePolicy"/> that internal Deno children
/// (REPL and plugin host) launch with. The broker resolves every permission check against this
/// snapshot; it carries exactly the control-channel, module-graph, and SDK grants the previous
/// launch-time flags expressed (ADR 0018 §2, §8; the broker is the single authority, so these are
/// the baseline the overlay layers extend). Deny rules still win over these grants.</summary>
internal static class PermissionBaseline
{
    /// <summary>Builds the baseline for the REPL child of one session.</summary>
    internal static EffectivePolicy ForDenoRepl(
        string moduleDirectory,
        string workingDirectory,
        string configFile,
        string lockFile,
        string ipcAddress,
        string esbuildWasm,
        string? windowsPipeName = null)
    {
        var read = new List<string>
        {
            moduleDirectory,
            workingDirectory,
            configFile,
            lockFile,
            esbuildWasm
        };
        var write = new List<string>();
        var net = new List<string>();
        var env = new List<string>
        {
            // Provider secret names are readable so Deno.env.get does not fail, but the values are
            // never injected into the child environment; evaluated cells observe them as undefined.
            "OPENAI_API_KEY",
            DenoReplEnvironment.SessionId,
            DenoReplEnvironment.Generation,
            DenoReplEnvironment.ClientModule,
            DenoReplEnvironment.IpcAddress,
            "DENO_DIR",
            "TMPDIR",
            "TMP",
            "TEMP"
        };

        if (OperatingSystem.IsWindows())
        {
            if (windowsPipeName is null)
                throw new ArgumentException(
                    "The Windows named-pipe bootstrap is not configured.",
                    nameof(windowsPipeName));
            net.Add(RequireLoopbackAddress(ipcAddress));
            env.Add(DenoReplEnvironment.PipeName);
            env.Add(DenoReplEnvironment.Credential);
            env.Add("SYSTEMROOT");
            // The Windows pipe bootstrap binds kernel32 before any control channel exists, so the
            // baseline carries the launch-time ffi grant; the broker gates post-bootstrap dlopen.
            // Re-verify path-qualified grants on Windows before narrowing (ADR 0018 §10).
            read.Add(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? string.Empty, "System32"));
        }
        else
        {
            var socketPath = Path.GetFullPath(ipcAddress);
            net.Add($"unix:{socketPath}");
            net.Add("localhost:80");
            read.Add(socketPath);
            write.Add(socketPath);
        }

        // The materialized module graph imports JSR packages over the network (the import map
        // resolves @ghostflyby/worker-actor and aves from jsr.io). The child's import and net
        // requests both carry the registry host:port, so the baseline grants import and net access
        // to jsr.io:443.
        var import = new List<string> { "jsr.io:443" };
        net.Add("jsr.io:443");
        PermissionKindRules ffi = new() { Allow = [] };
        if (OperatingSystem.IsWindows())
        {
            // The Windows pipe bootstrap dlopens kernel32 and uses Deno.UnsafePointer (which also
            // requires ffi access but carries no library path), so the baseline grants ffi fully
            // for the bootstrap window; the broker gates every post-bootstrap dlopen by default
            // (ADR 0018 §10; re-verify path-qualified grants on Windows before narrowing — the
            // UnsafePointer.of call makes a path-qualified grant insufficient for the bootstrap).
            ffi = new PermissionKindRules { AllowAll = true };
        }

        return PermissionLayerStore.Build(
            [
                new PermissionLayer
                {
                    Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                    {
                        [PermissionKind.Read] = new() { Allow = read },
                        [PermissionKind.Write] = new() { Allow = write },
                        [PermissionKind.Net] = new() { Allow = net },
                        [PermissionKind.Env] = new() { Allow = env },
                        [PermissionKind.Import] = new() { Allow = import },
                        [PermissionKind.Ffi] = ffi
                    }
                }
            ],
            new VariableTable(new EmptyVariableSource()));
    }

    /// <summary>Builds the baseline for the plugin host child (the union of plugin grants plus the
    /// host's own control-channel and SDK grants).</summary>
    internal static EffectivePolicy ForPluginHost(
        string configPath,
        string moduleDirectory,
        string socketPath,
        string sdkUrl,
        string workerEntryUrl,
        string hostId)
    {
        var read = new List<string> { configPath, moduleDirectory, socketPath };
        var write = new List<string> { socketPath };
        var net = new List<string> { "localhost", $"unix:{socketPath}" };
        var env = new List<string>
        {
            ReplControlEnvironment.IpcAddress,
            ReplControlEnvironment.PluginHostId,
            ReplControlEnvironment.PluginConfig,
            ReplControlEnvironment.PluginSdk,
            ReplControlEnvironment.PluginWorkerEntry
        };
        var import = new List<string> { "jsr.io:443" };
        net.Add("jsr.io:443");

        return PermissionLayerStore.Build(
            [
                new PermissionLayer
                {
                    Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                    {
                        [PermissionKind.Read] = new() { Allow = read },
                        [PermissionKind.Write] = new() { Allow = write },
                        [PermissionKind.Net] = new() { Allow = net },
                        [PermissionKind.Env] = new() { Allow = env },
                        [PermissionKind.Import] = new() { Allow = import }
                    }
                }
            ],
            new VariableTable(new EmptyVariableSource()));
    }

    private static string RequireLoopbackAddress(string address)
    {
        if (!Uri.TryCreate($"http://{address}", UriKind.Absolute, out var uri) ||
            !System.Net.IPAddress.TryParse(uri.Host, out var ipAddress) ||
            !System.Net.IPAddress.IsLoopback(ipAddress) || uri.Port <= 0)
            throw new InvalidOperationException("The Windows REPL endpoint must be a concrete loopback host and port.");
        return $"{ipAddress}:{uri.Port}";
    }

    private sealed class EmptyVariableSource : Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }
}
