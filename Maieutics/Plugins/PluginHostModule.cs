using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Plugins;

/// <summary>
///     Materializes the embedded plugin SDK, host entry, and worker entry modules to per-process
///     temp files and exposes their file URLs for host process injection.
/// </summary>
internal sealed class PluginHostModule
{
    private static readonly (string Resource, string RelativePath)[] Entries =
    [
        ("Maieutics.Deno.PluginSdk.ts", "maieutics-plugin-sdk/mod.ts"),
        ("Maieutics.Deno.PluginSdkEntry.ts", "maieutics-plugin-sdk/entry.ts"),
        ("Maieutics.Deno.PluginSdkRuntime.ts", "maieutics-plugin-sdk/runtime.ts"),
        ("Maieutics.Deno.PluginSdkInterop.ts", "maieutics-plugin-sdk/interop.ts"),
        ("Maieutics.Deno.PluginSdkActorRef.ts", "maieutics-plugin-sdk/actor_ref.ts"),
        ("Maieutics.Deno.PluginSdkReactive.ts", "maieutics-plugin-sdk/reactive.ts"),
        ("Maieutics.Deno.PluginSdkCollectionStream.ts", "maieutics-plugin-sdk/collection_stream.ts"),
        ("Maieutics.Deno.PluginSdkAdmission.ts", "maieutics-plugin-sdk/admission.ts"),
        ("Maieutics.Deno.PluginSdkHttp.ts", "maieutics-plugin-sdk/http.ts"),
        ("Maieutics.Deno.PluginSdkHttpCodec.ts", "maieutics-plugin-sdk/http_codec.ts"),
        ("Maieutics.Deno.PluginSdkLint.ts", "maieutics-plugin-sdk/lint-plugin.ts"),
        ("Maieutics.Deno.Widgets.Index.ts", "maieutics-plugin-sdk/widgets/index.ts"),
        ("Maieutics.Deno.Widgets.Runtime.ts", "maieutics-plugin-sdk/widgets/runtime.ts"),
        ("Maieutics.Deno.Widgets.Controls.ts", "maieutics-plugin-sdk/widgets/controls.ts"),
        ("Maieutics.Deno.Widgets.VNode.ts", "maieutics-plugin-sdk/widgets/vnode.ts"),
        ("Maieutics.Deno.Widgets.Transform.ts", "maieutics-plugin-sdk/widgets/transform.ts"),
        ("Maieutics.Deno.Widgets.Style.ts", "maieutics-plugin-sdk/widgets/style.ts"),
        ("Maieutics.Deno.Widgets.JsxRuntime.ts", "maieutics-plugin-sdk/widgets/jsx-runtime.ts"),
        ("Maieutics.Deno.PluginHost.ts", "maieutics-plugin-host/mod.ts"),
        ("Maieutics.Deno.PluginHostImpl.ts", "maieutics-plugin-host/host.ts"),
        ("Maieutics.Deno.PluginHostHttp.ts", "maieutics-plugin-host/http.ts"),
        ("Maieutics.Deno.PluginHostWorker.ts", "maieutics-plugin-host/worker_entry.ts"),
        ("Maieutics.Deno.PluginHostStorageEngine.ts", "maieutics-plugin-host/storage_engine.ts"),
        ("Maieutics.Deno.PluginHostStoragePool.ts", "maieutics-plugin-host/storage_pool.ts"),
        ("Maieutics.Deno.PluginHostStoragePoolWorker.ts", "maieutics-plugin-host/storage_pool_worker.ts"),
        ("Maieutics.Deno.PluginHostReplManager.ts", "maieutics-plugin-host/repl_manager.ts"),
        ("Maieutics.Deno.PluginHostReplProtocol.ts", "maieutics-plugin-host/host_repl_protocol.ts"),
        ("Maieutics.Deno.Runtime.BootstrapContract.ts", "maieutics-runtime/bootstrap_contract.ts"),
        ("Maieutics.Deno.Runtime.WorkerBootstrap.ts", "maieutics-runtime/worker_bootstrap.ts"),
        ("Maieutics.Deno.Runtime.WorkerFactory.ts", "maieutics-runtime/worker_factory.ts"),
        ("Maieutics.Deno.Runtime.WorkerPatch.ts", "maieutics-runtime/worker_patch.ts"),
        ("Maieutics.Deno.Runtime.StorageChannel.ts", "maieutics-runtime/storage_channel.ts"),
        ("Maieutics.Deno.Shared.Protocol.ts", "shared/protocol.ts"),
        ("Maieutics.Deno.Shared.Bus.ts", "shared/bus.ts"),
        ("Maieutics.Deno.Shared.IpcWebSocket.ts", "shared/ipc_websocket.ts"),
        ("Maieutics.Deno.PluginSdkConfig.json", "maieutics-plugin-sdk/deno.json")
    ];

