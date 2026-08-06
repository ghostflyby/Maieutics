namespace Maieutics.Jupyter;

public sealed record MaieuticsAgentKernelOptions
{
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    public int FlushCharacters { get; init; } = 1024;

    internal void Validate()
    {
        if (FlushInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(FlushInterval), "Flush interval must be positive.");

        ArgumentOutOfRangeException.ThrowIfLessThan(FlushCharacters, 1);
    }
}