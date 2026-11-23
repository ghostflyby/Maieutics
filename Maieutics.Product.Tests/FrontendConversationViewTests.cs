using Maieutics.Frontend;
using System.Text.Json;

namespace Maieutics.Product.Tests;

public sealed class FrontendConversationViewTests
{
    [Fact]
    public void VersionOneFixturesRoundTripWithNativeAotMetadataAndTolerateUnknownFields()
    {
        using var stream = typeof(FrontendConversationViewTests).Assembly.GetManifestResourceStream(
            "Maieutics.Product.Tests.Fixtures.conversation-view-v1.json") ?? throw new InvalidOperationException("Missing view wire fixture");
        using var fixture = JsonDocument.Parse(stream);
        foreach (var name in new[] { "snapshot", "change" })
        {
            var expected = fixture.RootElement.GetProperty(name);
            var extended = expected.GetRawText().Replace("\"type\":", "\"futureField\":true,\"type\":", StringComparison.Ordinal);
            var frame = JsonSerializer.Deserialize(extended, FrontendJsonContext.Default.FrontendViewFrame);
            Assert.NotNull(frame);
            var actual = JsonSerializer.SerializeToElement(frame, FrontendJsonContext.Default.FrontendViewFrame);
            Assert.True(JsonElement.DeepEquals(expected, actual), $"The {name} v1 wire fixture changed.");
        }
        foreach (var name in new[] { "content", "richContent" })
        {
            var content = fixture.RootElement.GetProperty(name);
            var body = content.Deserialize(FrontendJsonContext.Default.FrontendViewContent);
            Assert.NotNull(body);
            Assert.True(JsonElement.DeepEquals(content, JsonSerializer.SerializeToElement(body, FrontendJsonContext.Default.FrontendViewContent)));
        }
    }
    [Fact]
    public void PagesContainOnlyTheRequestedIndexWindowAndBodiesAreSeparate()
    {
        var view = new FrontendConversationView("session");
        view.Seed(new FrontendTranscript("session", 1,
            [new FrontendTranscriptTurn("stored", false, null, Enumerable.Range(0, 200)
                .Select(index => new FrontendMessage("user", [new FrontendMessagePart("text", $"body {index}")])).ToArray())]));
        var tail = view.Page(null, 80, null);
        Assert.Equal(200, tail.Total);
        Assert.Equal(80, tail.Entries.Length);
        Assert.Equal(120, tail.Entries[0].Ordinal);
        var page = view.Page(40, 3, tail.Epoch);
        Assert.Equal([40, 41, 42], page.Entries.Select(entry => entry.Ordinal));
        var entry = page.Entries[1];
        Assert.Equal("body 41", view.Content(entry.Id, entry.ContentRevision, tail.Epoch).Text);
        Assert.Equal(tail.Revision, page.Revision);
    }

    [Fact(Timeout = 10_000)]
    public async Task SnapshotSubscriptionFenceAndPageWatermarksRemainContiguous()
    {
        var view = new FrontendConversationView("session");
        view.Seed(new FrontendTranscript("session", 1,
            [new FrontendTranscriptTurn("stored", false, null, [new FrontendMessage("user", [new FrontendMessagePart("text", "old")])])]));
        var (snapshot, channel) = view.Subscribe();
        try
        {
            var page = Assert.IsType<FrontendViewPage>(snapshot.Page);
            view.Start("run", "question");
            var change = Assert.IsType<FrontendViewChange>((await channel.Reader.ReadAsync(TestContext.Current.CancellationToken)).Change);
            Assert.Equal(page.Revision, change.BaseRevision);
            Assert.Equal(2, change.Total);
            Assert.Equal("question", Assert.Single(change.Operations).Entry.Preview);
            var advanced = view.Page(0, 1, page.Epoch);
            Assert.Equal(change.Revision, advanced.Revision);
            view.Publish(new FrontendEventFrame("text.delta", RunId: "run", MessageId: "answer", Text: "first"));
            var next = Assert.IsType<FrontendViewChange>((await channel.Reader.ReadAsync(TestContext.Current.CancellationToken)).Change);
            Assert.Equal(advanced.Revision, next.BaseRevision);
        }
        finally { view.Unsubscribe(channel); }
    }

