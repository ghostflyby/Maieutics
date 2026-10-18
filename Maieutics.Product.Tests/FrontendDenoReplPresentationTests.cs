using System.Collections.ObjectModel;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.DenoRepl;
using Maieutics.Frontend;

namespace Maieutics.Product.Tests;

public sealed class FrontendDenoReplPresentationTests
{
    private sealed class FakeTarget : IFrontendPresentationTarget
    {
        public List<(string Type, string? DisplayId, JsonElement Data)> Published { get; } = [];

        public void PublishPresentation(
            string type,
            string? displayId,
            JsonElement data,
            CancellationToken cancellationToken)
        {
            Published.Add((type, displayId, data.Clone()));
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TrackedDisplayAndUpdatesCarryAStableDisplayId()
    {
        var target = new FakeTarget();
        var sink = new FrontendDenoReplPresentationSink(target);
        var displayId = ReplDisplayId.Create();

        await sink.DisplayTrackedAsync(
            ReplDisplayBundle.FromMarkdown("# hello"),
            displayId,
            EmptyMetadata(),
            TestContext.Current.CancellationToken);
        await sink.UpdateDisplayAsync(
            displayId,
            ReplDisplayBundle.FromText("updated"),
            EmptyMetadata(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, target.Published.Count());
        Assert.Equal("repl.display", target.Published[0].Type);
        Assert.Equal("repl.updateDisplay", target.Published[1].Type);
        Assert.All(target.Published, entry => Assert.True(entry.DisplayId == displayId.Value));
        Assert.Equal("# hello", target.Published[0].Data.GetProperty("text/markdown").GetString());
        Assert.Equal("updated", target.Published[1].Data.GetProperty("text/plain").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task UntrackedDisplayPublishesWithoutDisplayIdAndErrorsCarryTheBundle()
    {
        var target = new FakeTarget();
        var sink = new FrontendDenoReplPresentationSink(target);

        await sink.DisplayAsync(
            ReplDisplayBundle.FromText("hi"),
            EmptyMetadata(),
            TestContext.Current.CancellationToken);
        await sink.PublishErrorAsync("Boom", "broken", [], TestContext.Current.CancellationToken);

        Assert.Equal("repl.display", target.Published[0].Type);
        Assert.Null(target.Published[0].DisplayId);
        Assert.Equal("repl.error", target.Published[1].Type);
        Assert.Contains("Boom: broken", target.Published[1].Data.GetProperty("text/plain").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task NativeViewFamilyAnnouncementPassesThroughVerbatim()
    {
        // The custom-UI framework's native announcement mime (ADR 0038) must
        // ride a display bundle untouched: the presentation sink's only
        // mime-aware behavior is the binary-mime denylist, and this key is
        // not a binary mime. Locking that zero-interpretation promise.
        var target = new FakeTarget();
        var sink = new FrontendDenoReplPresentationSink(target);
        var announcement = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["modelId"] = "0b71a2b6-4b1e-4c66-9a06-93e0e2b60a01",
                ["viewFamily"] = "maieutics/form",
                ["version"] = "1.0",
                ["state"] = new Dictionary<string, object?> { ["fields"] = Array.Empty<string>() },
            });
        var bundle = new ReplDisplayBundle(new ReadOnlyDictionary<string, JsonElement>(
            new Dictionary<string, JsonElement>
            {
                ["application/vnd.maieutics.view+json"] = announcement,
                ["text/plain"] = JsonSerializer.SerializeToElement("form", ReplJsonContext.Default.String),
            }));

        await sink.DisplayAsync(bundle, EmptyMetadata(), TestContext.Current.CancellationToken);

        Assert.Single(target.Published);
        var data = target.Published[0].Data;
        Assert.Equal("maieutics/form", data.GetProperty("application/vnd.maieutics.view+json").GetProperty("viewFamily").GetString());
        Assert.Equal("0b71a2b6-4b1e-4c66-9a06-93e0e2b60a01", data.GetProperty("application/vnd.maieutics.view+json").GetProperty("modelId").GetString());
        Assert.Equal("form", data.GetProperty("text/plain").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task ClearedOutputPublishesAnEmptyClearFrame()
    {
        var target = new FakeTarget();
        var sink = new FrontendDenoReplPresentationSink(target);

        await sink.ClearOutputAsync(wait: false, TestContext.Current.CancellationToken);

        Assert.Equal("repl.clear", Assert.Single(target.Published).Type);
    }

    [Fact(Timeout = 30_000)]
    public async Task DetachingTheScopeRejectsWaitersAndDeactivatesTheSink()
    {
        var router = new FrontendDenoReplPresentationRouter();
        var target = new FakeTarget();
        var sessionId = AgentSessionId.Create();
        var scope = router.Attach(sessionId, target);
        var sink = scope.Sink;
        var callId = AgentToolCallId.Create();

        router.OpenCall(sessionId, callId);
        var resolved = await router
            .WaitForCallAsync(sessionId, callId, TestContext.Current.CancellationToken);
        Assert.Same(sink, resolved);

        await scope.DisposeAsync();

        // After detach the sink is inert and late waiters get the typed failure.
        var act = async () => await sink.DisplayAsync(
            ReplDisplayBundle.FromText("late"),
            EmptyMetadata(),
            CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await act.Invoke(); });
        var lateCall = async () => await router.WaitForCallAsync(sessionId, callId, CancellationToken.None);
        Assert.Equal("repl_presentation_unavailable", (await Assert.ThrowsAnyAsync<AgentToolException>(async () => { await lateCall.Invoke(); })).Code);
        Assert.Empty(target.Published);
    }

    [Fact(Timeout = 30_000)]
    public async Task InputRequestPublishesAFrameAndCompletesWithTheAnswer()
    {
        var target = new FakeTarget();
        var sink = new FrontendDenoReplPresentationSink(target);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));

        var wait = sink.RequestInputAsync("Name:", password: true, deadline.Token);
        Assert.Single(target.Published);
        var frame = target.Published[0];
        Assert.Equal("input.request", frame.Type);
        Assert.Null(frame.DisplayId);

        var payload = JsonSerializer.Deserialize<JsonElement>(frame.Data.GetRawText());
        var requestId = payload.GetProperty("requestId").GetString()!;
        Assert.Equal("Name:", payload.GetProperty("prompt").GetString());
        Assert.True(payload.GetProperty("password").GetBoolean());

        Assert.True(sink.TryCompleteInput(requestId, "ghost"));
        Assert.Equal("ghost", (await wait));
        // 第二次应答：请求已完成
        Assert.False(sink.TryCompleteInput(requestId, "again"));
    }

    [Fact]
    public void UnknownInputAnswerReturnsFalse()
    {
        var sink = new FrontendDenoReplPresentationSink(new FakeTarget());
        Assert.False(sink.TryCompleteInput("input-404", "x"));
    }

    [Fact(Timeout = 30_000)]
    public async Task RouterCompletesTheOwningSinkAndRequestIdsNeverCollide()
    {
        var router = new FrontendDenoReplPresentationRouter();
        var firstTarget = new FakeTarget();
        var secondTarget = new FakeTarget();
        await using var first = router.Attach(AgentSessionId.Create(), firstTarget);
        await using var second = router.Attach(AgentSessionId.Create(), secondTarget);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));

        var firstWait = first.Sink.RequestInputAsync("first:", false, deadline.Token);
        var secondWait = second.Sink.RequestInputAsync("second:", false, deadline.Token);
        var firstId = RequestIdOf(firstTarget);
        var secondId = RequestIdOf(secondTarget);
        Assert.NotEqual(secondId, firstId);

        Assert.True(router.TryCompleteInput(secondId, "b"));
        Assert.Equal("b", (await secondWait));
        Assert.True(router.TryCompleteInput(firstId, "a"));
        Assert.Equal("a", (await firstWait));
        // Request ids are unique across sinks, so a delivered answer cannot be
        // re-delivered anywhere.
        Assert.False(router.TryCompleteInput(secondId, "again"));
    }

    [Fact]
    public void RouterInputAnswerWithoutAttachedSinksReturnsFalse()
    {
        Assert.False(new FrontendDenoReplPresentationRouter()
            .TryCompleteInput($"input-{Guid.NewGuid():N}-1", "x"));
    }

    private static string RequestIdOf(FakeTarget target)
    {
        return target.Published
            .Single(entry => entry.Type == "input.request")
            .Data.GetProperty("requestId")
            .GetString()!;
    }

    [Fact(Timeout = 30_000)]
    public async Task InputRequestHonoursCallerCancellation()
    {
        var sink = new FrontendDenoReplPresentationSink(new FakeTarget());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var wait = sink.RequestInputAsync("prompt", false, cancellation.Token);
        cancellation.Cancel();
        var act = async () => await wait;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await act.Invoke(); });
    }

    private static IReadOnlyDictionary<string, JsonElement> EmptyMetadata()
    {
        return new Dictionary<string, JsonElement>();
    }
}
