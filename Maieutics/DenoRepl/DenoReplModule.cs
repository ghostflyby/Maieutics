namespace Maieutics.DenoRepl;

/// <summary>
///     Materializes the Deno REPL host and script client into one per-process module root.
/// </summary>
internal sealed class DenoReplModule
{
    private static readonly (string Resource, string RelativePath)[] Entries =
    [
        ("Maieutics.Deno.DenoRepl.Main.ts", "maieutics-deno-repl/main.ts"),
        ("Maieutics.Deno.DenoRepl.Protocol.ts", "maieutics-deno-repl/protocol.ts"),
        ("Maieutics.Deno.DenoRepl.Client.ts", "maieutics-deno-repl/repl_client.ts"),
        ("Maieutics.Deno.DenoRepl.Actor.ts", "maieutics-deno-repl/repl_actor.ts"),
        ("Maieutics.Deno.DenoRepl.Worker.ts", "maieutics-deno-repl/repl_worker.ts"),
        ("Maieutics.Deno.DenoRepl.InputMailbox.ts", "maieutics-deno-repl/input_mailbox.ts"),
        ("Maieutics.Deno.DenoRepl.Queue.ts", "maieutics-deno-repl/repl_eval_queue.ts"),
        ("Maieutics.Deno.DenoRepl.ProcessMain.ts", "maieutics-deno-repl/process_main.ts"),
        ("Maieutics.Deno.DenoRepl.ProcessRpc.ts", "maieutics-deno-repl/process_rpc.ts"),
        ("Maieutics.Deno.DenoRepl.Config.json", "maieutics-deno-repl/deno.json"),
        ("Maieutics.Deno.DenoRepl.Lock.json", "maieutics-deno-repl/deno.lock"),
        ("Maieutics.Deno.ReplClient.ts", "maieutics-repl-client/mod.ts"),
        ("Maieutics.Deno.ReplClientComm.ts", "maieutics-repl-client/comm.ts"),
        ("Maieutics.Deno.ReplClientWindowsBootstrap.ts", "maieutics-repl-client/windows_bootstrap.ts"),
        ("Maieutics.Deno.Shared.Protocol.ts", "shared/protocol.ts"),
        ("Maieutics.Deno.Shared.Bus.ts", "shared/bus.ts"),
        ("Maieutics.Deno.Shared.IpcWebSocket.ts", "shared/ipc_websocket.ts")
    ];

    private readonly Lazy<MaterializedModules> modules =
        new(Materialize, LazyThreadSafetyMode.ExecutionAndPublication);

    internal string ClientUrl => modules.Value.ClientUrl;

    internal string MainUrl => modules.Value.MainUrl;

    /// <summary>File URL of the REPL <em>process</em> entry (<c>process_main.ts</c>), the child
    /// module the plugin host derives via worker-actor <c>spawnProcess</c> (ADR 0020). It is
    /// materialized beside <c>deno.json</c>, so the host-derived child resolves its
    /// <c>@ghostflyby/worker-actor</c> import through the config discovered upward from the entry
    /// module.</summary>
    internal string ProcessMainUrl => modules.Value.ProcessMainUrl;

    internal string ConfigFile => modules.Value.ConfigFile;

    internal string LockFile => modules.Value.LockFile;

    internal string ModuleDirectory => modules.Value.ModuleDirectory;

    private static MaterializedModules Materialize()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-repl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (resource, relativePath) in Entries) WriteEmbedded(resource, Path.Combine(root, relativePath));

        return new MaterializedModules(
            new Uri(Path.Combine(root, "maieutics-repl-client/mod.ts")).AbsoluteUri,
            new Uri(Path.Combine(root, "maieutics-deno-repl/main.ts")).AbsoluteUri,
            new Uri(Path.Combine(root, "maieutics-deno-repl/process_main.ts")).AbsoluteUri,
            Path.Combine(root, "maieutics-deno-repl/deno.json"),
            Path.Combine(root, "maieutics-deno-repl/deno.lock"),
            root);
    }

    internal static void WriteEmbedded(string resourceName, string path)
    {
        using var stream = typeof(DenoReplModule).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Missing embedded Deno module '{resourceName}'.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        Directory.CreateDirectory(
            Path.GetDirectoryName(path) ??
            throw new InvalidOperationException($"Cannot resolve the directory for '{path}'."));
        File.WriteAllText(path, source);
    }

    private sealed record MaterializedModules(
        string ClientUrl,
        string MainUrl,
        string ProcessMainUrl,
        string ConfigFile,
        string LockFile,
        string ModuleDirectory);
}
