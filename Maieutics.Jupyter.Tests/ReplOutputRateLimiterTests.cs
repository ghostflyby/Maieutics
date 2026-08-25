using System.Text.Json;
using FluentAssertions;
using Maieutics.DenoRepl;

namespace Maieutics.Jupyter.Tests;

public sealed class ReplOutputRateLimiterTests
{
    private const long Frequency = 1000;

    [Fact]
    public void MessageBudgetExhaustedDropsDisplaysUntilSamplesExpire()
    {
        // 1001 small displays within the 3 s window exceed the 1000 msg/s budget.
        var (limiter, clock) = CreateLimiter(new DenoReplOptions { MaxDisplayMessageRate = 1000 });

        for (var sequence = 0; sequence < 1000; sequence++)
            limiter.TryReserve(Display("text/plain", "x")).Should().BeTrue();
        limiter.TryReserve(Display("text/plain", "x")).Should().BeFalse();

        // The window slides: once the first sample is older than the window, a new display fits
        // again (the budget is per window, jupyter_server rate-limit semantics).
        clock.Advance(TimeSpan.FromSeconds(3));
        limiter.TryReserve(Display("text/plain", "x")).Should().BeTrue();
    }

    [Fact]
    public void DataBudgetExhaustedDropsOversizedDisplay()
    {
        // A display carrying ~1 MB of native data reaches the 1 MB/s budget and is dropped.
        var (limiter, _) = CreateLimiter(new DenoReplOptions { MaxDisplayDataRate = 1_000_000 });
        limiter.TryReserve(Display("text/plain", new string('x', 500_000))).Should().BeTrue();
        limiter.TryReserve(Display("text/plain", new string('x', 500_000))).Should().BeFalse();
    }

    [Fact]
    public void WindowSlidesRecoveringTheDataBudget()
    {
        // Two displays whose combined native data bytes reach the window budget; the second is
        // dropped, then the first sample expires and the budget recovers.
        var (limiter, clock) = CreateLimiter(new DenoReplOptions { MaxDisplayDataRate = 100 });
        limiter.TryReserve(Display("text/plain", new string('x', 80))).Should().BeTrue();
        limiter.TryReserve(Display("text/plain", new string('x', 80))).Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(3));
        limiter.TryReserve(Display("text/plain", new string('x', 80))).Should().BeTrue();
    }

    [Fact]
    public void DataBytesIncludeTrailingBufferByteLength()
    {
        // The 8-byte native buffer counts at its actual length: two displays whose JSON alone
        // would fit the budget are dropped once the trailing buffer bytes are counted (binary
        // data is never counted as its base64 expansion).
        var (limiter, _) = CreateLimiter(new DenoReplOptions { MaxDisplayDataRate = 45 });
        limiter.TryReserve(Display("image/png", null, new byte[8])).Should().BeTrue();
        limiter.TryReserve(Display("image/png", null, new byte[8])).Should().BeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task RateLimitIsThreadSafeUnderConcurrentReservations()
    {
        var token = TestContext.Current.CancellationToken;
        token.ThrowIfCancellationRequested();
        var options = new DenoReplOptions { MaxDisplayMessageRate = 100 };
        var (limiter, _) = CreateLimiter(options);

        // 200 concurrent reservations; exactly 100 fit inside the window regardless of which
        // threads win the gate first.
        var reservations = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => limiter.TryReserve(Display("text/plain", "x")), token))
            .ToArray();
        var accepted = await Task.WhenAll(reservations).WaitAsync(token);

        accepted.Count(static value => value).Should().Be(100);
    }

    [Fact]
    public void MonotonicClockTimestampIsUsedForTheWindow()
    {
        // The limiter builds its window on Stopwatch-style monotonic timestamps; the injectable
        // clock advances deterministically. The monotonic source is not wall-clock time: the fake
        // timestamp is fed straight through.
        var (limiter, clock) = CreateLimiter(new DenoReplOptions { MaxDisplayMessageRate = 1 });
        limiter.TryReserve(Display("text/plain", "x")).Should().BeTrue();

        clock.Advance(TimeSpan.FromMilliseconds(500));
        limiter.TryReserve(Display("text/plain", "x")).Should().BeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(2500));
        limiter.TryReserve(Display("text/plain", "x")).Should().BeTrue();
    }

    private static (ReplOutputRateLimiter Limiter, FakeClock Clock) CreateLimiter(DenoReplOptions options)
    {
        var clock = new FakeClock();
        var limiter = new ReplOutputRateLimiter(
            options,
            clock.Timestamp,
            Frequency);
        return (limiter, clock);
    }

    private static ReplOutputDisplayFrame Display(string mime, string? text, params byte[][] buffers)
    {
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (text is not null)
            data[mime] = JsonSerializer.SerializeToElement(text);
        else
            data[mime] = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["$buffer"] = 0 });
        return new ReplOutputDisplayFrame(
            1,
            "execution-1",
            data,
            new Dictionary<string, JsonElement>(),
            null,
            false,
            buffers);
    }

    private sealed class FakeClock
    {
        private long timestamp;

        internal long Timestamp() => Interlocked.Read(ref timestamp);

        internal void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref timestamp, checked((long)(duration.TotalSeconds * Frequency)));
        }
    }
}
