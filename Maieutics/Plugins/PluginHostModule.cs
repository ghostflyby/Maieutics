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
        ("Maieutics.Deno.PluginHost.ts", "maieutics-plugin-host/mod.ts"),
        ("Maieutics.Deno.PluginHostImpl.ts", "maieutics-plugin-host/host.ts"),
        ("Maieutics.Deno.PluginHostWorker.ts", "maieutics-plugin-host/worker_entry.ts"),
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
        File.WriteAllText(ConfigFile, $"{{\"links\": [\"{sdkDirectory}\"]}}");
    }

    public string SdkUrl { get; }

    public string HostUrl { get; }

    public string WorkerEntryUrl { get; }

    /// <summary>Directory containing all materialized modules, for precise process read grants.</summary>
    public string ModuleDirectory { get; }

    /// <summary>Root deno.json whose `links` override the JSR-resolved SDK with the local copy.</summary>
    public string ConfigFile { get; }

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
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class PluginHostJsonContext : JsonSerializerContext;

internal sealed record PluginHostConfigFile(IReadOnlyList<PluginHostConfigPlugin> Plugins);

internal sealed record PluginHostConfigPlugin(
    string Id,
    string RootDir,
    IReadOnlyList<PluginHostConfigWorker> Workers,
    PluginHostConfigPermissions Permissions,
    IReadOnlyList<string> Dependencies);

internal sealed record PluginHostConfigWorker(string ExportName, string EntryUrl, string Specifier);

internal sealed record PluginHostConfigPermissions(
    JsonElement Env,
    JsonElement Net,
    JsonElement Read,
    JsonElement Write,
    JsonElement Run,
    JsonElement Ffi,
    JsonElement Sys,
    JsonElement Import);
