using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness = Maieutics.Product.Tests.FrontendApiIntegrationTests.FrontendHarness;
using GatedProvider = Maieutics.Testing.GatedModelProvider;

namespace Maieutics.Product.Tests;

/// <summary>
///     Pins the session turn queue's key to the resolved session. The session-addressed
///     routes resolve a session id through <c>Guid.TryParseExact(id, "N")</c>, where hex
///     digits are case-insensitive, so the uppercase and the lowercase spelling of one
///     session's id are the same session and must address one queue state: the queue used to
///     key its state by the raw route text instead, which gave one logical session two
///     queues — a second 64-item capacity budget, contradicting snapshots, and a clear
///     through one spelling that left the other spelling's items queued.
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendQueueSessionKeyTests(ITestOutputHelper output)
{
    private void Trace(string message) =>
        TestTrace.Write(output, "queue-session-key", message);

    /// <summary>The queue is filled to its committed capacity through the uppercase spelling
    /// of the session id. The worker is held between its submission handshake and the running
    /// publish, so the frozen state is "head dequeued (invisible), 63 waiting". With the queue
    /// keyed by the raw route text this batch opens a second, empty queue state and is
    /// accepted; keyed by the resolved session it must hit the one queue that already holds
    /// those 63 waiting items.</summary>
    [Fact(Timeout = 120_000)]
    public async Task CaseVariantsOfOneSessionIdShareTheCapacityAndSnapshots()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedProvider();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        var upperCaseId = sessionId.ToUpperInvariant();

        // The pin needs two spellings of one id, so the id must carry at least one hex
        // letter; an all-digit id has a single spelling and the test would prove nothing.
        Assert.True(
            sessionId.Any(character => character is >= 'a' and <= 'f'),
            $"the session id '{sessionId}' carries no hex letter, so it has no case variant");
        Trace($"session {sessionId}, alternate spelling {upperCaseId}");

        // Premise: the uppercase spelling resolves to the same session. The transcript route
        // serves the alternate form and echoes the canonical identity, which is exactly the
        // aliasing the queue under test must share.
        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{upperCaseId}/transcript",
            deadline.Token);
        Assert.Equal(sessionId, transcript.GetProperty("sessionId").GetString());

        // Hold the worker between the submission handshake and the running publish: the
        // dequeued head is out of the pending list and the remaining items stay queued, so
        // the state below is stable for the whole test instead of racing the drain.
        var releaseRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.TurnQueue.RunningPublishGateForTests = token => releaseRunning.Task.WaitAsync(token);
        try
        {
            var full = Enumerable.Range(0, 64).Select(index => $"queued {index}").ToArray();
            var ids = await EnqueueAsync(harness, upperCaseId, deadline.Token, full);
            Assert.Equal(64, ids.Length);
            await provider.WaitForParkedAsync(deadline.Token);

            // The worker dequeued the head and then parked at the gate, so the queue is
            // exactly "head dequeued (invisible), 63 waiting" from here on.
            var filled = await WaitForQueueAsync(
                harness,
                upperCaseId,
                queue => !HasRunningItem(queue) && ItemIds(queue).Length == 63,
                deadline.Token);
            Assert.Equal(sessionId, filled.GetProperty("sessionId").GetString());
            Trace("queue filled under the uppercase spelling");

            // The canonical spelling addresses the same queue: same identity, same waiting
            // items. Keyed by the raw route text it would have opened a second, empty queue
            // instead.
            var canonical = await GetQueueAsync(harness, sessionId, deadline.Token);
            Assert.Equal(sessionId, canonical.GetProperty("sessionId").GetString());
            Assert.False(HasRunningItem(canonical), "the head must stay in the dequeue window while the gate holds");
            Assert.Equal(ItemIds(filled), ItemIds(canonical));
            Trace("canonical spelling observes the same queue state");

            // Capacity is one budget per session: 63 waiting plus a two-item batch exceeds
            // the 64-item capacity, so the batch is rejected whole however the session is
            // spelled instead of spending a second budget.
            using var over = await EnqueueBatchAsync(harness, sessionId, deadline.Token, "one over", "two over");
            Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
            Assert.Equal(
                "queue_full",
                (await over.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
                .GetProperty("code")
                .GetString());
            Trace("capacity probe rejected");

            // Clearing through the canonical spelling clears the queue the uppercase spelling
            // filled; the in-flight head is untouched.
            using var cleared = await harness.Client.DeleteAsync(
                $"/v1/agent/sessions/{sessionId}/queue",
                deadline.Token);
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
            var clearedView = await GetQueueAsync(harness, upperCaseId, deadline.Token);
            Assert.Empty(ItemIds(clearedView));
            Assert.False(HasRunningItem(clearedView), "the dequeued head stays in the dequeue window");
            Trace("clear through the canonical spelling reached the uppercase queue");
        }
        finally
        {
            harness.TurnQueue.RunningPublishGateForTests = null;
            // Unpark and answer everything: a failed assertion must not leave the session's
            // run in flight, or the harness disposal blocks on its profile lease and masks
            // the real failure with a teardown timeout.
            provider.ReleaseAll();
            // Best-effort: empty the queue before the worker resumes, so a failed assertion
            // cannot cascade into dozens of queued runs against the fake provider.
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            cleanup.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await harness.Client.DeleteAsync(
                    $"/v1/agent/sessions/{sessionId}/queue",
                    cleanup.Token);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or InvalidOperationException or OperationCanceledException)
            {
                Trace($"cleanup clear did not complete: {exception.Message}");
            }

            releaseRunning.TrySetResult();
        }

        // Let the parked head finish so no run is left in flight at harness disposal.
        await harness.TurnQueue.WaitUntilIdleAsync(sessionId, deadline.Token);
        Trace("queue drained");
    }

    /// <summary>Clearing must remove the session's waiting items regardless of the spelling
    /// the clear is addressed with. Keyed by the raw route text, a clear under the uppercase
    /// spelling hit a second, empty queue and left the canonical spelling's items queued.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ClearingThroughOneSpellingClearsTheSharedQueue()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedProvider();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        var upperCaseId = sessionId.ToUpperInvariant();
        Assert.True(
            sessionId.Any(character => character is >= 'a' and <= 'f'),
            $"the session id '{sessionId}' carries no hex letter, so it has no case variant");

        var releaseRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.TurnQueue.RunningPublishGateForTests = token => releaseRunning.Task.WaitAsync(token);
        try
        {
            var ids = await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two", "queued three");
            Assert.Equal(3, ids.Length);
            await provider.WaitForParkedAsync(deadline.Token);

            // Freeze the state: the head is dequeued (held in the dequeue window) and exactly
            // two items wait behind it.
            var queued = await WaitForQueueAsync(
                harness,
                sessionId,
                queue => !HasRunningItem(queue) && ItemIds(queue).Length == 2,
                deadline.Token);
            Assert.Equal([ids[1], ids[2]], ItemIds(queued));

            // The uppercase spelling addresses the same session, so its clear must remove
            // the waiting items the canonical spelling sees.
            using var cleared = await harness.Client.DeleteAsync(
                $"/v1/agent/sessions/{upperCaseId}/queue",
                deadline.Token);
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);

            var remaining = await GetQueueAsync(harness, sessionId, deadline.Token);
            Assert.Empty(ItemIds(remaining));
            Assert.False(HasRunningItem(remaining), "the dequeued head stays in the dequeue window");
            Trace("clear through the uppercase spelling removed the waiting items");
        }
        finally
        {
            harness.TurnQueue.RunningPublishGateForTests = null;
            provider.ReleaseAll();
        }
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan timeout) =>
        TestDeadlines.Create(cancellationToken, timeout);

    private static Task<Harness> StartHarnessAsync(CancellationToken cancellationToken, GatedProvider provider)
    {
        // The shared harness extracts the endpoint from the provider instance, gated or not.
        return Harness.StartAsync(
            cancellationToken,
            provider,
            hanging: false);
    }

    /// <summary>Reads the session's queue snapshot through the addressed spelling.</summary>
    private static async Task<JsonElement> GetQueueAsync(
        Harness harness,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var response = await harness.Client
            .GetAsync($"/v1/agent/sessions/{sessionId}/queue", cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enqueues a batch, asserting the documented 202 acceptance.</summary>
    private static async Task<string[]> EnqueueAsync(
        Harness harness,
        string sessionId,
        CancellationToken cancellationToken,
        params string[] texts)
    {
        using var response = await EnqueueBatchAsync(harness, sessionId, cancellationToken, texts).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return ItemIds(body);
    }

    private static Task<HttpResponseMessage> EnqueueBatchAsync(
        Harness harness,
        string sessionId,
        CancellationToken cancellationToken,
        params string[] texts)
    {
        return harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = texts.Select(text => new { text }) },
            cancellationToken);
    }

    /// <summary>Waits (signal-driven, on the queue's own mutation version) until the queue
    /// snapshot matches <paramref name="predicate" />, so the state under assertion is a
    /// settled transition rather than a timing guess.</summary>
    private static async Task<JsonElement> WaitForQueueAsync(
        Harness harness,
        string sessionId,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        var version = harness.TurnQueue.FrameSnapshot(sessionId).Version;
        var last = default(JsonElement);
        try
        {
            while (true)
            {
                last = await GetQueueAsync(harness, sessionId, wait.Token).ConfigureAwait(false);
                if (predicate(last)) return last;

                version = await harness.TurnQueue
                    .WaitForChangeAsync(sessionId, version, wait.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (wait.IsCancellationRequested)
        {
            var lastView = last.ValueKind == JsonValueKind.Object ? last.GetRawText() : "(no snapshot)";
            throw new InvalidOperationException(
                $"the queue never matched the expected state; last view: {lastView}", exception);
        }
    }

    private static string[] ItemIds(JsonElement queue)
    {
        if (!queue.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];

        var ids = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() is { } id) ids.Add(id);
            else Assert.Fail("every wire item carries an id");
        }

        return [.. ids];
    }

    private static bool HasRunningItem(JsonElement queue) =>
        queue.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.Object;
}
