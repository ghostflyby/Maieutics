using System.Diagnostics;
using System.Text.Json;

namespace Maieutics.DenoRepl;

/// <summary>
///     Sliding-window rate limiter for REPL display/updateDisplay output, aligned with the
///     jupyter_server iopub rate limits (<c>ZMQChannelsWebsocketConnection</c>): the display
///     budgets mirror <c>iopub_data_rate_limit = 1_000_000</c> bytes and
///     <c>iopub_msg_rate_limit = 1000</c> messages over the <c>rate_limit_window = 3</c> second
///     window, and exceeding a budget drops the display instead of failing the kernel (jupyter's
///     <c>limit_rate=True</c> behavior). Only display/updateDisplay events consume the budget;
///     stdout/stderr (stream semantics) and clearOutput (clear semantics) are untouched.
///
///     The budget is per window: any sliding <see cref="DenoReplOptions.DisplayRateLimitWindow"/>
///     holds at most <see cref="DenoReplOptions.MaxDisplayMessageRate"/> samples and at most
///     <see cref="DenoReplOptions.MaxDisplayDataRate"/> data bytes (jupyter_server applies its
///     iopub limits per <c>rate_limit_window</c>). Data bytes are the display frame's Data JSON
///     bytes plus its trailing buffers at native byte length — binary data never counts as its
///     base64 expansion because the process-to-host segment stays native (AGENTS.md invariant 26).
/// </summary>
internal sealed class ReplOutputRateLimiter
{
    private readonly Lock gate = new();
    private readonly int maxDisplayDataRate;
    private readonly int maxDisplayMessageRate;
    private readonly LinkedList<Sample> samples = new();
    private readonly Func<long> timestampProvider;
    private readonly long windowTicks;
    private long windowBytes;

    /// <param name="options">Carries the display rate budgets and window.</param>
    /// <param name="timestampProvider">Monotonic timestamp source, defaulting to
    /// <see cref="Stopwatch.GetTimestamp()"/>. Injectable so tests can slide the window
    /// deterministically instead of waiting on a wall clock.</param>
    /// <param name="frequency">Ticks per second of <paramref name="timestampProvider"/>,
    /// defaulting to <see cref="Stopwatch.Frequency"/>.</param>
    internal ReplOutputRateLimiter(
        DenoReplOptions options,
        Func<long>? timestampProvider = null,
        long? frequency = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDisplayMessageRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDisplayDataRate, 1);
        if (options.DisplayRateLimitWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The display rate limit window must be positive.");
        maxDisplayMessageRate = options.MaxDisplayMessageRate;
        maxDisplayDataRate = options.MaxDisplayDataRate;
        var ticksPerSecond = frequency ?? Stopwatch.Frequency;
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerSecond, 1);
        windowTicks = checked((long)(options.DisplayRateLimitWindow.TotalSeconds * ticksPerSecond));
        this.timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
    }

    /// <summary>
    ///     Attempts to reserve the display against the sliding window budgets. Returns
    ///     <c>false</c> when the window already holds at least
    ///     <see cref="DenoReplOptions.MaxDisplayMessageRate"/> samples, or when adding this
    ///     frame's native data bytes would reach or exceed
    ///     <see cref="DenoReplOptions.MaxDisplayDataRate"/> — the caller drops the display from
    ///     both the notebook presentation and the model digest. Otherwise the sample is recorded
    ///     and <c>true</c> is returned. Expired samples are pruned on every call, so the window
    ///     slides lazily and never accumulates beyond one window's worth of samples.
    /// </summary>
    internal bool TryReserve(ReplOutputDisplayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var dataBytes = CountDataBytes(frame);
        var now = timestampProvider();

        lock (gate)
        {
            PruneExpired(now);
            if (samples.Count >= maxDisplayMessageRate) return false;
            if (windowBytes + dataBytes >= maxDisplayDataRate) return false;
            samples.AddLast(new Sample(now, dataBytes));
            windowBytes += dataBytes;
            return true;
        }
    }

    private void PruneExpired(long now)
    {
        while (samples.First is { } first && now - first.Value.Timestamp >= windowTicks)
        {
            windowBytes -= first.Value.Bytes;
            samples.RemoveFirst();
        }
    }

    /// <summary>The display's native data byte count: the Data JSON bundle bytes plus the trailing
    /// buffers at their actual lengths. Metadata is not counted (jupyter counts the whole message,
    /// but the display budget scopes to the rendered payload and buffers).</summary>
    private static int CountDataBytes(ReplOutputDisplayFrame frame)
    {
        var bytes = DenoReplExecutionCollector.CountJsonBytes(frame.Data);
        foreach (var buffer in frame.Buffers)
            bytes = checked(bytes + buffer.Length);
        return bytes;
    }

    private readonly record struct Sample(long Timestamp, int Bytes);
}
