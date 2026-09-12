using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Providers.OpenAI;
using Harness = Maieutics.Product.Tests.FrontendApiIntegrationTests.FrontendHarness;
using EventsConnection = Maieutics.Product.Tests.FrontendApiIntegrationTests.FrontendEventsConnection;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendTurnQueueIntegrationTests
{
    private const string Answer = "queued answer";

    private readonly ITestOutputHelper output;

    public FrontendTurnQueueIntegrationTests(ITestOutputHelper output) => this.output = output;

    private void Trace(string message) =>
        output.WriteLine($"[queue-test] {DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message}");

    [Fact(Timeout = 120_000)]
    public async Task QueuedTurnsRunSeriallyInTheEnqueuedOrder()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        // Park the direct run first so the queue drains behind in-flight work.
        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await WaitForParkedAsync(provider, 1, deadline.Token);
        await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");

        // The direct run settles, then the queue drains head-first, serially. The gated
        // provider parks every run, so each release settles exactly one run: the direct
        // one, then each queued item as the worker submits it.
        provider.ReleaseNext();
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue), deadline.Token);
        provider.ReleaseNext();
        var afterFirst = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        afterFirst.Should().Equal(["direct question", "queued one"]);

        provider.ReleaseNext();
        var afterSecond = await WaitForUserTextsAsync(harness, sessionId, 3, deadline.Token);
        afterSecond.Should().Equal(["direct question", "queued one", "queued two"]);

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task DirectTurnStaysBusyRejectedWhileTheQueueIsActive()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await WaitForParkedAsync(provider, 1, deadline.Token);
        await EnqueueAsync(harness, sessionId, deadline.Token, "queued one");

        // Direct submissions keep their semantics: busy, never queued (invariant 4).
        var direct = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/turns",
            new { text = "direct second" },
            deadline.Token);
        direct.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await direct.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        error.GetProperty("code").GetString().Should().Be("agent_busy");

        // The direct run completes first; the queue item the worker submits after it
        // settles next (each gated provider request parks until its own release).
        provider.ReleaseNext();
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue), deadline.Token);
        provider.ReleaseNext();
        var texts = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        texts.Should().Equal(["direct question", "queued one"]);

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueueSnapshotCarriesRunningItemAndOrderedPendingItems()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var empty = await GetQueueAsync(harness, sessionId, deadline.Token);
        empty.GetProperty("sessionId").GetString().Should().Be(sessionId);
        empty.GetProperty("capacity").GetInt32().Should().Be(64);
        HasRunningItem(empty).Should().BeFalse();
        ItemIds(empty).Should().BeEmpty();

        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await WaitForParkedAsync(provider, 1, deadline.Token);
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
        items[0].GetProperty("text").GetString().Should().Be("queued two");
        DateTimeOffset.Parse(items[0].GetProperty("enqueuedAt").GetString()!, CultureInfo.InvariantCulture)
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        // The first queued item runs with its run id once the direct run settles.
        provider.ReleaseNext();
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[0]), deadline.Token);
        var running = await GetQueueAsync(harness, sessionId, deadline.Token);
        running.GetProperty("running").GetProperty("runId").GetString()!.Should().HaveLength(32);
        ItemIds(running).Should().Equal([ids[1]]);

        // The second item runs after the first settles; then the queue drains.
        provider.ReleaseNext();
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[1]), deadline.Token);
        provider.ReleaseNext();
        await WaitForUserTextsAsync(harness, sessionId, 3, deadline.Token);
        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueueItemDeleteRemovesQueuedItemsOnly()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await WaitForParkedAsync(provider, 1, deadline.Token);
        var ids = await EnqueueAsync(
            harness,
            sessionId,
            deadline.Token,
            "queued one",
            "queued two",
            "queued three");

        // The worker dequeues the head as soon as it is enqueued (it then waits out the
        // direct run), so the deterministic removal target is a pending tail item.
        (await harness.Client.DeleteAsync(
                $"/v1/agent/sessions/{sessionId}/queue/{ids[2]}",
                deadline.Token))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The direct run settles and the dequeued head starts; the deleted tail is gone
        // from the pending order and never runs.
        provider.ReleaseNext();
        await WaitForQueueAsync(
            harness,
            sessionId,
            queue => HasRunningItem(queue, ids[0]) && ItemIds(queue).SequenceEqual([ids[1]]),
            deadline.Token);

        // The running item is not deletable: cancel the run instead. An unknown id is 404.
        var running = await harness.Client.DeleteAsync(
            $"/v1/agent/sessions/{sessionId}/queue/{ids[0]}",
            deadline.Token);
        running.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var runningError = await running.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        runningError.GetProperty("code").GetString().Should().Be("item_running");

        var unknown = await harness.Client.DeleteAsync(
            $"/v1/agent/sessions/{sessionId}/queue/{Guid.NewGuid():N}",
            deadline.Token);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var unknownError = await unknown.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        unknownError.GetProperty("code").GetString().Should().Be("not_found");

        // The pending successor is removable too; neither deleted item ever ran.
        (await harness.Client.DeleteAsync(
                $"/v1/agent/sessions/{sessionId}/queue/{ids[1]}",
                deadline.Token))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        provider.ReleaseNext();
        var texts = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        texts.Should().Equal(["direct question", "queued one"]);

        // An unknown session is the usual typed 404.
        var missing = await harness.Client.DeleteAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/queue",
            deadline.Token);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueueClearRemovesQueuedItemsAndTheRunningItemCompletes()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await WaitForParkedAsync(provider, 1, deadline.Token);
        var ids = await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");

        provider.ReleaseNext();
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[0]), deadline.Token);

        // Clear removes the pending items only; the running item is untouched.
        (await harness.Client.DeleteAsync($"/v1/agent/sessions/{sessionId}/queue", deadline.Token))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cleared = await GetQueueAsync(harness, sessionId, deadline.Token);
        HasRunningItem(cleared, ids[0]).Should().BeTrue();
        ItemIds(cleared).Should().BeEmpty();

        provider.ReleaseNext();
        var texts = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        texts.Should().Equal(["direct question", "queued one"]);

        // Clearing an empty queue is a no-op that still answers 204.
        (await harness.Client.DeleteAsync($"/v1/agent/sessions/{sessionId}/queue", deadline.Token))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The next enqueue starts a fresh worker after the drained one exited; the item
        // runs to completion and the transcript keeps its committed order.
        await EnqueueAsync(harness, sessionId, deadline.Token, "queued after clear");
        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
        var afterRestart = await WaitForUserTextsAsync(harness, sessionId, 3, deadline.Token);
        afterRestart.Should().Equal(["direct question", "queued one", "queued after clear"]);

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueueFullRejectsTheWholeBatch()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var full = Enumerable.Range(0, 64).Select(index => $"queued {index}").ToArray();
        var ids = await EnqueueAsync(harness, sessionId, deadline.Token, full);
        ids.Should().HaveCount(64);

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
        over.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await over.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        error.GetProperty("code").GetString().Should().Be("queue_full");
        ItemIds(await GetQueueAsync(harness, sessionId, deadline.Token)).Should().HaveCount(63);

        // A batch above the request shape itself is a typed 400 before the capacity check.
        var oversizedBatch = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = Enumerable.Range(0, 65).Select(index => new { text = $"x{index}" }) },
            deadline.Token);
        oversizedBatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var batchError = await oversizedBatch.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        batchError.GetProperty("code").GetString().Should().Be("invalid_request");

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueueRejectsCommandCellsInvalidTextsAndUnknownSessions()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var command = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = new[] { new { text = "%status" } } },
            deadline.Token);
        command.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var commandError = await command.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        commandError.GetProperty("code").GetString().Should().Be("invalid_request");
        commandError.GetProperty("message").GetString().Should().Contain("turn endpoint");

        var empty = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = new[] { new { text = "   " } } },
            deadline.Token);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await empty.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString().Should().Be("invalid_request");

        var none = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = Array.Empty<object>() },
            deadline.Token);
        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var oversized = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = new[] { new { text = new string('x', 32_001) } } },
            deadline.Token);
        oversized.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await oversized.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString().Should().Be("agent_input_too_large");

        var missing = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/queue",
            new { items = new[] { new { text = "hello" } } },
            deadline.Token);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString().Should().Be("not_found");

        // The snapshot route mirrors the other session-addressed routes' typed 404.
        (await harness.Client.GetAsync($"/v1/agent/sessions/{Guid.NewGuid():N}/queue", deadline.Token))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Nothing from the rejected batches was enqueued.
        ItemIds(await GetQueueAsync(harness, sessionId, deadline.Token)).Should().BeEmpty();

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 180_000)]
    public async Task QueueUpdatedFramesReachEveryEventsSocket()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(160));
        var provider = new GatedOpenAiServer();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        Trace("harness booted");
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        Trace("session resolved");

        await using var first = await harness.OpenEventsAsync(sessionId, deadline.Token);
        (await first.ReceiveFrameAsync(deadline.Token)).GetProperty("type").GetString().Should().Be("hello");
        Trace("first socket hello");

        // The worker starts the first item immediately, so the socket's wake observes the
        // first stable state: the dequeued head running and the rest pending in order.
        var ids = await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");
        ids.Should().HaveCount(2);
        Trace($"enqueued {string.Join(",", ids)}");
        var firstRunning = await CollectQueueFrameUntilAsync(
            first,
            queue => HasRunningItem(queue, ids[0]) && ItemIds(queue).SequenceEqual([ids[1]]),
            deadline.Token);
        Trace("first socket saw running state");
        // Frame items carry ids only; the text lives in the GET snapshot.
        firstRunning.GetProperty("items")[0].TryGetProperty("text", out _).Should().BeFalse();

        // A second socket opened later receives the current state right after hello —
        // the queue wake is raced before the announced run's serving — and then serves
        // the running stream like the first socket does.
        await using var second = await harness.OpenEventsAsync(sessionId, deadline.Token);
        (await second.ReceiveFrameAsync(deadline.Token)).GetProperty("type").GetString().Should().Be("hello");
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
        await WaitForParkedAsync(provider, 1, deadline.Token);
        Trace($"parked before release: {provider.ParkedRequests}");
        provider.ReleaseNext();
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

        await WaitForParkedAsync(provider, 1, deadline.Token);
        provider.ReleaseNext();
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, thirdIds[0]), deadline.Token);
        await WaitForParkedAsync(provider, 1, deadline.Token);
        provider.ReleaseNext();
        Trace("released items two and three");
        await WaitForUserTextsAsync(harness, sessionId, 3, deadline.Token);
        Trace("transcript committed all three");

        // A socket that opens after the queue drained receives the (empty) current state
        // right after its run replay, without any further mutation.
        await using var third = await harness.OpenEventsAsync(sessionId, deadline.Token);
        (await third.ReceiveFrameAsync(deadline.Token)).GetProperty("type").GetString().Should().Be("hello");
        Trace("third socket hello");
        await CollectQueueFrameUntilAsync(third, queue => !HasRunningItem(queue), deadline.Token);
        Trace("third socket saw drained state");

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
        Trace("drained");
    }

    [Fact(Timeout = 120_000)]
    public async Task WorkerSurvivesADirectSubmissionRace()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        // A direct run holds the single-run gate when the worker pops its first item: the
        // submission hits the busy gate, waits the direct run out, and retries.
        await harness.SubmitTurnAsync(sessionId, "direct question", deadline.Token);
        await WaitForParkedAsync(provider, 1, deadline.Token);
        var ids = await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");

        // The worker dequeued the first item and is waiting for the in-flight run.
        await WaitForQueueAsync(
            harness,
            sessionId,
            queue => !ItemIds(queue).Contains(ids[0]) && ItemIds(queue).Contains(ids[1]),
            deadline.Token);

        provider.ReleaseNext();
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[0]), deadline.Token);
        provider.ReleaseNext();
        await WaitForQueueAsync(harness, sessionId, queue => HasRunningItem(queue, ids[1]), deadline.Token);
        provider.ReleaseNext();
        var texts = await WaitForUserTextsAsync(harness, sessionId, 3, deadline.Token);
        texts.Should().Equal(["direct question", "queued one", "queued two"]);

        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueuePinLeaseIsHeldWhileWorkIsQueuedAndReleasedOnDrain()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(100));
        var provider = new GatedOpenAiServer();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        var manager = harness.SessionManager;
        var addressed = new Maieutics.Agent.AgentSessionId(Guid.ParseExact(sessionId, "N"));

        // No queue work: no pin. The lease is acquired synchronously with the enqueue
        // commit, so a session with queued work is pinned from that moment.
        manager.IsPinned(addressed).Should().BeFalse();
        await EnqueueAsync(harness, sessionId, deadline.Token, "queued one", "queued two");
        manager.IsPinned(addressed).Should().BeTrue();

        // The queue drains (the drain loop releases each gated run as the worker
        // submits it) and the lease is released with the drain, deterministically
        // before the snapshot can report the drained state.
        await DrainQueueAsync(harness, provider, sessionId, deadline.Token);
        var texts = await WaitForUserTextsAsync(harness, sessionId, 2, deadline.Token);
        texts.Should().Equal(["queued one", "queued two"]);
        manager.IsPinned(addressed).Should().BeFalse();
    }

    private static async Task<Harness> StartHarnessAsync(CancellationToken cancellationToken, GatedOpenAiServer provider)
    {
        // The shared harness extracts the endpoint from its known provider types, so the
        // gated server injects its endpoint through the configuration transform instead.
        return await Harness.StartAsync(
            cancellationToken,
            provider,
            hanging: false,
            transformConfiguration: body => body.Replace(
                "\"Endpoint\": \"\"",
                $"\"Endpoint\": \"{provider.Endpoint}\"",
                StringComparison.Ordinal));
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private static async Task<JsonElement> GetQueueAsync(
        Harness harness,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var response = await harness.Client
            .GetAsync($"/v1/agent/sessions/{sessionId}/queue", cancellationToken)
            .ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> WaitForQueueAsync(
        Harness harness,
        string sessionId,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(45));
        var polls = 0;
        while (true)
        {
            var queue = await GetQueueAsync(harness, sessionId, wait.Token).ConfigureAwait(false);
            polls++;
            if (predicate(queue)) return queue;

            if (polls % 10 == 0)
                Trace($"queue poll #{polls}: {QueueSummary(queue)}");
            await Task.Delay(50, wait.Token).ConfigureAwait(false);
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
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        items.Should().HaveCount(texts.Length);
        var ids = new string[items.Length];
        var firstPosition = items[0].GetProperty("position").GetInt32();
        for (var index = 0; index < items.Length; index++)
        {
            // Positions are 1-based pending indices in run order: a batch enqueued behind
            // work that is still queued continues after it, so only the offsets are fixed.
            items[index].GetProperty("position").GetInt32().Should().Be(firstPosition + index);
            ids[index] = items[index].GetProperty("id").GetString()!;
        }

        return ids;
    }

    private static async Task WaitForParkedAsync(
        GatedOpenAiServer provider,
        int count,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(45));
        while (provider.ParkedRequests < count)
        {
            await Task.Delay(25, wait.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Releases parked provider requests until the queue has fully drained, so no
    /// run is still in flight when the harness's host disposes (the runtime configuration's
    /// disposal waits for the active run's profile lease).</summary>
    private static async Task DrainQueueAsync(
        Harness harness,
        GatedOpenAiServer provider,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var queue = await GetQueueAsync(harness, sessionId, wait.Token).ConfigureAwait(false);
            if (!HasRunningItem(queue) && ItemIds(queue).Length == 0) return;

            provider.ReleaseAll();
            await Task.Delay(50, wait.Token).ConfigureAwait(false);
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

            await Task.Delay(50, wait.Token).ConfigureAwait(false);
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
            // frames genuinely never came — name what the socket actually delivered.
            throw new InvalidOperationException(
                $"no matching queue.updated frame; frames seen: [{string.Join("; ", seen)}]", exception);
        }
    }

    private static string QueueSummary(JsonElement queue)
    {
        var running = queue.TryGetProperty("running", out var state)
            ? state.GetProperty("itemId").GetString()
            : null;
        var items = ItemIds(queue);
        return $"running={running ?? "none"} items=[{string.Join(",", items)}]";
    }

    private static bool HasRunningItem(JsonElement queue, string? itemId = null)
    {
        if (!queue.TryGetProperty("running", out var running)) return false;

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

    /// <summary>A chat-completions SSE server whose responses are gated one request at a
    /// time: every provider request parks until the test releases it, so run lifetimes and
    /// queue transitions can be staged deterministically.</summary>
    private sealed class GatedOpenAiServer : IAsyncDisposable
    {
        private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

        private readonly CancellationTokenSource cancellation = new();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Task serveLoop;
        private readonly Lock gate = new();
        private readonly List<TaskCompletionSource> parked = [];
        private readonly List<Task> connections = [];

        public GatedOpenAiServer()
        {
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
            serveLoop = ServeAsync(cancellation.Token);
        }

        public Uri Endpoint { get; }

        /// <summary>How many provider requests have arrived and are waiting for release.</summary>
        public int ParkedRequests
        {
            get
            {
                lock (gate)
                {
                    return parked.Count;
                }
            }
        }

        /// <summary>Releases the oldest parked provider request.</summary>
        public void ReleaseNext()
        {
            lock (gate)
            {
                if (parked.Count > 0) parked[0].TrySetResult();
            }
        }

        /// <summary>Releases every currently parked provider request.</summary>
        public void ReleaseAll()
        {
            lock (gate)
            {
                foreach (var parkedRequest in parked) parkedRequest.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            ReleaseAll();
            Task[] connectionTasks;
            lock (gate)
            {
                connectionTasks = [.. connections];
            }

            try
            {
                await Task.WhenAll(connectionTasks).ConfigureAwait(false);
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
            }

            try
            {
                await serveLoop.ConfigureAwait(false);
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
            }

            cancellation.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    Task connection = ServeClientAsync(client, cancellationToken);
                    lock (gate)
                    {
                        connections.Add(connection);
                    }
                }
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
            }
        }

        private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                await using var stream = client.GetStream();
                var buffer = new byte[64 * 1024];
                while (!cancellationToken.IsCancellationRequested)
                {
                    // The gate registers only after the request fully arrived, so release
                    // order matches request order (runs are serialized by the session).
                    await ReadRequestAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (gate)
                    {
                        parked.Add(release);
                    }

                    await release.Task.ConfigureAwait(false);
                    lock (gate)
                    {
                        parked.Remove(release);
                    }

                    if (cancellationToken.IsCancellationRequested) return;

                    var body = Encoding.UTF8.GetBytes(
                        "data: {\"choices\":[{\"delta\":{\"content\":\"" + Answer + "\"}}]}\n\n" +
                        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
                    var headers = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n\r\n");
                    await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or EndOfStreamException or SocketException or IOException
                    or ObjectDisposedException)
            {
            }
            finally
            {
                client.Dispose();
            }
        }

        private static async Task ReadRequestAsync(
            NetworkStream stream,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            var total = 0;
            int headerEnd;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();

                total += read;
                headerEnd = IndexOf(buffer, total, HeaderTerminator);
                if (headerEnd >= 0) break;

                if (total == buffer.Length)
                    throw new InvalidOperationException("The request headers overflowed the test buffer.");
            }

            var contentLength = ParseContentLength(Encoding.ASCII.GetString(buffer, 0, headerEnd));
            var bodyEnd = headerEnd + HeaderTerminator.Length + contentLength;
            while (total < bodyEnd)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();

                total += read;
            }
        }

        private static int IndexOf(byte[] buffer, int length, byte[] pattern)
        {
            for (var start = 0; start <= length - pattern.Length; start++)
            {
                var matched = true;
                for (var offset = 0; offset < pattern.Length; offset++)
                    if (buffer[start + offset] != pattern[offset])
                    {
                        matched = false;
                        break;
                    }

                if (matched) return start;
            }

            return -1;
        }

        private static int ParseContentLength(string headers)
        {
            foreach (var line in headers.Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    return int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);

            return 0;
        }
    }
}
