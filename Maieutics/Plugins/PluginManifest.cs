using System.Diagnostics.CodeAnalysis;
using Maieutics.Control;
using Maieutics.Mcp;
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
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<PluginImportEntry> Imports,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<PluginExtensionEntry> Extensions,
    IReadOnlyList<PluginDataEntry> DataEntries,
    IReadOnlyList<McpServerDefinition> McpServers,
    IReadOnlyList<PluginTrigger> Triggers,
    IReadOnlyList<string> ExtensionDiagnostics,
    bool InspectionsContentReadAll,
    string? McpServersError = null,
    PluginUiFormDefinition? UiForm = null);

/// <summary>One declarative extension entry from the manifest's `extensions` section:
/// a kernel-known kind plus the raw data the kind's kernel interpreter consumes.
/// Entries are data-only — they never require or imply a worker.</summary>
internal sealed record PluginExtensionEntry(string Kind, JsonElement Data);

/// <summary>The kernel-known declarative extension kinds (the manifest counterpart of
/// the closed extension-point catalog). Unknown kinds in a manifest are surfaced as
/// diagnostics and ignored, so newer plugins degrade visibly on older kernels.</summary>
internal static class PluginExtensionKind
{
    public const string McpDiscover = "McpDiscover";

    public static bool IsKnown(string kind)
    {
        return kind == McpDiscover;
    }
}

/// <summary>One data entry point declared in the manifest's `entrypoints` section with a
/// plain string value: the kernel collects the referenced file as raw JSON instead of
/// launching it. Interpretation belongs to the entry name's kernel interpreter
/// (<see cref="PluginDataName"/>); a collection failure rides the entry as an error
/// marker and never fails the plugin load.</summary>
internal sealed record PluginDataEntry(string Name, JsonElement? Data, string? Error);

/// <summary>The kernel-known data entry names. A catalogued name requires its string
/// entrypoint value to resolve to a file the kernel can interpret; unknown names are
/// collected but inert, with a visible diagnostic.</summary>
internal static class PluginDataName
{
    public const string Mcp = "mcp";

    /// <summary>A declarative form template (ADR 0038 stage 3): interpreted into a
    /// <see cref="PluginUiFormDefinition"/> the kernel can publish into the foreground
    /// session's comm plane on demand — no worker runs for a data-only form.</summary>
    public const string Ui = "ui";

    public static bool IsKnown(string name)
    {
        return name == Mcp || name == Ui;
    }
}

/// <summary>One declarative form field interpreted from a `ui` data entry.</summary>
internal sealed record PluginUiFormField(
    string Name,
    string? Label,
    string Type,
    IReadOnlyList<PluginUiFormChoice>? Choices,
    bool? Required,
    string? Placeholder);

/// <summary>One choice option of a declarative form field.</summary>
internal sealed record PluginUiFormChoice(string Value, string Label);

/// <summary>A `ui` data entry interpreted into a publishable form template: the kernel
/// maps declared actions onto grants the plugin already holds (capability-gated), so a
/// data-only form never runs code (ADR 0038 stage 3).</summary>
internal sealed record PluginUiFormDefinition(
    string? Title,
    IReadOnlyList<PluginUiFormField> Fields,
    string? SubmitLabel,
    string? CancelLabel,
    string? SubmitTool,
    string? Error);

/// <summary>Interprets the raw `ui` data-entry JSON into a definition; a malformed
/// entry produces a definition carrying the error (the sticky-marker pattern the
/// `mcp` entry uses — the plugin stays loaded, the form is unusable).</summary>
internal static class PluginUiFormInterpreter
{
    internal static PluginUiFormDefinition InterpretUiForm(JsonElement data)
    {
    try
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return new PluginUiFormDefinition(null, [], null, null, null, "The ui data entry must be a JSON object.");
        }

        string? title = data.TryGetProperty("title", out var titleElement) &&
            titleElement.ValueKind == JsonValueKind.String ? titleElement.GetString() : null;