    [Fact]
    public void ContentNeverSubstitutesADifferentRevisionAndFailureSealsOpenBlocks()
    {
        var view = new FrontendConversationView("session");
        view.Start("run", "question");
        view.Publish(new FrontendEventFrame("text.delta", RunId: "run", MessageId: "answer", Text: "first"));
        var page = view.Page(null, 80, null);
        var entry = page.Entries[^1];
        view.Publish(new FrontendEventFrame("text.delta", RunId: "run", MessageId: "answer", Text: " second"));
        Assert.Equal("content_revision_gap", Assert.Throws<FrontendFailureException>(() => view.Content(entry.Id, entry.ContentRevision, page.Epoch)).Code);
        var current = view.Page(null, 80, page.Epoch).Entries[^1];
        Assert.Equal("first second", view.Content(current.Id, current.ContentRevision, page.Epoch).Text);
        view.Publish(new FrontendEventFrame("run.failed", RunId: "run", Code: "cancelled", Message: "cancelled"));
        Assert.All(view.Page(null, 80, page.Epoch).Entries, row => Assert.Equal("sealed", row.State));
        Assert.Equal("epoch_changed", Assert.Throws<FrontendFailureException>(() => view.Page(0, 1, "other")).Code);
        Assert.Equal("invalid_request", Assert.Throws<FrontendFailureException>(() => view.Page(-1, 1, page.Epoch)).Code);
    }

    [Theory]
    [InlineData("run.completed", null)]
    [InlineData("run.failed", "cancelled")]
    [InlineData("run.failed", "agent_error")]
    public void ChildTerminalSealsItsOpenRowsWithoutEndingTheParent(string terminal, string? code)
    {
        var view = new FrontendConversationView("session");
        view.Start("parent", "question");
        view.Publish(new FrontendEventFrame("text.delta", RunId: "parent", MessageId: "parent-answer", Text: "parent"));
        view.Publish(new FrontendEventFrame("text.delta", RunId: "child", MessageId: "answer", Text: "partial child"));
        view.Publish(new FrontendEventFrame("reasoning.delta", RunId: "child", MessageId: "summary", Text: "summary"));
        view.Publish(new FrontendEventFrame("tool.started", RunId: "child", CallId: "tool", Tool: "read"));
        var (_, channel) = view.Subscribe();
        try
        {
            view.Publish(new FrontendEventFrame(terminal, RunId: "child", Code: code, Message: "child stopped"));
            Assert.True(channel.Reader.TryRead(out var frame));
            Assert.Equal("parent", frame.ActiveRunId);
            var change = Assert.IsType<FrontendViewChange>(frame.Change);
            Assert.Equal(3, change.Operations.Count(operation => operation.Entry.InteractionId == "child" && operation.Entry.Kind != "diagnostic"));
            var rows = view.Page(null, 80, null).Entries;
            Assert.All(rows.Where(row => row.InteractionId == "child"), row => Assert.Equal("sealed", row.State));
            Assert.Equal("open", Assert.Single(rows, row => row.Id == "parent:message:parent-answer").State);
        }
        finally { view.Unsubscribe(channel); }
    }

