namespace Maieutics.DenoRepl;

internal sealed class DenoReplOptions
{
    internal const string SectionName = "Maieutics:DenoRepl";

    public string Executable { get; set; } = "deno";

    public int MaxSessionsPerAgent { get; set; } = 4;

    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public TimeSpan InterruptGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxModelOutputBytes { get; set; } = 128 * 1024;

    public int MaxPresentationTextBytes { get; set; } = 1024 * 1024;

    public int MaxPresentationEventsPerExecution { get; set; } = 256;

    public int MaxPresentationBundleBytes { get; set; } = 16 * 1024 * 1024;

    public bool AutoInstallModuleGraph { get; set; } = true;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Executable);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSessionsPerAgent, 1);
        ValidatePositive(StartupTimeout, nameof(StartupTimeout));
        ValidatePositive(ExecutionTimeout, nameof(ExecutionTimeout));
        ValidatePositive(InterruptGracePeriod, nameof(InterruptGracePeriod));
        ValidatePositive(ShutdownTimeout, nameof(ShutdownTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxModelOutputBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPresentationTextBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPresentationEventsPerExecution, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPresentationBundleBytes, 1);
    }

    private static void ValidatePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(name, "The timeout must be positive.");
    }
}
