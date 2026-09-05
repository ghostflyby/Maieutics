using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.DenoRepl;
using Maieutics.Frontend;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

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

    [Fact]
    public async Task TrackedDisplayAndUpdatesCarryAStableDisplayId()
    {
        var target = new FakeTarget();
        var sink = new FrontendDenoReplPresentationSink(target);
        var displayId = ReplDisplayId.Create();

        await sink.DisplayTrackedAsync(
            ReplDisplayBundle.FromMarkdown("# hello"),
            displayId,
            EmptyMetadata(),
            CancellationToken.None);
        await sink.UpdateDisplayAsync(
            displayId,
            ReplDisplayBundle.FromText("updated"),
            EmptyMetadata(),
            CancellationToken.None);

        target.Published.Should().HaveCount(2);
        target.Published[0].Type.Should().Be("repl.display");
        target.Published[1].Type.Should().Be("repl.updateDisplay");
        target.Published.Should().OnlyContain(entry => entry.DisplayId == displayId.Value);
        target.Published[0].Data.GetProperty("text/markdown").GetString().Should().Be("# hello");
        target.Published[1].Data.GetProperty("text/plain").GetString().Should().Be("updated");
    }

    [Fact]
    public async Task UntrackedDisplayPublishesWithoutDisplayIdAndErrorsCarryTheBundle()
    {
        var target = new FakeTarget();
        var sink = new FrontendDenoReplPresentationSink(target);

        await sink.DisplayAsync(ReplDisplayBundle.FromText("hi"), EmptyMetadata(), CancellationToken.None);
        await sink.PublishErrorAsync("Boom", "broken", [], CancellationToken.None);

        target.Published[0].Type.Should().Be("repl.display");
        target.Published[0].DisplayId.Should().BeNull();
        target.Published[1].Type.Should().Be("repl.error");
        target.Published[1].Data.GetProperty("text/plain").GetString().Should().Contain("Boom: broken");
    }

    [Fact]
    public async Task ClearedOutputPublishesAnEmptyClearFrame()
    {
        var target = new FakeTarget();
        var sink = new FrontendDenoReplPresentationSink(target);

        await sink.ClearOutputAsync(wait: false, CancellationToken.None);

        target.Published.Should().ContainSingle()
            .Which.Type.Should().Be("repl.clear");
    }

    [Fact]
    public async Task DetachingTheScopeRejectsWaitersAndDeactivatesTheSink()
    {
        var router = new FrontendDenoReplPresentationRouter();
        var target = new FakeTarget();
        var sessionId = AgentSessionId.Create();
        await using var scope = router.Attach(sessionId, target);
        var sink = scope.Sink;
        var callId = AgentToolCallId.Create();

        router.OpenCall(sessionId, callId);
        var resolved = await router.WaitForCallAsync(sessionId, callId, CancellationToken.None);
        resolved.Should().BeSameAs(sink);

        await scope.DisposeAsync();

        // After detach the sink is inert and late waiters get the typed failure.
        var act = async () => await sink.DisplayAsync(
            ReplDisplayBundle.FromText("late"),
            EmptyMetadata(),
            CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
        var lateCall = async () => await router.WaitForCallAsync(sessionId, callId, CancellationToken.None);
        (await lateCall.Should().ThrowAsync<AgentToolException>()).Which.Code.Should()
            .Be("repl_presentation_unavailable");
        target.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task InputRequestPublishesAFrameAndCompletesWithTheAnswer()
    {
        var target = new FakeTarget();
        var sink = new FrontendDenoReplPresentationSink(target);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));

        var wait = sink.RequestInputAsync("Name:", password: true, deadline.Token);
        target.Published.Should().ContainSingle();
        var frame = target.Published[0];
        frame.Type.Should().Be("input.request");
        frame.DisplayId.Should().BeNull();

        var payload = JsonSerializer.Deserialize<JsonElement>(frame.Data.GetRawText());
        var requestId = payload.GetProperty("requestId").GetString()!;
        payload.GetProperty("prompt").GetString().Should().Be("Name:");
        payload.GetProperty("password").GetBoolean().Should().BeTrue();

        sink.TryCompleteInput(requestId, "ghost").Should().BeTrue();
        (await wait).Should().Be("ghost");
        // 第二次应答：请求已完成
        sink.TryCompleteInput(requestId, "again").Should().BeFalse();
    }

    [Fact]
    public async Task UnknownInputAnswerReturnsFalse()
    {
        var sink = new FrontendDenoReplPresentationSink(new FakeTarget());
        sink.TryCompleteInput("input-404", "x").Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task InputRequestHonoursCallerCancellation()
    {
        var sink = new FrontendDenoReplPresentationSink(new FakeTarget());
        using var cancellation = new CancellationTokenSource();
        var wait = sink.RequestInputAsync("prompt", false, cancellation.Token);
        cancellation.Cancel();
        var act = async () => await wait;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static IReadOnlyDictionary<string, JsonElement> EmptyMetadata()
    {
        return new Dictionary<string, JsonElement>();
    }
}
