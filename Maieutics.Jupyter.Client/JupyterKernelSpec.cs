using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public sealed record JupyterKernelSpec(IReadOnlyList<string> Argv, string DisplayName, string Language)
{
    public static async Task<JupyterKernelSpec> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var file = await JsonSerializer.DeserializeAsync<KernelSpecFile>(stream, Json.Options, cancellationToken)
                   ?? throw new JupyterProtocolException($"Kernel spec '{path}' did not contain valid JSON.");

        if (file.Argv.Count == 0)
        {
            throw new JupyterProtocolException($"Kernel spec '{path}' did not define argv.");
        }

        return new JupyterKernelSpec(file.Argv, file.DisplayName, file.Language);
    }

    public Process Start(string connectionFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Argv[0],
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (var argument in Argv.Skip(1))
        {
            startInfo.ArgumentList.Add(argument.Replace("{connection_file}", connectionFile, StringComparison.Ordinal));
        }

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"Could not start kernel '{DisplayName}'.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private sealed class KernelSpecFile
    {
        [JsonPropertyName("argv")] public List<string> Argv { get; init; } = [];

        [JsonPropertyName("display_name")] public string DisplayName { get; init; } = string.Empty;

        [JsonPropertyName("language")] public string Language { get; init; } = string.Empty;
    }

    private static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}