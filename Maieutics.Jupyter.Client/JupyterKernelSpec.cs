using System.Text.Json;
using System.Text.Json.Serialization;
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
        var file = await JsonSerializer.DeserializeAsync<KernelSpecFile>(stream, Json.Options, cancellationToken)
                   ?? throw new JupyterProtocolException($"Kernel spec '{path}' did not contain valid JSON.");

        if (file.Argv.Count == 0 || string.IsNullOrWhiteSpace(file.Argv[0]))
        {
            throw new JupyterProtocolException($"Kernel spec '{path}' did not define a valid argv.");
        }

        return new JupyterKernelSpec(
            file.Argv,
            file.DisplayName,
            file.Language,
            file.InterruptMode,
            file.Environment);
    }

    private sealed class KernelSpecFile
    {
        [JsonPropertyName("argv")] public List<string> Argv { get; init; } = [];

        [JsonPropertyName("display_name")] public string DisplayName { get; init; } = string.Empty;

        [JsonPropertyName("language")] public string Language { get; init; } = string.Empty;

        [JsonPropertyName("interrupt_mode")] public string InterruptMode { get; init; } = "signal";

        [JsonPropertyName("env")] public Dictionary<string, string> Environment { get; init; } = [];
    }

    private static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}