    [Fact(Timeout = 10_000)]
    public async Task SlowSubscriberFailsExplicitlyWithoutStoppingTheProducer()
    {
        var view = new FrontendConversationView("session");
        var (_, channel) = view.Subscribe();
        view.Start("run", "question");
        for (var index = 0; index < 300; index++)
            view.Publish(new FrontendEventFrame("text.delta", RunId: "run", MessageId: "answer", Text: "x"));
        while (channel.Reader.TryRead(out _)) { }
        var failure = await Assert.ThrowsAsync<FrontendFailureException>(() => channel.Reader.Completion.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("view_backpressure", failure.Code);
        var page = view.Page(null, 80, null);
        var entry = page.Entries[^1];
        Assert.Equal(300, view.Content(entry.Id, entry.ContentRevision, page.Epoch).Text.Length);
        view.Unsubscribe(channel);
    }

    [Fact]
    public void OversizedLiveBodyHasAnExplicitBoundedPreview()
    {
        var view = new FrontendConversationView("session");
        view.Start("run", new string('中', 100_000));
        var page = view.Page(null, 80, null);
        var entry = Assert.Single(page.Entries);
        var body = view.Content(entry.Id, entry.ContentRevision, page.Epoch).Text;
        Assert.Equal(64_000, body.Length);
        Assert.Contains("有界正文预览", body, StringComparison.Ordinal);
        Assert.Equal(240, entry.Preview.Length);
    }
    [Fact]
    public void RichDisplayKeepsStructuredDataAtItsExactRevisionAndUpdatesInPlace()
    {
        var view = new FrontendConversationView("session");
        view.Start("run", "input");
        using var first = JsonDocument.Parse("""{"application/vnd.maieutics.vue+json":{"version":1,"format":"sfc","source":"<template><button>First</button></template>"},"text/plain":"fallback"}""");
        view.Publish(new FrontendEventFrame("repl.display", RunId: "run", DisplayId: "display", Data: first.RootElement));
        var page = view.Page(null, 80, null);
        var entry = page.Entries[^1];
        var content = view.Content(entry.Id, entry.ContentRevision, page.Epoch);
        var presentation = Assert.IsType<FrontendViewPresentation>(content.Presentation);
        Assert.Equal("fallback", content.Text);
        Assert.Equal("mime", presentation.Kind);
        Assert.True(JsonElement.DeepEquals(first.RootElement, presentation.Value ?? default));
        using var second = JsonDocument.Parse("""{"application/vnd.maieutics.form+json":{"version":1,"modelId":"form","state":{"fields":[],"values":{}}}}""");
        view.Publish(new FrontendEventFrame("repl.updateDisplay", RunId: "run", DisplayId: "display", Data: second.RootElement));
        var updated = view.Page(null, 80, null).Entries[^1];
        Assert.Equal(entry.Id, updated.Id);
        Assert.Equal(page.Total, view.Page(null, 80, null).Total);
        Assert.NotEqual(entry.ContentRevision, updated.ContentRevision);
        Assert.Equal("content_revision_gap", Assert.Throws<FrontendFailureException>(() => view.Content(entry.Id, entry.ContentRevision, page.Epoch)).Code);
        Assert.True(JsonElement.DeepEquals(second.RootElement, view.Content(updated.Id, updated.ContentRevision, page.Epoch).Presentation?.Value ?? default));
        view.Publish(new FrontendEventFrame("repl.clear", RunId: "run"));
        var cleared = view.Page(null, 80, null).Entries[^1];
        Assert.Null(view.Content(cleared.Id, cleared.ContentRevision, page.Epoch).Presentation);
    }

    [Fact]
    public void ToolArgumentsAndResultsStayStructuredAndInterruptedToolsStopRunning()
    {
        var view = new FrontendConversationView("session");
        view.Start("run", "input");
        using var arguments = JsonDocument.Parse("""{"path":"workspace://local/a","count":3}""");
        view.Publish(new FrontendEventFrame("tool.started", RunId: "run", CallId: "read", Tool: "read", Arguments: arguments.RootElement));
        view.Publish(new FrontendEventFrame("run.failed", RunId: "run", Code: "cancelled"));
        var page = view.Page(null, 80, null);
        var entry = Assert.Single(page.Entries, row => row.Kind == "tool");
        var content = view.Content(entry.Id, entry.ContentRevision, page.Epoch);
        var tool = content.Presentation?.Value?.Deserialize(FrontendJsonContext.Default.FrontendViewTool);
        Assert.NotNull(tool);
        Assert.Equal("error", tool.Status);
        Assert.True(JsonElement.DeepEquals(arguments.RootElement, tool.Arguments ?? default));
        Assert.Equal("sealed", entry.State);
    }

    [Fact]
    public void OversizedPresentationFallsBackWithoutRetainingPartialExecutableData()
    {
        var view = new FrontendConversationView("session");
        view.Start("run", "input");
        using var display = JsonDocument.Parse("{\"application/json\":\"" + new string('x', 100_000) + "\"}");
        view.Publish(new FrontendEventFrame("repl.display", RunId: "run", DisplayId: "display", Data: display.RootElement));
        var page = view.Page(null, 80, null);
        var entry = page.Entries[^1];
        var content = view.Content(entry.Id, entry.ContentRevision, page.Epoch);
        Assert.Null(content.Presentation);
        Assert.Contains("有界正文预览", content.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "tool.finished")]
    [InlineData(false, "run.completed")]
    [InlineData(false, "run.failed")]
    [InlineData(true, "tool.finished")]
    [InlineData(true, "run.completed")]
    [InlineData(true, "run.failed")]
    public void TruncatedToolTerminationPreservesItsLastKnownPreview(bool sharedCapacity, string terminal)
    {
        var view = new FrontendConversationView("session");
        if (sharedCapacity)
            for (var index = 0; index < 62; index++)
                view.Start($"filler-{index}", new string('x', 64_000));
        view.Start("run", "input");
        using var arguments = JsonDocument.Parse("{\"path\":\"retained-" + new string('x', sharedCapacity ? 20_000 : 100_000) + "\"}");
        view.Publish(new FrontendEventFrame("tool.started", RunId: "run", CallId: "call", Tool: "read_known_file", Arguments: arguments.RootElement));
        var page = view.Page(null, 80, null);
        var entry = Assert.Single(page.Entries, row => row.Kind == "tool");
        var before = view.Content(entry.Id, entry.ContentRevision, page.Epoch);
        Assert.Null(before.Presentation);
        Assert.Contains("read_known_file", before.Text, StringComparison.Ordinal);
        Assert.Contains("retained-", before.Text, StringComparison.Ordinal);
        Assert.Contains("有界正文预览", before.Text, StringComparison.Ordinal);

        using var result = JsonDocument.Parse("""{"status":"ok","value":"finished"}""");
        view.Publish(new FrontendEventFrame(terminal, RunId: "run", CallId: "call", Result: result.RootElement, Code: "cancelled"));
        var sealedEntry = Assert.Single(view.Page(null, 80, null).Entries, row => row.Id == entry.Id);
        Assert.Equal("sealed", sealedEntry.State);
        Assert.Equal(entry.ContentRevision, sealedEntry.ContentRevision);
        Assert.Equal(before, view.Content(sealedEntry.Id, sealedEntry.ContentRevision, page.Epoch));
    }

    [Theory]
    [InlineData("run.completed", false, "assistant")]
    [InlineData("run.completed", true, "markdown")]
    [InlineData("run.failed", false, "markdown")]
    public void AssistantExecutionRequiresSuccessfulUntruncatedRunCompletion(string terminal, bool truncated, string expected)
    {
        var view = new FrontendConversationView("session");
        view.Start("run", "<button>input is always passive</button>");
        view.Publish(new FrontendEventFrame("text.delta", RunId: "run", MessageId: "answer", Text: "<button>reply</button>"));
        view.Publish(new FrontendEventFrame("message.completed", RunId: "run", MessageId: "answer",
            AgentMessage: new FrontendMessage("assistant", [new FrontendMessagePart("text", "<button>reply</button>")])));
        var page = view.Page(null, 80, null);
        var entry = Assert.Single(page.Entries, row => row.Kind == "message");
        Assert.Equal("markdown", view.Content(entry.Id, entry.ContentRevision, page.Epoch).Presentation?.Kind);
        var input = Assert.Single(page.Entries, row => row.Kind == "input");
        Assert.Null(view.Content(input.Id, input.ContentRevision, page.Epoch).Presentation);
        view.Publish(new FrontendEventFrame(terminal, RunId: "run", Truncated: truncated, Code: "cancelled"));
        var completed = Assert.Single(view.Page(null, 80, null).Entries, row => row.Kind == "message");
        Assert.Equal(expected, view.Content(completed.Id, completed.ContentRevision, page.Epoch).Presentation?.Kind);
    }

    [Theory]
    [InlineData(false, "assistant")]
    [InlineData(true, "markdown")]
    public void RestoredAssistantExecutionUsesCanonicalTurnTruncation(bool truncated, string expected)
    {
        var view = new FrontendConversationView("session");
        view.Seed(new FrontendTranscript("session", 1,
            [new FrontendTranscriptTurn("run", truncated, null, [new FrontendMessage("assistant", [new FrontendMessagePart("text", "reply")])])]));
        var page = view.Page(null, 80, null);
        var entry = Assert.Single(page.Entries);
        Assert.Equal(expected, view.Content(entry.Id, entry.ContentRevision, page.Epoch).Presentation?.Kind);
    }

}
