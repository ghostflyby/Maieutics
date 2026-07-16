namespace Maieutics.Agent;

public sealed record AgentSessionOptions
{
    public string? SystemPrompt { get; init; }

    public int MaxRetainedTurns { get; init; } = 50;

    public int MaxHistoryCharacters { get; init; } = 200_000;

    public int MaxInputCharacters { get; init; } = 32_000;

    public int MaxResponseCharacters { get; init; } = 64_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetainedTurns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHistoryCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxInputCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseCharacters, 1);
    }
}