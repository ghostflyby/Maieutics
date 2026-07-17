using Maieutics.Providers.OpenAI;

namespace Maieutics;

public sealed class MaieuticsOptions
{
    public const string SectionName = "Maieutics";

    public const string ValidationMessage =
        "Maieutics requires a model, supported OpenAI API flavor, API key, valid connection file, " +
        "positive history limits, and positive flush settings.";

    public string Model { get; set; } = string.Empty;

    public string? SystemPrompt { get; set; }

    // ReSharper disable once InconsistentNaming
    public MaieuticsOpenAIOptions OpenAI { get; set; } = new();

    public MaieuticsAgentOptions Agent { get; set; } = new();

    public MaieuticsJupyterOptions Jupyter { get; set; } = new();

    public static bool IsValid(MaieuticsOptions options) =>
        !string.IsNullOrWhiteSpace(options.Model) &&
        Enum.IsDefined(options.OpenAI.ApiFlavor) &&
        !string.IsNullOrWhiteSpace(options.OpenAI.ApiKey) &&
        (options.OpenAI.Endpoint is null ||
         options.OpenAI.Endpoint.IsAbsoluteUri &&
         options.OpenAI.Endpoint.Scheme is "http" or "https") &&
        !string.IsNullOrWhiteSpace(options.Jupyter.ConnectionFile) &&
        File.Exists(options.Jupyter.ConnectionFile) &&
        options.Agent is { MaxRetainedTurns: > 0, MaxHistoryCharacters: > 0 } and
            { MaxInputCharacters: > 0, MaxResponseCharacters: > 0 } &&
        options.Jupyter.FlushInterval > TimeSpan.Zero &&
        options.Jupyter.FlushCharacters > 0;
}

// ReSharper disable once InconsistentNaming
public sealed class MaieuticsOpenAIOptions
{
    public OpenAiApiFlavor ApiFlavor { get; set; } = OpenAiApiFlavor.Responses;

    public string ApiKey { get; set; } = string.Empty;

    public Uri? Endpoint { get; set; }
}

public sealed class MaieuticsAgentOptions
{
    public int MaxRetainedTurns { get; set; } = 50;

    public int MaxHistoryCharacters { get; set; } = 200_000;

    public int MaxInputCharacters { get; set; } = 32_000;

    public int MaxResponseCharacters { get; set; } = 64_000;
}

public sealed class MaieuticsJupyterOptions
{
    public string ConnectionFile { get; set; } = string.Empty;

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    public int FlushCharacters { get; set; } = 1024;
}