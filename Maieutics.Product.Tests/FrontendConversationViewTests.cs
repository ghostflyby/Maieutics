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
        var content = fixture.RootElement.GetProperty("content");
        var body = content.Deserialize(FrontendJsonContext.Default.FrontendViewContent);
        Assert.NotNull(body);
        Assert.True(JsonElement.DeepEquals(content, JsonSerializer.SerializeToElement(body, FrontendJsonContext.Default.FrontendViewContent)));
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
}
