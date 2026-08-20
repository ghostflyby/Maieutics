using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Plugins;

internal sealed record PluginDescriptor(
    string Id,
    string Name,
    string RootDirectory,
    IReadOnlyList<PluginWorkerDescriptor> Workers,
    PluginPermissionGrants Permissions,
    string? Isolation,
    IReadOnlyList<string> Dependencies);

internal sealed record PluginWorkerDescriptor(string ExportName, string EntryUrl);

internal sealed record PluginPermissionGrants(
    PluginPermissionGrant Env,
    PluginPermissionGrant Net,
    PluginPermissionGrant Read,
    PluginPermissionGrant Write,
    PluginPermissionGrant Run,
    PluginPermissionGrant Ffi,
    PluginPermissionGrant Sys,
    PluginPermissionGrant Import);

[JsonConverter(typeof(PluginPermissionGrantJsonConverter))]
internal sealed record PluginPermissionGrant(bool AllowAll, IReadOnlyList<string> Values)
{
    public static readonly PluginPermissionGrant None = new(false, []);
    public static readonly PluginPermissionGrant All = new(true, []);
}

internal sealed class PluginPermissionGrantJsonConverter : JsonConverter<PluginPermissionGrant>
{
    public override PluginPermissionGrant Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => PluginPermissionGrant.All,
            JsonTokenType.False => PluginPermissionGrant.None,
            JsonTokenType.Null => PluginPermissionGrant.None,
            JsonTokenType.StartArray => ReadValues(ref reader),
            _ => PluginPermissionGrant.None
        };
    }

    public override void Write(Utf8JsonWriter writer, PluginPermissionGrant value, JsonSerializerOptions options)
    {
        if (value.AllowAll)
        {
            writer.WriteBooleanValue(true);
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value.Values) writer.WriteStringValue(item);

        writer.WriteEndArray();
    }

    private static PluginPermissionGrant ReadValues(ref Utf8JsonReader reader)
    {
        var array = JsonElement.ParseValue(ref reader);
        var values = array.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .OfType<string>()
            .Where(value => !string.IsNullOrEmpty(value))
            .ToArray();
        return new PluginPermissionGrant(false, values);
    }
}

/// <summary>Reads a plugin's deno.json into a runtime descriptor without executing plugin code.</summary>
internal static class PluginManifest
{
    public static bool TryLoad(string directory, [NotNullWhen(true)] out PluginDescriptor? descriptor, out string error)
    {
        descriptor = null;
        var configPath = FindConfig(directory);
        if (configPath is null)
        {
            error = $"No deno.json or deno.jsonc found in '{directory}'.";
            return false;
        }

        PluginManifestFile manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                           File.ReadAllText(configPath),
                           PluginManifestJsonContext.Default.PluginManifestFile) ??
                       throw new JsonException("The manifest is null.");
        }
        catch (JsonException exception)
        {
            error = $"Invalid deno.json '{configPath}': {exception.Message}";
            return false;
        }

        if (manifest.Maieutics is null)
        {
            error = $"'{configPath}' is not a Maieutics plugin (missing the 'maieutics' marker).";
            return false;
        }

        var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var name = manifest.Name ?? id;
        var workers = ReadWorkers(manifest.Exports, directory);
        var permissions = ReadPermissions(manifest.Permissions?.Default);
        var isolation = manifest.Maieutics.Isolation;
        var dependencies = manifest.Maieutics.Dependencies ?? [];
        descriptor = new PluginDescriptor(id, name, directory, workers, permissions, isolation, dependencies);
        error = string.Empty;
        return true;
    }

    private static string? FindConfig(string directory)
    {
        var denoJson = Path.Combine(directory, "deno.json");
        if (File.Exists(denoJson)) return denoJson;

        var denoJsonc = Path.Combine(directory, "deno.jsonc");
        return File.Exists(denoJsonc) ? denoJsonc : null;
    }

    private static IReadOnlyList<PluginWorkerDescriptor> ReadWorkers(JsonElement? exports, string directory)
    {
        if (exports is not { ValueKind: JsonValueKind.Object } exportsObject) return [];

        var workers = new List<PluginWorkerDescriptor>();
        foreach (var property in exportsObject.EnumerateObject())
        {
            if (property.Name == ".") continue;

            if (property.Value.ValueKind != JsonValueKind.String) continue;

            var relative = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(relative)) continue;

            var fullPath = Path.GetFullPath(Path.Combine(directory, relative));
            workers.Add(new PluginWorkerDescriptor(property.Name, new Uri(fullPath).AbsoluteUri));
        }

        return workers;
    }

    private static PluginPermissionGrants ReadPermissions(PluginManifestPermissionSet? set)
    {
        return new PluginPermissionGrants(
            set?.Env ?? PluginPermissionGrant.None,
            set?.Net ?? PluginPermissionGrant.None,
            set?.Read ?? PluginPermissionGrant.None,
            set?.Write ?? PluginPermissionGrant.None,
            set?.Run ?? PluginPermissionGrant.None,
            set?.Ffi ?? PluginPermissionGrant.None,
            set?.Sys ?? PluginPermissionGrant.None,
            set?.Import ?? PluginPermissionGrant.None);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowOutOfOrderMetadataProperties = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(PluginManifestFile))]
[JsonSerializable(typeof(PluginManifestPermissions))]
[JsonSerializable(typeof(PluginManifestPermissionSet))]
[JsonSerializable(typeof(PluginPermissionGrant))]
[JsonSerializable(typeof(PluginManifestMaieutics))]
internal sealed partial class PluginManifestJsonContext : JsonSerializerContext;

internal sealed record PluginManifestFile(
    string? Name = null,
    JsonElement? Exports = null,
    PluginManifestPermissions? Permissions = null,
    PluginManifestMaieutics? Maieutics = null);

internal sealed record PluginManifestPermissions(PluginManifestPermissionSet? Default = null);

internal sealed record PluginManifestPermissionSet(
    PluginPermissionGrant? Env = null,
    PluginPermissionGrant? Net = null,
    PluginPermissionGrant? Read = null,
    PluginPermissionGrant? Write = null,
    PluginPermissionGrant? Run = null,
    PluginPermissionGrant? Ffi = null,
    PluginPermissionGrant? Sys = null,
    PluginPermissionGrant? Import = null);

internal sealed record PluginManifestMaieutics(
    string? Isolation = null,
    IReadOnlyList<string>? Dependencies = null);