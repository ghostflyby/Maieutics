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

/// <summary>Reads a plugin's declaration files into a runtime descriptor without executing plugin code.</summary>
/// <remarks>
///     A plugin is a directory containing a <c>maieutics.json</c> (the plugin declaration: entrypoints,
///     dependencies, isolation) and optionally a <c>deno.json</c> (package identity: name used for
///     specifiers, exports exposed to other plugins for type imports, permissions). The two are
///     separated: exports never decide worker startup — only <c>maieutics.json</c> entrypoints do.
/// </remarks>
internal static class PluginManifest
{
    public static bool TryLoad(string directory, [NotNullWhen(true)] out PluginDescriptor? descriptor, out string error)
    {
        descriptor = null;
        var pluginConfigPath = Path.Combine(directory, "maieutics.json");
        if (!File.Exists(pluginConfigPath))
        {
            error = $"No maieutics.json found in '{directory}' (a Maieutics plugin is declared by maieutics.json).";
            return false;
        }

        MaieuticsManifestFile pluginManifest;
        try
        {
            pluginManifest = JsonSerializer.Deserialize(
                                 File.ReadAllText(pluginConfigPath),
                                 PluginManifestJsonContext.Default.MaieuticsManifestFile) ??
                             throw new JsonException("The manifest is null.");
        }
        catch (JsonException exception)
        {
            error = $"Invalid maieutics.json '{pluginConfigPath}': {exception.Message}";
            return false;
        }

        var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var name = ReadPackageName(directory, id);
        var workers = ReadEntrypoints(pluginManifest.Entrypoints, directory);
        var permissions = ReadPermissions(ReadPackagePermissions(directory));
        var isolation = pluginManifest.Isolation;
        var dependencies = pluginManifest.Dependencies ?? [];
        descriptor = new PluginDescriptor(id, name, directory, workers, permissions, isolation, dependencies);
        error = string.Empty;
        return true;
    }

    /// <summary>Reads the package name from deno.json (falling back to the directory name).</summary>
    private static string ReadPackageName(string directory, string fallback)
    {
        var denoJson = Path.Combine(directory, "deno.json");
        if (!File.Exists(denoJson)) return fallback;
        try
        {
            var manifest = JsonSerializer.Deserialize(
                               File.ReadAllText(denoJson),
                               PluginManifestJsonContext.Default.PluginManifestFile) ??
                           throw new JsonException("The manifest is null.");
            return manifest.Name ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static PluginManifestPermissionSet? ReadPackagePermissions(string directory)
    {
        var denoJson = Path.Combine(directory, "deno.json");
        if (!File.Exists(denoJson)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize(
                               File.ReadAllText(denoJson),
                               PluginManifestJsonContext.Default.PluginManifestFile) ??
                           throw new JsonException("The manifest is null.");
            return manifest.Permissions?.Default;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Reads the worker entrypoints from maieutics.json. Each entrypoint name becomes one worker
    ///     actor; the array lists the scripts of that worker (the first is the entry, the rest are
    ///     same-worker helpers — they never become separate workers). Every script path must resolve
    ///     inside the plugin directory; an escaping path (.. / symlink) is rejected for that entrypoint.
    /// </summary>
    private static IReadOnlyList<PluginWorkerDescriptor> ReadEntrypoints(
        IReadOnlyDictionary<string, string[]>? entrypoints,
        string directory)
    {
        if (entrypoints is null || entrypoints.Count == 0) return [];

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var workers = new List<PluginWorkerDescriptor>();
        foreach (var (entrypointName, scripts) in entrypoints)
        {
            if (string.IsNullOrWhiteSpace(entrypointName)) continue;
            if (scripts is null || scripts.Length == 0) continue;

            var entry = scripts[0];
            if (string.IsNullOrWhiteSpace(entry)) continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(root, entry));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            // Directory traversal protection: the resolved path must stay inside the plugin root.
            if (!IsWithinRoot(fullPath, root)) continue;

            workers.Add(new PluginWorkerDescriptor(entrypointName, new Uri(fullPath).AbsoluteUri));
        }
        return workers;
    }

    /// <summary>Whether <paramref name="fullPath"/> stays inside <paramref name="root"/> (no `..` escape).</summary>
    private static bool IsWithinRoot(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Resolves the local (file/relative) targets of a project's deno.json imports to their
    ///     package directories. Remote (jsr:/npm:/http) imports are skipped: they are resolved by the
    ///     Deno toolchain during install, not by the kernel.
    /// </summary>
    internal static IEnumerable<string> ReadLocalImportTargets(string projectDirectory)
    {
        var denoJson = Path.Combine(projectDirectory, "deno.json");
        if (!File.Exists(denoJson)) yield break;

        JsonElement imports;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(denoJson));
            imports = document.RootElement.TryGetProperty("imports", out var value)
                ? value.Clone()
                : default;
        }
        catch (JsonException)
        {
            yield break;
        }
        if (imports.ValueKind != JsonValueKind.Object) yield break;

        foreach (var property in imports.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            var target = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(target)) continue;
            if (target.StartsWith("jsr:", StringComparison.Ordinal) ||
                target.StartsWith("npm:", StringComparison.Ordinal) ||
                target.StartsWith("http", StringComparison.Ordinal))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(target, projectDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            yield return fullPath;
        }
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
[JsonSerializable(typeof(MaieuticsManifestFile))]
[JsonSerializable(typeof(PluginManifestFile))]
[JsonSerializable(typeof(PluginManifestPermissions))]
[JsonSerializable(typeof(PluginManifestPermissionSet))]
[JsonSerializable(typeof(PluginPermissionGrant))]
[JsonSerializable(typeof(PluginManifestMaieutics))]
internal sealed partial class PluginManifestJsonContext : JsonSerializerContext;

/// <summary>The plugin declaration file (maieutics.json): worker entrypoints, dependencies, isolation.</summary>
internal sealed record MaieuticsManifestFile(
    IReadOnlyDictionary<string, string[]>? Entrypoints = null,
    IReadOnlyList<string>? Dependencies = null,
    string? Isolation = null);

/// <summary>The package identity file (deno.json), read for name and permissions only.</summary>
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

/// <summary>Legacy deno.json <c>maieutics</c> field; retained for tolerant parsing, no longer authoritative.</summary>
internal sealed record PluginManifestMaieutics(
    string? Isolation = null,
    IReadOnlyList<string>? Dependencies = null);