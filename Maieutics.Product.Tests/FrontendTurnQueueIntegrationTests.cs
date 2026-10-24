using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Harness = Maieutics.Product.Tests.FrontendApiIntegrationTests.FrontendHarness;
using EventsConnection = Maieutics.Product.Tests.FrontendApiIntegrationTests.FrontendEventsConnection;
using GatedOpenAiServer = Maieutics.Testing.GatedModelProvider;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendTurnQueueIntegrationTests
{
    private const string Answer = "queued answer";

    private readonly ITestOutputHelper _output;

    /// <summary>The current test's gated provider, so wait-timeout diagnostics can report
    /// whether the run under test ever reached the provider gate. Null until a test that
    /// needs the diagnostic sets it; one class instance serves exactly one test.</summary>
    private GatedOpenAiServer? _diagnosticProvider;

    public FrontendTurnQueueIntegrationTests(ITestOutputHelper output) => this._output = output;

    private void Trace(string message) =>
        TestTrace.Write(_output, "queue-test", message);

    [Fact(Timeout = 120_000)]
    public async Task QueuedTurnsRunSeriallyInTheEnqueuedOrder()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        // Park the direct run first so the queue drains behind in-flight work.
        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await provider.WaitForParkedAsync(1, deadline.Token);
        await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");

        // The direct run settles, then the queue drains head-first, serially. The gated
        // provider parks every run, so each release settles exactly one run: the direct
        // one, then each queued item as the worker submits it.
        await ReleaseOneAsync(provider, deadline.Token);
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue), deadline.Token);
        await ReleaseOneAsync(provider, deadline.Token);
        var afterFirst = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        Assert.Equal(["direct question", "queued one"], afterFirst);

        await ReleaseOneAsync(provider, deadline.Token);
        var afterSecond = await WaitForUserTextsAsync(harness, sessionId, 3, deadline.Token);
        Assert.Equal(["direct question", "queued one", "queued two"], afterSecond);

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    /// <summary>Deterministic regression for the release bookkeeping that made
    /// <see cref="QueuedTurnsRunSeriallyInTheEnqueuedOrder" /> time out on CI. The gated
    /// provider used to leave a released request listed until the server-side continuation
    /// removed it, so a release issued inside that window hit the stale head and silently
    /// no-opped while the request that was really parked stayed parked forever. Synthetic
    /// entries pin that window shut: with the old bookkeeping the second release necessarily
    /// lands on the already-released head and the still-parked request is never completed.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ReleasesReachOnlyRequestsStillAwaitingRelease()
    {
        _ = TestContext.Current.CancellationToken;
        await using var provider = new GatedOpenAiServer(answer: Answer);
        var first = provider.ParkForTest();
        var second = provider.ParkForTest();
        Assert.Equal(2, provider.ParkedRequests);

        provider.ReleaseNext();
        Assert.True(first.Task.IsCompleted, "the oldest awaiting request is the one released");
        Assert.Equal(1, provider.ParkedRequests);

        provider.ReleaseNext();
        Assert.True(second.Task.IsCompleted, "the second release must reach the request that is still parked instead of "
                                             + "completing an already-released entry again");
        Assert.Equal(0, provider.ParkedRequests);

        provider.ReleaseAll();
        Assert.Equal(0, provider.ParkedRequests);
    }

    [Fact(Timeout = 120_000)]
    public async Task DirectTurnStaysBusyRejectedWhileTheQueueIsActive()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await provider.WaitForParkedAsync(1, deadline.Token);
        await EnqueueAsync(harness, sessionId, deadline.Token, "queued one");

        // Direct submissions keep their semantics: busy, never queued (invariant 4).
        var direct = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/turns",
            new { text = "direct second" },
            deadline.Token);
        Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
        var error = await direct.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("agent_busy", error.GetProperty("code").GetString());

        // The direct run completes first; the queue item the worker submits after it
        // settles next (each gated provider request parks until its own release).
        await ReleaseOneAsync(provider, deadline.Token);
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue), deadline.Token);
        await ReleaseOneAsync(provider, deadline.Token);
        var texts = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        Assert.Equal(["direct question", "queued one"], texts);

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueueSnapshotCarriesRunningItemAndOrderedPendingItems()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var empty = await GetQueueAsync(harness, sessionId, deadline.Token);
        Assert.Equal(sessionId, empty.GetProperty("sessionId").GetString());
        Assert.Equal(64, empty.GetProperty("capacity").GetInt32());
        Assert.False(HasRunningItem(empty));
        Assert.Empty(ItemIds(empty));

        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await provider.WaitForParkedAsync(1, deadline.Token);
        var ids = await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");

        // The worker dequeues the head as soon as it is enqueued and then waits out the
        // direct run, so the settled pending view shows the remaining item in run order.
        await WaitForQueueAsync(
            harness,
            sessionId,
            queue => !HasRunningItem(queue) && ItemIds(queue).SequenceEqual([ids[1]]),
            deadline.Token);
        var queued = await GetQueueAsync(harness, sessionId, deadline.Token);
        var items = queued.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("queued two", items[0].GetProperty("text").GetString());
        Assert.Equal(DateTimeOffset.UtcNow, DateTimeOffset.Parse(items[0].GetProperty("enqueuedAt").GetString()!, CultureInfo.InvariantCulture), TimeSpan.FromMinutes(1));

        // The first queued item runs with its run id once the direct run settles.
        await ReleaseOneAsync(provider, deadline.Token);
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[0]), deadline.Token);
        var running = await GetQueueAsync(harness, sessionId, deadline.Token);
        Assert.Equal(32, running.GetProperty("running").GetProperty("runId").GetString()!.Length);
        Assert.Equal([ids[1]], ItemIds(running));

        // The second item runs after the first settles; then the queue drains.
        await ReleaseOneAsync(provider, deadline.Token);
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[1]), deadline.Token);
        await ReleaseOneAsync(provider, deadline.Token);
        await WaitForUserTextsAsync(harness, sessionId, 3, deadline.Token);
        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueueItemDeleteRemovesQueuedItemsOnly()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await provider.WaitForParkedAsync(1, deadline.Token);
        var ids = await EnqueueAsync(
            harness,
            sessionId,
            deadline.Token,
            "queued one",
            "queued two",
            "queued three");

        // The worker dequeues the head as soon as it is enqueued (it then waits out the
        // direct run), so the deterministic removal target is a pending tail item.
        Assert.Equal(HttpStatusCode.NoContent, (await harness.Client.DeleteAsync(
                $"/v1/agent/sessions/{sessionId}/queue/{ids[2]}",
                deadline.Token))
            .StatusCode);

        // The direct run settles and the dequeued head starts; the deleted tail is gone
        // from the pending order and never runs.
        await ReleaseOneAsync(provider, deadline.Token);
        await WaitForQueueAsync(
            harness,
            sessionId,
            queue => HasRunningItem(queue, ids[0]) && ItemIds(queue).SequenceEqual([ids[1]]),
            deadline.Token);

        // The running item is not deletable: cancel the run instead. An unknown id is 404.
        var running = await harness.Client.DeleteAsync(
            $"/v1/agent/sessions/{sessionId}/queue/{ids[0]}",
            deadline.Token);
        Assert.Equal(HttpStatusCode.Conflict, running.StatusCode);
        var runningError = await running.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("item_running", runningError.GetProperty("code").GetString());

        var unknown = await harness.Client.DeleteAsync(
            $"/v1/agent/sessions/{sessionId}/queue/{Guid.NewGuid():N}",
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var unknownError = await unknown.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("not_found", unknownError.GetProperty("code").GetString());

        // The pending successor is removable too; neither deleted item ever ran.
        Assert.Equal(HttpStatusCode.NoContent, (await harness.Client.DeleteAsync(
                $"/v1/agent/sessions/{sessionId}/queue/{ids[1]}",
                deadline.Token))
            .StatusCode);
        await ReleaseOneAsync(provider, deadline.Token);
        var texts = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        Assert.Equal(["direct question", "queued one"], texts);

        // An unknown session is the usual typed 404.
        var missing = await harness.Client.DeleteAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/queue",
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueueClearRemovesQueuedItemsAndTheRunningItemCompletes()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await provider.WaitForParkedAsync(1, deadline.Token);
        Trace("direct run parked");
        var ids = await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");
        Trace("enqueued two items");

        await ReleaseOneAsync(provider, deadline.Token);
        Trace("released direct run");
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[0]), deadline.Token);
        Trace("first item running");

        // Clear removes the pending items only; the running item is untouched.
        Assert.Equal(HttpStatusCode.NoContent, (await harness.Client.DeleteAsync($"/v1/agent/sessions/{sessionId}/queue", deadline.Token))
            .StatusCode);
        var cleared = await GetQueueAsync(harness, sessionId, deadline.Token);
        Assert.True(HasRunningItem(cleared, ids[0]));
        Assert.Empty(ItemIds(cleared));
        Trace("queue cleared");

        await ReleaseOneAsync(provider, deadline.Token);
        Trace("released first item");
        var texts = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        Assert.Equal(["direct question", "queued one"], texts);
        Trace("two turns committed");
        Assert.Equal(HttpStatusCode.NoContent, (await harness.Client.DeleteAsync($"/v1/agent/sessions/{sessionId}/queue", deadline.Token))
            .StatusCode);
        Trace("no-op clear ok");
        await EnqueueAsync(harness, sessionId, deadline.Token, "queued after clear");
        Trace("fresh enqueued");

        // Clearing an empty queue is a no-op that still answers 204.

        // The next enqueue starts a fresh worker after the drained one exited; the item
        // runs to completion and the transcript keeps its committed order.
        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
        var afterRestart = await WaitForUserTextsAsync(harness, sessionId, 3, deadline.Token);
        Assert.Equal(["direct question", "queued one", "queued after clear"], afterRestart);

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueueFullRejectsTheWholeBatch()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var full = Enumerable.Range(0, 64).Select(index => $"queued {index}").ToArray();
        var ids = await EnqueueAsync(harness, sessionId, deadline.Token, full);
        Assert.Equal(64, ids.Count());

        // The first item is running, so 63 are pending: a two-item batch would exceed
        // the capacity and is rejected whole.
        await WaitForQueueAsync(
            harness,
            sessionId,
            queue => HasRunningItem(queue, ids[0]) && ItemIds(queue).Length == 63,
            deadline.Token);
        var over = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = new[] { new { text = "one over" }, new { text = "two over" } } },
            deadline.Token);
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        var error = await over.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("queue_full", error.GetProperty("code").GetString());
        Assert.Equal(63, ItemIds(await GetQueueAsync(harness, sessionId, deadline.Token)).Count());

        // A batch above the request shape itself is a typed 400 before the capacity check.
        var oversizedBatch = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = Enumerable.Range(0, 65).Select(index => new { text = $"x{index}" }) },
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, oversizedBatch.StatusCode);
        var batchError = await oversizedBatch.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("invalid_request", batchError.GetProperty("code").GetString());

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueueRejectsCommandCellsInvalidTextsAndUnknownSessions()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var command = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = new[] { new { text = "%status" } } },
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, command.StatusCode);
        var commandError = await command.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("invalid_request", commandError.GetProperty("code").GetString());
        Assert.Contains("turn endpoint", commandError.GetProperty("message").GetString());

        var empty = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = new[] { new { text = "   " } } },
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("invalid_request", (await empty.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString());

        var none = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = Array.Empty<object>() },
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        var oversized = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = new[] { new { text = new string('x', 32_001) } } },
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal("agent_input_too_large", (await oversized.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString());

        var missing = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/queue",
            new { items = new[] { new { text = "hello" } } },
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString());

        // The snapshot route mirrors the other session-addressed routes' typed 404.
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.GetAsync($"/v1/agent/sessions/{Guid.NewGuid():N}/queue", deadline.Token))
            .StatusCode);

        // Nothing from the rejected batches was enqueued.
        Assert.Empty(ItemIds(await GetQueueAsync(harness, sessionId, deadline.Token)));

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 180_000)]
    public async Task QueueUpdatedFramesReachEveryEventsSocket()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(160));
        var provider = new GatedOpenAiServer(answer: Answer);
        // Exposed to the shared wait helpers for their timeout diagnostics.
        _diagnosticProvider = provider;
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        Trace("harness booted");
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        Trace("session resolved");

        await using var first = await harness.OpenEventsAsync(sessionId, deadline.Token);
        Assert.Equal("hello", (await first.ReceiveFrameAsync(deadline.Token)).GetProperty("type").GetString());
        Trace("first socket hello");

        // The worker starts the first item immediately, so the socket's wake observes the
        // first stable state: the dequeued head running and the rest pending in order.
        var ids = await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");
        Assert.Equal(2, ids.Count());
        Trace($"enqueued {string.Join(",", ids)}");
        var firstRunning = await CollectQueueFrameUntilAsync(
            first,
            queue => HasRunningItem(queue, ids[0]) && ItemIds(queue).SequenceEqual([ids[1]]),
            deadline.Token);
        Trace("first socket saw running state");
        // Frame items carry ids only; the text lives in the GET snapshot.
        Assert.False(firstRunning.GetProperty("items")[0].TryGetProperty("text", out _));

        // A second socket opened later receives the current state right after hello —
        // the queue wake is raced before the announced run's serving — and then serves
        // the running stream like the first socket does.
        await using var second = await harness.OpenEventsAsync(sessionId, deadline.Token);
        Assert.Equal("hello", (await second.ReceiveFrameAsync(deadline.Token)).GetProperty("type").GetString());
        Trace("second socket hello");
        await CollectQueueFrameUntilAsync(
            second,
            queue => HasRunningItem(queue, ids[0]) && ItemIds(queue).SequenceEqual([ids[1]]),
            deadline.Token);
        Trace("second socket saw running state");

        // An enqueue while the head runs wakes the sockets too, but both are serving the
        // head's run stream, so they observe that mutation's state only once the serving
        // completes — the full-state frame makes the intermediate state unnecessary.
        var thirdIds = await EnqueueAsync(harness, sessionId, deadline.Token, "queued three");
        Trace($"enqueued third {thirdIds[0]}");

        // Each release settles one run; the next item's running transition is the next
        // stable state both sockets observe (one release per gated provider request).
        // The worker's provider request is issued asynchronously after the run starts, so
        // a release must WAIT for the request to park — releasing earlier is a no-op that
        // parks the request forever (the Windows CI failure: every step before this was
        // instant, and the released-into-nothing request froze the run mid-flight).
        await provider.WaitForParkedAsync(1, deadline.Token);
        Trace($"parked before release: {provider.ParkedRequests}");
        await ReleaseOneAsync(provider, deadline.Token);
        Trace("released item one");
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[1]), deadline.Token);
        await CollectQueueFrameUntilAsync(
            first,
            queue => HasRunningItem(queue, ids[1]) && ItemIds(queue).SequenceEqual([thirdIds[0]]),
            deadline.Token);
        await CollectQueueFrameUntilAsync(
            second,
            queue => HasRunningItem(queue, ids[1]) && ItemIds(queue).SequenceEqual([thirdIds[0]]),
            deadline.Token);
        Trace("both sockets saw second running state");

        await provider.WaitForParkedAsync(1, deadline.Token);
        await ReleaseOneAsync(provider, deadline.Token);
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, thirdIds[0]), deadline.Token);
        await provider.WaitForParkedAsync(1, deadline.Token);
        await ReleaseOneAsync(provider, deadline.Token);
        Trace("released items two and three");
        await WaitForUserTextsAsync(harness, sessionId, 3, deadline.Token);
        Trace("transcript committed all three");

        // A socket that opens after the queue drained receives the (empty) current state
        // right after its run replay, without any further mutation.
        await using var third = await harness.OpenEventsAsync(sessionId, deadline.Token);
        Assert.Equal("hello", (await third.ReceiveFrameAsync(deadline.Token)).GetProperty("type").GetString());
        Trace("third socket hello");
        await CollectQueueFrameUntilAsync(third, queue => !HasRunningItem(queue), deadline.Token);
        Trace("third socket saw drained state");

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
        Trace("drained");
    }

    /// <summary>Deterministic regression for the queue.updated liveness failure behind
    /// <see cref="QueueUpdatedFramesReachEveryEventsSocket" /> (CI: provider parked=1,
    /// frames seen running=none, 45 s hang). The old serving loop deferred every queue
    /// frame behind the whole run stream: the running transition published while the
    /// socket was already serving the parked run was never flushed until the run
    /// settled, and a client gating on that frame starved with it. The gate holds the
    /// dequeue window open deterministically, so the transition lands mid-serve by
    /// construction; the frame must arrive while the run is still parked.</summary>
    [Fact(Timeout = 120_000)]
    public async Task RunningTransitionFlushesWhileTheRunIsServed()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        _diagnosticProvider = provider;
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await using var socket = await harness.OpenEventsAsync(sessionId, deadline.Token);
        Assert.Equal("hello", (await socket.ReceiveFrameAsync(deadline.Token)).GetProperty("type").GetString());

        // Hold the worker between the submission handshake and the Running publish, so
        // the enqueue's dequeue frame (running=none) is already flushed and the socket
        // has entered the run's serving loop before the transition exists.
        var releaseRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.TurnQueue.RunningPublishGateForTests = token => releaseRunning.Task.WaitAsync(token);
        try
        {
            var ids = await EnqueueAsync(harness, sessionId, deadline.Token, "gated one");
            await provider.WaitForParkedAsync(1, deadline.Token);
            Trace("run parked at the provider");

            // The transition publishes while the run is parked mid-serve — the exact
            // interleaving the old loop deferred for the run's whole lifetime.
            releaseRunning.SetResult();
            var running = await CollectQueueFrameUntilAsync(
                socket,
                queue => HasRunningItem(queue, ids[0]),
                deadline.Token);
            Assert.True(running.TryGetProperty("running", out _));

            await ReleaseOneAsync(provider, deadline.Token);
            await WaitForUserTextsAsync(harness, sessionId, 1, deadline.Token);
        }
        finally
        {
            harness.TurnQueue.RunningPublishGateForTests = null;
            // A failure before the release must unpark the worker, or harness disposal
            // hangs on the worker completion and masks the real assertion.
            releaseRunning.TrySetResult();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task WorkerSurvivesADirectSubmissionRace()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        // A direct run holds the single-run gate when the worker pops its first item: the
        // submission hits the busy gate, waits the direct run out, and retries.
        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await provider.WaitForParkedAsync(1, deadline.Token);
        var ids = await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");

        // The worker dequeued the first item and is waiting for the in-flight run.
        await WaitForQueueAsync(
            harness,
            sessionId,
            queue => !ItemIds(queue).Contains(ids[0]) && ItemIds(queue).Contains(ids[1]),
            deadline.Token);

        await ReleaseOneAsync(provider, deadline.Token);
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[0]), deadline.Token);
        await ReleaseOneAsync(provider, deadline.Token);
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[1]), deadline.Token);
        await ReleaseOneAsync(provider, deadline.Token);
        var texts = await WaitForUserTextsAsync(harness, sessionId, 3, deadline.Token);
        Assert.Equal(["direct question", "queued one", "queued two"], texts);

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task DrainReleasesARunParkedWhileTheQueueLooksEmpty()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        // Reproduce the window the drain has to survive: a direct run holds the single-run
        // gate (parked at the provider), and the worker dequeues its item while that run is
        // still in flight. The dequeue→running handshake then blocks on the busy gate, so the
        // queue reports neither a pending nor a running item while a provider request is
        // already on its way. A drain that samples emptiness once and returns — or that skips
        // the release on such a sample — leaves that request parked forever, which is exactly
        // the Windows CI hang (the harness's host disposal then blocks on the run's lease).
        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await provider.WaitForParkedAsync(1, deadline.Token);
        var ids = await EnqueueAsync(harness, sessionId, deadline.Token, "queued one");
        await WaitForQueueAsync(
            harness,
            sessionId,
            queue => !ItemIds(queue).Contains(ids[0]),
            deadline.Token);

        // The worker has dequeued, is waiting out the parked direct run, and the queue is
        // legitimately empty from the API's point of view.
        var observed = await GetQueueAsync(harness, sessionId, deadline.Token);
        Assert.Empty(ItemIds(observed));
        Assert.False(HasRunningItem(observed));

        // The drain must still converge: it keeps releasing as each run reaches the gate.
        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);

        var texts = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        Assert.Equal(["direct question", "queued one"], texts);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueuePinLeaseIsHeldWhileWorkIsQueuedAndReleasedOnDrain()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer(answer: Answer);
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        var manager = harness.SessionManager;
        var addressed = new AgentSessionId(Guid.ParseExact(sessionId, "N"));

        // No queue work: no pin. The lease is acquired synchronously with the enqueue
        // commit, so a session with queued work is pinned from that moment.
        Assert.False(manager.IsPinned(addressed));
        await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");
        Assert.True(manager.IsPinned(addressed));

        // The queue drains (the drain loop releases each gated run as the worker
        // submits it) and the lease is released with the drain, deterministically
        // before the snapshot can report the drained state.
        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
        var texts = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        Assert.Equal(["queued one", "queued two"], texts);
        Assert.False(manager.IsPinned(addressed));
    }

    private static async Task<Harness> StartHarnessAsync(CancellationToken cancellationToken, GatedOpenAiServer provider)
    {
        // The shared harness extracts the endpoint from the provider instance, gated or not.
        return await Harness.StartAsync(
            cancellationToken,
            provider,
            hanging: false);
    }

    private static CancellationTokenSource CreateDeadline(
        CancellationToken cancellationToken,
        TimeSpan timeout) => TestDeadlines.Create(cancellationToken, timeout);

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

    private async Task WaitForQueueAsync(
        Harness harness,
        string sessionId,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(45));
        var polls = 0;
        // The queue publishes a version on every mutation; a read that does not match yet
        // parks on that signal instead of re-reading on a timer.
        var version = harness.TurnQueue.FrameSnapshot(sessionId).Version;
        try
        {
            while (true)
            {
                var queue = await GetQueueAsync(harness, sessionId, wait.Token).ConfigureAwait(false);
                polls++;
                if (predicate(queue)) return;

                if (polls % 10 == 0)
                    Trace($"queue poll #{polls}: {QueueSummary(queue)}");
                version = await harness.TurnQueue
                    .WaitForChangeAsync(sessionId, version, wait.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (wait.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"the queue state never matched after {polls} polls; provider parked={_diagnosticProvider?.ParkedRequests.ToString() ?? "n/a"}",
                exception);
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
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(texts.Length, items.Count());
        var ids = new string[items.Length];
        var firstPosition = items[0].GetProperty("position").GetInt32();
        for (var index = 0; index < items.Length; index++)
        {
            // Positions are 1-based pending indices in run order: a batch enqueued behind
            // work that is still queued continues after it, so only the offsets are fixed.
            Assert.Equal(firstPosition + index, items[index].GetProperty("position").GetInt32());
            ids[index] = items[index].GetProperty("id").GetString()!;
        }

        return ids;
    }

    /// <summary>Releases exactly one gated provider request, waiting for it to park
    /// first: the worker issues the request asynchronously after the run starts, so an
    /// unconditional release can fire into an empty gate and park the request forever.</summary>
    private static async Task ReleaseOneAsync(
        GatedOpenAiServer provider,
        CancellationToken cancellationToken)
    {
        await provider.WaitForParkedAsync(1, cancellationToken).ConfigureAwait(false);
        provider.ReleaseNext();
    }

    /// <summary>Releases parked provider requests until the queue has fully drained, so no
    /// run is still in flight when the harness's host disposes (the runtime configuration's
    /// disposal waits for the active run's profile lease).
    /// <para>
    /// Two properties are load-bearing, and the earlier version had neither:
    /// </para>
    /// <para>
    /// <b>Release on every poll, not only when the queue looks busy.</b> The worker removes an
    /// item from the pending list and publishes it as running only after the submission
    /// handshake completes, so a poll can legitimately see neither a pending nor a running item
    /// while a run is already on its way to the gate. Skipping the release on such a sample
    /// leaves that request parked with nothing left to release it. The submission handshake can
    /// also block for a long time — when a direct submission holds the single-run gate the
    /// worker waits the in-flight run out before retrying — which widens that window from a
    /// round trip into however long the other run is parked.
    /// </para>
    /// <para>
    /// <b>Require sustained emptiness.</b> A single empty sample is not evidence that the work
    /// finished; returning on one ends the drain before the run is visible, and the harness's
    /// host disposal then blocks on that run's profile lease until the xUnit timeout — the
    /// Windows CI failure this guards.
    /// </para></summary>
    private async Task DrainQueueAsync(
        Harness harness,
        GatedOpenAiServer provider,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        var releases = 0;
        try
        {
            // Fully signal-driven drain: wake when either the queue reaches idle (the
            // worker is gone and nothing is pending, running, or mid-dequeue) or a provider
            // request parks. A parked request is released immediately, which settles its run
            // and publishes the next queue change. This replaces the former "stable empty
            // across N polls" heuristic, whose samples could legitimately miss the
            // dequeue→running window.
            while (true)
            {
                var idle = harness.TurnQueue.WaitUntilIdleAsync(sessionId, wait.Token);
                var parked = provider.WaitForParkedAsync(1, wait.Token);
                var winner = await Task.WhenAny(idle, parked).ConfigureAwait(false);
                if (winner == idle)
                {
                    await idle.ConfigureAwait(false);
                    // No run can still own a request, but a vanished client may have left a
                    // zombie entry; release it so it cannot leak past the harness teardown.
                    provider.ReleaseAll();
                    Trace($"queue drained after {releases} release(s)");
                    return;
                }

                provider.ReleaseAll();
                releases++;
            }
        }
        catch (OperationCanceledException exception) when (wait.IsCancellationRequested)
        {
            using var diagnostic = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            string summary;
            try
            {
                summary = QueueSummary(await GetQueueAsync(harness, sessionId, diagnostic.Token));
            }
            catch (Exception readFailure) when (readFailure is OperationCanceledException or HttpRequestException)
            {
                summary = $"unreadable ({readFailure.GetType().Name})";
            }

            throw new InvalidOperationException(
                $"the queue did not drain after {releases} release(s); last observed: {summary}",
                exception);
        }
    }

    private static async Task<string[]> WaitForUserTextsAsync(
        Harness harness,
        string sessionId,
        int minimumTurns,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            // Capture the queue version before reading: a turn is committed during the
            // submission handshake, so any change published after this read is evidence the
            // transcript may have advanced. Waking on the queue's own mutation signal
            // replaces the former fixed polling interval.
            var version = harness.TurnQueue.FrameSnapshot(sessionId).Version;
            var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                wait.Token).ConfigureAwait(false);
            var turns = transcript.GetProperty("turns");
            if (turns.GetArrayLength() >= minimumTurns)
            {
                return turns.EnumerateArray()
                    .Select(turn => turn.GetProperty("messages")[0].GetProperty("parts")[0].GetProperty("text").GetString()!)
                    .ToArray();
            }

            await harness.TurnQueue.WaitForChangeAsync(sessionId, version, wait.Token).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> CollectQueueFrameUntilAsync(
        EventsConnection connection,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(45));
        var seen = new List<string>();
        try
        {
            while (true)
            {
                var frame = await connection.ReceiveFrameAsync(wait.Token).ConfigureAwait(false);
                var type = frame.GetProperty("type").GetString();
                if (type != "queue.updated") continue;
                var queue = frame.GetProperty("queue");
                seen.Add(QueueSummary(queue));
                if (predicate(queue)) return queue;
            }
        }
        catch (OperationCanceledException exception)
        {
            // The budgets above are sized for slow CI runners; a timeout here means the
            // frames genuinely never came — name what the socket actually delivered and
            // whether the run ever reached the provider gate (parked=0 means the
            // submission itself stalled before issuing the provider request).
            throw new InvalidOperationException(
                $"no matching queue.updated frame; provider parked={_diagnosticProvider?.ParkedRequests.ToString() ?? "n/a"}; "
                + $"frames seen: [{string.Join("; ", seen)}]",
                exception);
        }
    }

    private static string QueueSummary(JsonElement queue)
    {
        // `running` is optional on the wire: absent OR null both mean no running item.
        var running = queue.TryGetProperty("running", out var state) &&
                      state.ValueKind == JsonValueKind.Object
            ? state.GetProperty("itemId").GetString()
            : null;
        var items = ItemIds(queue);
        return $"running={running ?? "none"} items=[{string.Join(",", items)}]";
    }

    private static bool HasRunningItem(JsonElement queue, string? itemId = null)
    {
        if (!queue.TryGetProperty("running", out var running) ||
            running.ValueKind != JsonValueKind.Object) return false;

        var runningItemId = running.GetProperty("itemId").GetString();
        return itemId is null || runningItemId == itemId;
    }

    private static string[] ItemIds(JsonElement queue)
    {
        if (!queue.TryGetProperty("items", out var items)) return [];

        return items.EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();
    }

    [Fact(Timeout = 30_000)]
    public async Task AParkedRequestWhoseClientVanishesDoesNotBlockTheGate()
    {
        // Deterministic pin for the Debug-load hang: a client that sends a request and
        // vanishes while its release is pending used to leave an unreleasable entry at
        // the head of the gate, so every subsequent release popped the zombie and the
        // live request starved forever.
        var provider = new GatedOpenAiServer(answer: Answer);
        using var zombie = new TcpClient();
        await zombie.ConnectAsync(IPAddress.Loopback, provider.Port, TestContext.Current.CancellationToken);
        var request = Encoding.ASCII.GetBytes(
            "POST /v1/chat/completions HTTP/1.1\r\nHost: gated\r\nContent-Length: 2\r\n\r\n{}");
        await zombie.Client.SendAsync(request, TestContext.Current.CancellationToken);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(10));
        await provider.WaitForParkedAsync(1, wait.Token);

        zombie.Close();
        await provider.WaitForParkedDrainedAsync(wait.Token);

        // The gate must serve the LIVE head now that the zombie is gone.
        var probe = provider.ParkForTest();
        provider.ReleaseNext();
        await probe.Task.WaitAsync(wait.Token);
        Assert.True(probe.Task.IsCompleted);
    }
}
