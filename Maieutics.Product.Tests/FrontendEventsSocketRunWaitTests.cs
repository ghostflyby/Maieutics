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

        // Deterministic readiness: the queue snapshot can present "drained" (no running, no
        // pending) inside the worker's dequeue handshake — the head is dequeued and its run
        // is about to start, so nothing is observable yet. Readiness therefore requires a
        // stable cycle: observe drained, settle every run seen so far, re-read, and only
        // submit when the re-read is still drained and surfaced no new run. A run stream's
        // Settled completes only after the single-run gate released and the presentation
        // scope detached, so the direct submission cannot race either busy cause.
        var seenRunIds = new HashSet<string>();
        while (true)
        {
            var observed = await WaitForQueueDrainedAsync(harness, sessionId, deadline.Token);
            if (observed is not null) seenRunIds.Add(observed);

            foreach (var seenRunId in seenRunIds)
            {
                if (harness.SessionService.TryGetRun(seenRunId, out var seenStream) && seenStream is not null)
                    await seenStream.Settled.WaitAsync(deadline.Token);
            }

            if (await IsQueueEmptyAsync(harness, sessionId, deadline.Token) &&
                !await HasNewRunningAsync(harness, sessionId, seenRunIds, deadline.Token))
            {
                Trace("queue drained stably; submitting a direct turn");
                break;
            }

            Trace("drain observation was not stable; re-reading");
        }

        Trace("submitting a direct turn");
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
    /// running item observed on the way there. A single drained observation is not readiness
    /// by itself: the dequeue handshake window presents as an empty queue while the next
    /// item's run is about to hold the single-run gate.</summary>
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

    /// <summary>Reads the queue once; true when it reports no running item and no pending
    /// items.</summary>
    private static async Task<bool> IsQueueEmptyAsync(Harness harness, string sessionId, CancellationToken cancellationToken)
    {
        using var response = await harness.Client
            .GetAsync($"/v1/agent/sessions/{sessionId}/queue", cancellationToken)
            .ConfigureAwait(false);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var queue = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return queue.GetProperty("items").GetArrayLength() == 0 &&
               !queue.TryGetProperty("running", out _);
    }

    /// <summary>Reads the queue once; true when it is running an item whose run has not been
    /// seen and settled yet.</summary>
    private static async Task<bool> HasNewRunningAsync(
        Harness harness,
        string sessionId,
        HashSet<string> seenRunIds,
        CancellationToken cancellationToken)
    {
        using var response = await harness.Client
            .GetAsync($"/v1/agent/sessions/{sessionId}/queue", cancellationToken)
            .ConfigureAwait(false);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var queue = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return queue.TryGetProperty("running", out var running) &&
               running.GetProperty("runId").GetString() is { } runId &&
               !seenRunIds.Contains(runId);
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }
}
