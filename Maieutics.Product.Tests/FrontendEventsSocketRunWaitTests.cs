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
        await WaitForQueueDrainedAsync(harness, sessionId, deadline.Token);
        Trace("queue drained; submitting a direct turn");

        // A 409 here is the documented busy window's tail, not a queue bug: the drained
        // snapshot is published when the worker clears the running item, and the previous
        // run's gate release / presentation detach becomes visible to a direct submission
        // a beat later. "Busy" has two causes with different remedies (gate held vs.
        // detach in flight); the direct submission retries briefly and only a persistent
        // rejection fails the test.
        using var response = await SubmitDirectUntilAcceptedAsync(harness, sessionId, deadline.Token);
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


    /// <summary>Submits one direct turn, retrying bounded when the drained queue's busy
    /// window (gate release / presentation detach propagation) rejects it as busy.</summary>
    private static async Task<HttpResponseMessage> SubmitDirectUntilAcceptedAsync(
        Harness harness,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var deadline = TimeSpan.FromSeconds(5);
        while (true)
        {
            var response = await harness.Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "direct after drain" },
                cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.Conflict || deadline <= TimeSpan.Zero)
            {
                response.StatusCode.Should().Be(
                    System.Net.HttpStatusCode.Accepted,
                    "the queue has drained, so a direct turn is accepted rather than busy-rejected");
                return response;
            }

            await Task.Delay(100, cancellationToken);
            deadline -= TimeSpan.FromMilliseconds(100);
        }
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

    private async Task WaitForQueueDrainedAsync(Harness harness, string sessionId, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            using var response = await harness.Client
                .GetAsync($"/v1/agent/sessions/{sessionId}/queue", wait.Token)
                .ConfigureAwait(false);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            var queue = await response.Content.ReadFromJsonAsync<JsonElement>(wait.Token).ConfigureAwait(false);
            var pending = queue.GetProperty("items").GetArrayLength();
            var running = queue.TryGetProperty("running", out _);
            if (pending == 0 && !running)
            {
                Trace("queue reported drained");
                return;
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
