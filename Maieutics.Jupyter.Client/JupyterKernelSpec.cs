using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public sealed record JupyterKernelSpec(
    IReadOnlyList<string> Argv,
    string DisplayName,
    string Language,
    string InterruptMode,
    IReadOnlyDictionary<string, string> Environment)
{
    public static async Task<JupyterKernelSpec> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var file = await JsonSerializer.DeserializeAsync(
                       stream,
                       KernelSpecJsonContext.Default.KernelSpecFile,
                       cancellationToken)
                   ?? throw new JupyterProtocolException($"Kernel spec '{path}' did not contain valid JSON.");

        if (file.Argv.Count == 0 || string.IsNullOrWhiteSpace(file.Argv[0]))
            throw new JupyterProtocolException($"Kernel spec '{path}' did not define a valid argv.");

        return new JupyterKernelSpec(
            file.Argv,
            file.DisplayName,
            file.Language,
            file.InterruptMode,
            file.Environment);
    }
}

internal sealed class KernelSpecFile
{
    [JsonPropertyName("argv")] public List<string> Argv { get; init; } = [];

    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("language")] public string Language { get; init; } = string.Empty;

    [JsonPropertyName("interrupt_mode")] public string InterruptMode { get; init; } = "signal";

    [JsonPropertyName("env")] public Dictionary<string, string> Environment { get; init; } = [];
}

// Preserves the previous JsonSerializerDefaults.Web matching behavior (camelCase names,
// case-insensitive keys) while replacing reflection metadata with source generation.
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(KernelSpecFile))]
internal sealed partial class KernelSpecJsonContext : JsonSerializerContext;
