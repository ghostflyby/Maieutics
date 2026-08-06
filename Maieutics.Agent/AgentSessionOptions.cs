namespace Maieutics.Agent;

/// <summary>Configures Agent session limits and instructions.</summary>
public sealed record AgentSessionOptions
{
    /// <summary>Gets the system instructions sent with every model invocation.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Gets the maximum number of complete turns retained in committed history.</summary>
    public int MaxRetainedTurns { get; init; } = 50;

    /// <summary>Gets the maximum number of UTF-8 JSON bytes retained in committed history.</summary>
    public int MaxHistoryBytes { get; init; } = 400_000;

    /// <summary>Gets the maximum number of UTF-16 characters accepted in one input.</summary>
    public int MaxInputCharacters { get; init; } = 32_000;

    /// <summary>Gets the maximum number of UTF-16 characters accepted in one response.</summary>
    public int MaxResponseCharacters { get; init; } = 64_000;

    /// <summary>Gets the maximum number of model round trips in one turn.</summary>
    public int MaxModelIterationsPerTurn { get; init; } = 24;

    /// <summary>Gets the maximum wall-clock duration of one turn, or zero when unlimited.</summary>
    public TimeSpan MaxTurnDuration { get; init; } = TimeSpan.Zero;

    /// <summary>Gets the maximum number of tool calls in one turn.</summary>
    public int MaxToolCallsPerTurn { get; init; } = 48;

    /// <summary>Gets the maximum UTF-8 encoded argument size for one tool call.</summary>
    public int MaxToolArgumentsBytes { get; init; } = 65_536;

    /// <summary>Gets the maximum UTF-8 encoded result envelope size for one tool call.</summary>
    public int MaxToolResultBytes { get; init; } = 262_144;

    /// <summary>Gets the maximum number of progress events emitted by one tool call.</summary>
    public int MaxToolProgressEventsPerCall { get; init; } = 256;

    /// <summary>Gets the capacity of each run's bounded event stream.</summary>
    public int EventBufferCapacity { get; init; } = 128;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetainedTurns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHistoryBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxInputCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxModelIterationsPerTurn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTurnDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolCallsPerTurn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolArgumentsBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolResultBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolProgressEventsPerCall, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(EventBufferCapacity, 1);
    }
}