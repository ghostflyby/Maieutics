using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Providers.OpenAI;
using Harness = Maieutics.Product.Tests.FrontendApiIntegrationTests.FrontendHarness;

namespace Maieutics.Product.Tests;

/// <summary>Guards the events socket's single-run-wait invariant. The loop races a run wake
/// against a queue wake and used to abandon the losing run wait without cancelling it; the
/// announcements channel hands each item to exactly one waiter (oldest first), so the
/// abandoned reader stayed queued ahead of the freshly created one and took the next
/// announcement with it. A direct turn announced after the queue has drained has no later
/// queue mutation to wake the loop and recover the run, so the run is never served on that
/// socket.</summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendEventsSocketRunWaitTests
{
    private readonly ITestOutputHelper output;

    public FrontendEventsSocketRunWaitTests(ITestOutputHelper output) => this.output = output;

    private void Trace(string message) =>
        output.WriteLine($"[events-run-wait] {DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message}");

    /// <summary>A run announced after the turn queue has fully drained must still be served on
    /// a socket that observed the queue's transitions. The drain's queue wakes are what used to
    /// leave an abandoned pending run wait behind, and a direct turn publishes no queue mutation
    /// afterwards, so nothing else can wake the loop.</summary>
    [Fact(Timeout = 90_000)]
    public async Task DirectTurnAfterTheQueueDrainsIsStillServedOnAnOpenEventsSocket()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(70));
        // The harness owns the provider's lifetime and disposes it with the host.
        var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            requestCount: 3);
        await using var harness = await Harness.StartAsync(
            deadline.Token,
            provider,
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
        var hello = await events.ReceiveFrameAsync(deadline.Token);
        hello.GetProperty("type").GetString().Should().Be("hello");

        // Queue two turns. The worker's dequeue/running/settled transitions publish queue
        // wakes while the events loop is waiting for a run, which is the window that left an
        // abandoned reader behind.
        var enqueued = await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");
        enqueued.Should().HaveCount(2);

        // The queue is fully drained once the API reports no running item and no pending items.
        var lastQueuedRunId = await WaitForQueueDrainedAsync(harness, sessionId, deadline.Token);
        Trace("queue drained; waiting for the last queued run to settle before the direct turn");

        // Deterministic readiness: a run stream's Settled completes only after the
        // single-run gate released and the presentation scope detached (the pump's
        // disposal signal is the last step of its finally), so once it is observed the
        // direct submission cannot race either busy cause. A run that already left the
        // retained registry has already settled.
        if (lastQueuedRunId is not null &&
            harness.SessionService.TryGetRun(lastQueuedRunId, out var lastStream) &&
            lastStream is not null)
        {
            await lastStream.Settled.WaitAsync(deadline.Token);
        }

        Trace("last queued run settled; submitting a direct turn");
        using var response = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/turns",
            new { text = "direct after drain" },
            deadline.Token);
        response.StatusCode.Should().Be(
            System.Net.HttpStatusCode.Accepted,
            "the last queued run settled, so a direct turn is accepted rather than busy-rejected");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        var runId = body.GetProperty("runId").GetString();
        runId.Should().NotBeNullOrEmpty();

        // The run must reach this socket. A parked loop delivers nothing further, so the
        // assertion is on the run's own terminal frame, not on elapsed time.
        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() is "run.completed" or "run.failed"
                     && frame.TryGetProperty("runId", out var id) && id.GetString() == runId,
            deadline.Token);
        var terminal = frames.Last(frame => frame.GetProperty("type").GetString() is "run.completed" or "run.failed");
        terminal.GetProperty("type").GetString().Should().Be("run.completed");
        terminal.GetProperty("runId").GetString().Should().Be(runId);
    }



    private static async Task<string[]> EnqueueAsync(
        Harness harness,
        string sessionId,
        CancellationToken cancellationToken,
        params string[] texts)
    {
        using var response = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = texts.Select(text => new { text }) },
            cancellationToken).ConfigureAwait(false);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return [.. body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray()];
    }

    /// <summary>Waits for the queue to report drained and returns the runId of the last
    /// running item observed on the way there — the deterministic readiness signal for a
    /// subsequent direct submission (the run stream's Settled task completes only after the
    /// single-run gate released and the presentation scope detached).</summary>
    private async Task<string?> WaitForQueueDrainedAsync(Harness harness, string sessionId, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(45));
        string? lastRunningRunId = null;
        while (true)
        {
            using var response = await harness.Client
                .GetAsync($"/v1/agent/sessions/{sessionId}/queue", wait.Token)
                .ConfigureAwait(false);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            var queue = await response.Content.ReadFromJsonAsync<JsonElement>(wait.Token).ConfigureAwait(false);
            var pending = queue.GetProperty("items").GetArrayLength();
            if (queue.TryGetProperty("running", out var running))
            {
                lastRunningRunId = running.GetProperty("runId").GetString();
                continue;
            }

            if (pending == 0)
            {
                Trace("queue reported drained");
                return lastRunningRunId;
            }

            await Task.Delay(50, wait.Token).ConfigureAwait(false);
        }
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }
}