        var fields = new List<PluginUiFormField>();
        if (data.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var fieldElement in fieldsElement.EnumerateArray())
            {
                if (fieldElement.ValueKind != JsonValueKind.Object ||
                    !fieldElement.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String ||
                    nameElement.GetString() is not { Length: > 0 } name)
                {
                    fields.Add(new PluginUiFormField("", null, "", null, null, null));
                    continue;
                }

                var type = fieldElement.TryGetProperty("type", out var typeElement) &&
                    typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : "text";

                IReadOnlyList<PluginUiFormChoice>? choices = null;
                if (fieldElement.TryGetProperty("choices", out var choicesElement) &&
                    choicesElement.ValueKind == JsonValueKind.Array)
                {
                    var parsed = new List<PluginUiFormChoice>();
                    foreach (var choice in choicesElement.EnumerateArray())
                    {
                        var value = choice.ValueKind switch
                        {
                            JsonValueKind.String => choice.GetString(),
                            JsonValueKind.Number => choice.GetRawText(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            _ => null,
                        };
                        if (value is not null) parsed.Add(new PluginUiFormChoice(value, value));
                    }

                    choices = parsed;
                }

                fields.Add(new PluginUiFormField(
                    name,
                    fieldElement.TryGetProperty("label", out var labelElement) &&
                        labelElement.ValueKind == JsonValueKind.String ? labelElement.GetString() : null,
                    type ?? "text",
                    choices,
                    fieldElement.TryGetProperty("required", out var requiredElement) &&
                        requiredElement.ValueKind == JsonValueKind.True,
                    fieldElement.TryGetProperty("placeholder", out var placeholderElement) &&
                        placeholderElement.ValueKind == JsonValueKind.String ? placeholderElement.GetString() : null));
            }
        }
        else
        {
            return new PluginUiFormDefinition(title, [], null, null, null, "The ui data entry requires a fields array.");
        }

