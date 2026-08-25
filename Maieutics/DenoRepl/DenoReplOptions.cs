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

    /// <summary>Total byte budget for the model-side display digests of one execution. Once the
    /// budget is exhausted <see cref="Maieutics.DenoRepl.DenoReplPresentationResult.DigestTruncated" />
    /// is set and later displays are not digested.</summary>
    public int MaxModelDisplayDigestBytes { get; set; } = 4096;

    /// <summary>Maximum UTF-8 bytes of a digest preview (rune-safe truncation).</summary>
    public int MaxDisplayDigestPreviewBytes { get; set; } = 512;

    /// <summary>Maximum display data bytes per <see cref="DisplayRateLimitWindow"/>, aligned with
    /// jupyter_server's <c>iopub_data_rate_limit</c> default (1,000,000 bytes per 3 s window).
    /// Displays that exceed the budget are dropped from the notebook and the model digest; the
    /// kernel keeps running (<c>limit_rate</c> behavior).</summary>
    public int MaxDisplayDataRate { get; set; } = 1_000_000;

    /// <summary>Maximum display/updateDisplay messages per <see cref="DisplayRateLimitWindow"/>,
    /// aligned with jupyter_server's <c>iopub_msg_rate_limit</c> default (1000 messages per 3 s
    /// window). Excess displays are dropped from the notebook and the model digest; the kernel
    /// keeps running (<c>limit_rate</c> behavior).</summary>
    public int MaxDisplayMessageRate { get; set; } = 1000;

    /// <summary>Sliding window for the display rate limits, aligned with jupyter_server's
    /// <c>rate_limit_window</c> default of 3 seconds.</summary>
    public TimeSpan DisplayRateLimitWindow { get; set; } = TimeSpan.FromSeconds(3);

    public bool AutoInstallModuleGraph { get; set; } = true;

    /// <summary>Derives REPL processes through the plugin host (<c>host.repl.derive</c>, ADR
    /// 0020 B5) instead of the kernel spawning them directly. Defaults to true; set false to keep
    /// the kernel-derived path (the dual-track fallback).</summary>
    public bool HostDerivedRepl { get; set; } = true;

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
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxModelDisplayDigestBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDisplayDigestPreviewBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDisplayDataRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDisplayMessageRate, 1);
        ValidatePositive(DisplayRateLimitWindow, nameof(DisplayRateLimitWindow));
    }

    private static void ValidatePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(name, "The timeout must be positive.");
    }
}
