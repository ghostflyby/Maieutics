namespace Maieutics;

public sealed class MaieuticsOptions
{
    public const string SectionName = "Maieutics";

    public string? SystemPrompt { get; set; }

    public string DefaultProfile { get; set; } = string.Empty;

    public Dictionary<string, MaieuticsModelProfileOptions> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // Legacy single-provider configuration. Removed after the compatibility window.
    public MaieuticsModelOptions Model { get; set; } = new();

    public MaieuticsProviderOptions Providers { get; set; } = new();

    public MaieuticsAgentOptions Agent { get; set; } = new();

    public MaieuticsMcpOptions Mcp { get; set; } = new();

    public MaieuticsJupyterOptions Jupyter { get; set; } = new();

    internal void ValidateCommon()
    {
        Agent.Validate();
        Jupyter.Validate();
    }
}

public sealed class MaieuticsMcpOptions
{
    public Dictionary<string, MaieuticsMcpServerOptions> Servers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MaieuticsMcpServerOptions
{
    public bool Enabled { get; set; }

    public string Transport { get; set; } = string.Empty;

    public string? Command { get; set; }

    public string[] Arguments { get; set; } = [];

    public string? WorkingDirectory { get; set; }

    public Dictionary<string, string?> EnvironmentVariables { get; set; } =
        new(StringComparer.Ordinal);

    public string? Endpoint { get; set; }

    public Dictionary<string, string> Headers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Tools { get; set; } =
        new(StringComparer.Ordinal);

    public TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class MaieuticsModelProfileOptions
{
    public string Source { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;
}

public sealed class MaieuticsModelOptions
{
    public string Provider { get; set; } = "OpenAI";

    public string Name { get; set; } = string.Empty;
}

public sealed class MaieuticsProviderOptions
{
    // Provider-specific legacy sections are read by their registered factories.
}

public sealed class MaieuticsAgentOptions
{
    public int MaxRetainedTurns { get; set; } = 50;

    public int MaxHistoryBytes { get; set; } = 400_000;

    // Legacy configuration input. Normalize before validation and remove after the compatibility window.
    public int? MaxHistoryCharacters { get; set; }

    public int MaxInputCharacters { get; set; } = 32_000;

    public int MaxResponseCharacters { get; set; } = 64_000;

    public int MaxModelIterationsPerTurn { get; set; } = 8;

    public int MaxToolCallsPerTurn { get; set; } = 16;

    public int MaxToolArgumentsBytes { get; set; } = 65_536;

    public int MaxToolResultBytes { get; set; } = 262_144;

    public int MaxToolProgressEventsPerCall { get; set; } = 256;

    public int EventBufferCapacity { get; set; } = 128;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetainedTurns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHistoryBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxInputCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxModelIterationsPerTurn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolCallsPerTurn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolArgumentsBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolResultBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolProgressEventsPerCall, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(EventBufferCapacity, 1);
    }
}

public sealed class MaieuticsJupyterOptions
{
    public string ConnectionFile { get; set; } = string.Empty;

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    public int FlushCharacters { get; set; } = 1024;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionFile);
        if (!File.Exists(ConnectionFile))
        {
            throw new FileNotFoundException("The configured Jupyter connection file does not exist.", ConnectionFile);
        }

        if (FlushInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(FlushInterval), "Flush interval must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(FlushCharacters, 1);
    }
}