        return new PluginUiFormDefinition(
            title,
            fields,
            data.TryGetProperty("submitLabel", out var submitElement) &&
                submitElement.ValueKind == JsonValueKind.String ? submitElement.GetString() : null,
            data.TryGetProperty("cancelLabel", out var cancelElement) &&
                cancelElement.ValueKind == JsonValueKind.String ? cancelElement.GetString() : null,
            data.TryGetProperty("submitTool", out var submitToolElement) &&
                submitToolElement.ValueKind == JsonValueKind.String ? submitToolElement.GetString() : null,
            null);
    }
    catch (Exception exception) when (exception is InvalidOperationException or JsonException)
    {
        return new PluginUiFormDefinition(null, [], null, null, null, $"Invalid ui data entry: {exception.Message}");
    }
    }
}

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
    /// <summary>The entrypoints key holding the worker map (worker name → script array).
    /// Sibling keys are data entry names (ADR 0033).</summary>
    private const string WorkerSectionName = "worker";

    public static bool TryLoad(
        string directory,
        Maieutics.Permissions.VariableTable? triggerVariables,
        [NotNullWhen(true)] out PluginDescriptor? descriptor,
        out string error)
    {
        return TryLoad(directory, out descriptor, out error, triggerVariables);
    }

    public static bool TryLoad(string directory, [NotNullWhen(true)] out PluginDescriptor? descriptor, out string error)
    {
        return TryLoad(directory, out descriptor, out error, null);
    }

    private static bool TryLoad(
        string directory,
        [NotNullWhen(true)] out PluginDescriptor? descriptor,
        out string error,
        Maieutics.Permissions.VariableTable? triggerVariables)
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
        IReadOnlyList<PluginWorkerDescriptor> workers;
        IReadOnlyList<PluginDataEntry> dataEntries;
        IReadOnlyList<string> dataDiagnostics;
        try
        {
            (workers, dataEntries, dataDiagnostics) = ReadEntrypoints(pluginManifest.Entrypoints, directory);
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
        var permissions = ReadPermissions(ReadPackagePermissions(directory));
        var isolation = pluginManifest.Isolation;
        var dependencies = pluginManifest.Dependencies ?? [];
        var imports = ReadImports(directory);
        // Capabilities are deny-by-default: only names the core's catalog knows are
        // kept, so a manifest can never grant a capability the kernel has not defined.
        var capabilities = (pluginManifest.Capabilities ?? [])
            .Where(PluginCapabilityCatalog.Contains)
            .ToArray();
        IReadOnlyList<PluginExtensionEntry> extensions;
        IReadOnlyList<string> extensionDiagnostics;
        try
        {
            extensions = ReadExtensions(pluginManifest.Extensions, out extensionDiagnostics);
        }
        catch (JsonException exception)
        {
            error = $"Invalid 'extensions' section: {exception.Message}";
            return false;
        }

        // UI interpretation of the declared data entry (ADR 0038 stage 3): a declarative
        // form template the kernel can publish on demand. Same sticky-marker shape as
        // the `mcp` entry — a broken entry never fails the plugin load.
        PluginUiFormDefinition? uiForm = null;
        if (dataEntries.FirstOrDefault(entry => entry.Name == PluginDataName.Ui) is { } uiEntry)
        {
            if (uiEntry.Error is { } uiCollectionError)
            {
                uiForm = new PluginUiFormDefinition(null, [], null, null, null,
                    $"The 'ui' data entry could not be collected: {uiCollectionError}");
            }
            else if (uiEntry.Data is { } uiData)
            {
                uiForm = PluginUiFormInterpreter.InterpretUiForm(uiData);
            }
        }

        // MCP interpretation of the declared data entry (ADR 0033). The 'mcp' name is
        // the only source — there is no implicit file pickup beside the manifest. A
        // broken entry does not fail the plugin load: the manifest declarations
        // (entrypoints, capabilities, extensions) stay authoritative, and the error
        // marker keeps the plugin registered so its previous MCP contribution remains
        // discoverable-but-failed — the sticky-last-good rule the coordinator applies.
        IReadOnlyList<McpServerDefinition> mcpServers = [];
        string? mcpServersError = null;
        if (dataEntries.FirstOrDefault(entry => entry.Name == PluginDataName.Mcp) is { } mcpEntry)
        {
            if (mcpEntry.Error is { } collectionError)
                mcpServersError = $"The 'mcp' data entry could not be collected: {collectionError}";
            else if (mcpEntry.Data is { } collectedData)
            {
                try
                {
                    mcpServers = McpServerFile.ReadJson(collectedData, directory, $"plugin:{id}::");
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or JsonException or InvalidDataException)
                {
                    mcpServersError = $"Invalid mcp.json data entry: {exception.Message}";
                }
            }
        }

        // Inspections are the content-observation declaration (ADR 0032): a plugin that
        // declares contentReadAll receives tool results in its post-invoke hooks. Absence
        // means the hooks fire without result payloads.
        var contentReadAll = false;
        if (pluginManifest.Inspections is { ValueKind: JsonValueKind.Object } inspections)
        {
            if (inspections.TryGetProperty("contentReadAll", out var readAll))
            {
                if (readAll.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "Invalid 'inspections' section: contentReadAll must be a boolean.";
                    return false;
                }

                contentReadAll = readAll.GetBoolean();
            }
        }

        // Triggers (ADR 0036): the kernel owns the declaration plane — kinds are a
        // closed catalog and watch paths expand through the permission store's
        // variable table, so a trigger can never widen what the plugin may read.
        IReadOnlyList<PluginTrigger> triggers;
        try
        {
            triggers = PluginTriggerReader.Read(
                pluginManifest.Triggers,
                triggerVariables ?? EmptyVariables());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or Maieutics.Permissions.PermissionException)
        {
            error = exception.Message;
            return false;
        }

        var declarativeDiagnostics = new List<string>(extensionDiagnostics);
        declarativeDiagnostics.AddRange(dataDiagnostics);

        descriptor = new PluginDescriptor(
            id,
            name,
            directory,
            workers,
            permissions,
            isolation,
            dependencies,
            imports,
            capabilities,
            extensions,
            dataEntries,
            mcpServers,
            triggers,
            declarativeDiagnostics,
            contentReadAll,
            mcpServersError,
            uiForm);
        error = string.Empty;
        return true;
    }

    /// <summary>Reads the manifest's `entrypoints` section. The top-level key is the
    /// entry KIND, never a name: `worker` holds the worker map (worker name → script
    /// array, the first script starting one worker, the rest same-worker helpers);
    /// catalogued data names (`mcp`, <see cref="PluginDataName"/>) hold a string path to
    /// a file the kernel collects and interprets instead of launching (ADR 0033);
    /// unknown names holding a string are collected but inert with a visible diagnostic
    /// (forward compatibility, same rule as unknown extension kinds). Structural
    /// violations fail the manifest — including an array under an unknown name, which
    /// is almost certainly a pre-hierarchy worker declaration missing its `worker`
    /// wrapper. A data file that cannot be collected only marks its entry.</summary>
    private static (
        IReadOnlyList<PluginWorkerDescriptor> Workers,
        IReadOnlyList<PluginDataEntry> DataEntries,
        IReadOnlyList<string> Diagnostics)
        ReadEntrypoints(JsonElement? entrypoints, string directory)
    {
        var workers = new List<PluginWorkerDescriptor>();
        var dataEntries = new List<PluginDataEntry>();
        var diagnostics = new List<string>();
        if (entrypoints is not { ValueKind: JsonValueKind.Object } section)
        {
            if (entrypoints is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
                throw new JsonException("The 'entrypoints' section must be an object.");
            return (workers, dataEntries, diagnostics);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        foreach (var entry in section.EnumerateObject())
        {
            if (string.Equals(entry.Name, WorkerSectionName, StringComparison.Ordinal))
            {
                if (entry.Value.ValueKind is not JsonValueKind.Object)
                    throw new JsonException(
                        "The 'worker' entrypoints section must be an object of worker name → script array.");
                foreach (var worker in entry.Value.EnumerateObject())
                    if (TryReadWorker(root, worker) is { } descriptor)
                        workers.Add(descriptor);
                continue;
            }

            switch (entry.Value.ValueKind)
            {
                case JsonValueKind.String:
                    var dataPath = entry.Value.GetString();
                    if (string.IsNullOrWhiteSpace(dataPath))
                    {
                        dataEntries.Add(new PluginDataEntry(entry.Name, null, "The data entry path is empty."));
                        break;
                    }

                    if (!PluginDataName.IsKnown(entry.Name))
                        diagnostics.Add(
                            $"Unknown data entry '{entry.Name}' is declared but not supported by this kernel; " +
                            $"it is collected but inert (known names: {PluginDataName.Mcp}).");
                    dataEntries.Add(CollectDataEntry(root, entry.Name, dataPath));
                    break;

                case JsonValueKind.Array when PluginDataName.IsKnown(entry.Name):
                    throw new JsonException(
                        $"The entrypoint '{entry.Name}' is a catalogued data entry and requires a string path, not a script array.");

                default:
                    throw new JsonException(
                        $"Unknown entrypoint section '{entry.Name}'. Worker declarations live under 'worker'; " +
                        $"catalogued data entries take a string path (known names: {PluginDataName.Mcp}).");
            }
        }

        return (workers, dataEntries, diagnostics);
    }

    /// <summary>Reads one code entrypoint's script array. The first non-empty script is
    /// the worker's entry module and must resolve inside the plugin root; anything else
    /// is a tolerant skip, matching the historical behavior for malformed arrays.</summary>
    private static PluginWorkerDescriptor? TryReadWorker(string root, JsonProperty entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name)) return null;
        // The manifest export name is reserved for declarative entries: a worker
        // export with this name would be shadowed by the manifest discovery branch.
        if (string.Equals(entry.Name, PluginHostManager.ManifestExportName, StringComparison.Ordinal))
            throw new JsonException(
                $"The entrypoint name '{PluginHostManager.ManifestExportName}' is reserved.");
        if (entry.Value.ValueKind is not JsonValueKind.Array)
            throw new JsonException(
                $"The worker '{entry.Name}' must be declared as a script array.");

        var entryScript = entry.Value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        if (entryScript is null) return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(root, entryScript));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // Directory traversal protection: the resolved path must stay inside the plugin root.
        if (!IsWithinRoot(fullPath, root)) return null;

        return new PluginWorkerDescriptor(entry.Name, new Uri(fullPath).AbsoluteUri);
    }

    private static Maieutics.Permissions.VariableTable EmptyVariables()
    {
        return new Maieutics.Permissions.VariableTable(
            new EmptyTriggerVariableSource());
    }

    private sealed class EmptyTriggerVariableSource : Maieutics.Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name) => null;
    }

    /// <summary>Collects one string-valued data entry: resolves the path inside the
    /// plugin root and parses the file as JSON. Any failure rides the entry as an error
    /// marker — the plugin stays loaded, and the entry's interpreter treats a failed
    /// collection as no data (for 'mcp', the sticky-last-good marker).</summary>
    private static PluginDataEntry CollectDataEntry(string root, string name, string relativePath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PluginDataEntry(name, null, $"The data entry path '{relativePath}' is not a valid path.");
        }

        if (!IsWithinRoot(fullPath, root))
            return new PluginDataEntry(name, null, $"The data entry path '{relativePath}' resolves outside the plugin root.");

        if (!File.Exists(fullPath))
            return new PluginDataEntry(name, null, $"The data entry file '{relativePath}' does not exist.");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
            return new PluginDataEntry(name, document.RootElement.Clone(), null);
        }
        catch (JsonException exception)
        {
            return new PluginDataEntry(name, null, $"Invalid JSON in '{relativePath}': {exception.Message}");
        }
    }

    /// <summary>Parses the manifest's declarative `extensions` section. The section must
    /// be an object of kind → entry arrays (structural failures fail the plugin, matching
    /// maieutics.json strictness); entry data is carried raw for the kind's kernel
    /// interpreter. Unknown kinds are dropped with a diagnostic, never honored.</summary>
    private static IReadOnlyList<PluginExtensionEntry> ReadExtensions(
        JsonElement? section,
        out IReadOnlyList<string> diagnostics)
    {
        var found = new List<string>();
        var entries = new List<PluginExtensionEntry>();
        if (section is not { ValueKind: JsonValueKind.Object } objectSection)
        {
            if (section is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
                throw new JsonException("The 'extensions' section must be an object.");
            diagnostics = found.AsReadOnly();
            return entries;
        }

        foreach (var kind in objectSection.EnumerateObject())
        {
            if (kind.Value.ValueKind != JsonValueKind.Array)
                throw new JsonException($"The 'extensions.{kind.Name}' section must be an array of entries.");
            if (!PluginExtensionKind.IsKnown(kind.Name))
            {
                found.Add(
                    $"Unknown extension kind '{kind.Name}' is declared but not supported by this kernel; " +
                    $"it is ignored (known kinds: {PluginExtensionKind.McpDiscover}).");
                continue;
            }

            foreach (var entry in kind.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    throw new JsonException(
                        $"The 'extensions.{kind.Name}' entries must be objects.");
                entries.Add(new PluginExtensionEntry(kind.Name, entry.Clone()));
            }
        }

        diagnostics = found.AsReadOnly();
        return entries;
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

    /// <summary>Reads the plugin's declared <c>deno.json</c> <c>imports</c> for the
    /// runtime import-map merge. Malformed or missing declarations yield no entries.</summary>
    internal static IReadOnlyList<PluginImportEntry> ReadImports(string directory)
    {
        var denoJson = Path.Combine(directory, "deno.json");
        if (!File.Exists(denoJson)) return [];
        try
        {
            var manifest = JsonSerializer.Deserialize(
                               File.ReadAllText(denoJson),
                               PluginManifestJsonContext.Default.PluginManifestFile);
            return PluginImportReader.Read(manifest?.Imports);
        }
        catch (JsonException)
        {
            return [];
        }
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

    internal static PluginPermissionGrants ReadPermissions(PluginManifestPermissionSet? set)
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

/// <summary>The plugin declaration file (maieutics.json): entrypoints (code arrays and
/// data paths), dependencies, isolation.</summary>
internal sealed record MaieuticsManifestFile(
    JsonElement? Entrypoints = null,
    IReadOnlyList<string>? Dependencies = null,
    string? Isolation = null,
    IReadOnlyList<string>? Capabilities = null,
    JsonElement? Extensions = null,
    JsonElement? Inspections = null,
    JsonElement? Triggers = null);

/// <summary>The package identity file (deno.json), read for name and permissions only.</summary>
internal sealed record PluginManifestFile(
    string? Name = null,
    JsonElement? Exports = null,
    PluginManifestPermissions? Permissions = null,
    PluginManifestMaieutics? Maieutics = null,
    JsonElement? Imports = null);

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