    public PluginHostModule()
    {
        ModuleDirectory = Path.Combine(Path.GetTempPath(), $"mc-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ModuleDirectory);
        foreach (var (resource, relativePath) in Entries)
            WriteEmbedded(resource, Path.Combine(ModuleDirectory, relativePath));

        SdkUrl = new Uri(Path.Combine(ModuleDirectory, "maieutics-plugin-sdk/mod.ts")).AbsoluteUri;
        HostUrl = new Uri(Path.Combine(ModuleDirectory, "maieutics-plugin-host/mod.ts")).AbsoluteUri;
        WorkerEntryUrl = new Uri(
            Path.Combine(ModuleDirectory, "maieutics-plugin-host/worker_entry.ts")).AbsoluteUri;
        var sdkDirectory = new Uri(Path.Combine(ModuleDirectory, "maieutics-plugin-sdk")).AbsoluteUri;
        ConfigFile = Path.Combine(ModuleDirectory, "deno.json");
        SdkDirectory = sdkDirectory;
    }

    /// <summary>Import-map keys owned by the host materialization itself. Plugin
    /// imports colliding with these are skipped: overriding them would redirect the
    /// host or the SDK away from the pinned copies.</summary>
    public static readonly IReadOnlySet<string> ReservedImportKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "@ghostflyby/worker-actor",
            "@preact/signals-core",
            "@maieutics/plugin-sdk"
        };

    /// <summary>Writes the root <c>deno.json</c> once the plugin set is known: the
    /// host machinery mappings, the merged plugin import entries (see
    /// PluginImportMerger), and the <c>links</c> override that keeps the SDK resolving
    /// to the local copy instead of the registry. Called from
    /// <see cref="PluginHostManager.Start"/> after the plugin scan; the file does not
    /// exist before this point.</summary>
    public void WriteRootConfig(IReadOnlyList<KeyValuePair<string, string>> mergedImports)
    {
        ArgumentNullException.ThrowIfNull(mergedImports);
        using var stream = File.Open(ConfigFile, FileMode.Create, FileAccess.Write);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("imports");
            writer.WriteString("@ghostflyby/worker-actor", "jsr:@ghostflyby/worker-actor@0.6.0");
            writer.WriteString("@preact/signals-core", "npm:@preact/signals-core@1.14.4");
            foreach (var (key, value) in mergedImports)
            {
                writer.WriteString(key, value);
            }
            writer.WriteEndObject();
            writer.WriteStartArray("links");
            writer.WriteStringValue(SdkDirectory);
            writer.WriteEndArray();
            writer.WriteNumber("minimumDependencyAge", 0);
            writer.WriteEndObject();
        }
    }

    public string SdkUrl { get; }

    public string HostUrl { get; }

    public string WorkerEntryUrl { get; }

    /// <summary>Directory containing all materialized modules, for precise process read grants.</summary>
    public string ModuleDirectory { get; }

    /// <summary>Root deno.json whose `links` override the JSR-resolved SDK with the local copy.</summary>
    public string ConfigFile { get; }

    private string SdkDirectory { get; }

    private static void WriteEmbedded(string resourceName, string path)
    {
        using var stream = typeof(PluginHostModule).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Missing embedded Deno module '{resourceName}'.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        Directory.CreateDirectory(
            Path.GetDirectoryName(path) ??
            throw new InvalidOperationException($"Cannot resolve the directory for '{path}'."));
        File.WriteAllText(path, source);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PluginHostConfigFile))]
[JsonSerializable(typeof(PluginHostConfigPlugin))]
[JsonSerializable(typeof(PluginHostConfigWorker))]
[JsonSerializable(typeof(PluginHostConfigPermissions))]
[JsonSerializable(typeof(PluginHostConfigStorage))]
[JsonSerializable(typeof(PluginReloadPayload))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class PluginHostJsonContext : JsonSerializerContext;

internal sealed record PluginHostConfigFile(
    IReadOnlyList<PluginHostConfigPlugin> Plugins,
    string? StorageDataRoot = null);

internal sealed record PluginHostConfigPlugin(
    string Id,
    string RootDir,
    IReadOnlyList<PluginHostConfigWorker> Workers,
    PluginHostConfigPermissions Permissions,
    IReadOnlyList<string> Dependencies,
    PluginHostConfigStorage? Storage = null);

internal sealed record PluginHostConfigWorker(string ExportName, string EntryUrl, string Specifier);

/// <summary>The kernel-assisted persistent-storage directory for one plugin (ADR 0022): the
/// authoritative store lives in the Deno host process and persists here. The kernel derives the
/// path; the host never does.</summary>
internal sealed record PluginHostConfigStorage(string DataDir);

internal sealed record PluginHostConfigPermissions(
    JsonElement Env,
    JsonElement Net,
    JsonElement Read,
    JsonElement Write,
    JsonElement Run,
    JsonElement Ffi,
    JsonElement Sys,
    JsonElement Import);

/// <summary>Payload for the in-process <c>plugin.reload</c> bus message: the target worker plus the
/// plugin's full replacement config (permissions, workers, dependencies) so the host can rebuild
/// the worker with the new grants. <see cref="Plugin"/> is null for a pure source-change reload
/// (same config, new module text).</summary>
internal sealed record PluginReloadPayload(
    string PluginId,
    string ExportName,
    PluginHostConfigPlugin? Plugin);
