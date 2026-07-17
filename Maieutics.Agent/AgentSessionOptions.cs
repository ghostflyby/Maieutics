namespace Maieutics.Agent;

/// <summary>Configures Agent session limits and instructions.</summary>
public sealed record AgentSessionOptions
{
    /// <summary>Gets the system instructions sent with every model invocation.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Gets the maximum number of complete turns retained in committed history.</summary>
    public int MaxRetainedTurns { get; init; } = 50;

    /// <summary>Gets the maximum number of UTF-16 characters retained in committed history.</summary>
    public int MaxHistoryCharacters { get; init; } = 200_000;

    /// <summary>Gets the maximum number of UTF-16 characters accepted in one input.</summary>
    public int MaxInputCharacters { get; init; } = 32_000;

    /// <summary>Gets the maximum number of UTF-16 characters accepted in one response.</summary>
    public int MaxResponseCharacters { get; init; } = 64_000;

    /// <summary>Gets the capacity of each run's bounded event stream.</summary>
    public int EventBufferCapacity { get; init; } = 128;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetainedTurns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHistoryCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxInputCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(EventBufferCapacity, 1);
    }
}