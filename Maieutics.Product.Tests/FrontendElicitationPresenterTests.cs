using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Frontend;
using Maieutics.Mcp;

namespace Maieutics.Product.Tests;

public sealed class FrontendElicitationPresenterTests
{
    private sealed class FakePublisher : IFrontendSessionFramePublisher
    {
        private readonly bool publishable;

        internal FakePublisher(bool publishable = true)
        {
            this.publishable = publishable;
        }

        internal List<(AgentSessionId SessionId, string Type, JsonElement Data)> Published { get; } = [];

        public bool TryPublishPresentation(AgentSessionId sessionId, string type, JsonElement data)
        {
            if (!publishable) return false;
            Published.Add((sessionId, type, data));
            return true;
        }
    }

    private static FrontendElicitationPresenter CreatePresenter(FakePublisher publisher) =>
        new(
            publisher,
            new FrontendFormModelHost(
                new FrontendCommRouter((_, _, _) => ValueTask.CompletedTask),
                publisher));

    private static McpElicitationRequest BuildRequest()
    {
        using var document = JsonDocument.Parse(
            """{"type":"object","properties":{"token":{"type":"string"}}}""");
        return new McpElicitationRequest(
            "srv",
            AgentSessionId.Create().Value.ToString("N"),
            "Provide the token",
            document.RootElement.Clone(),
            Password: false);
    }

    [Fact]
    public async Task PresentPublishesFrameAndAcceptCompletesWithObjectContent()
    {
        var publisher = new FakePublisher();
        var presenter = CreatePresenter(publisher);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.Create();

        var wait = presenter.PresentAsync(
            new McpElicitationRequest(session.Value.ToString("N"), session.Value.ToString("N"), "msg", null, false),
            deadline.Token);

        // The frame is published synchronously before the await.
        await Task.Delay(50, deadline.Token);
        // This request carries no schema, so only the input.request frame is published.
        Assert.Single(publisher.Published);
        var (publishedSession, type, data) = publisher.Published.Single();
        Assert.Equal(session.Value, publishedSession.Value);
        Assert.Equal("input.request", type);
        var payload = JsonSerializer.Deserialize<JsonElement>(data.GetRawText());
        Assert.Equal("msg", payload.GetProperty("prompt").GetString());
        Assert.Equal(session.Value.ToString("N"), payload.GetProperty("serverId").GetString());

        Assert.True(presenter.TryCompleteInput(
            payload.GetProperty("requestId").GetString()!,
            null,
            """{"token":"t0k"}"""));

        var answer = await wait;
        Assert.Equal("accept", answer.Action);
        Assert.Equal("""{"token":"t0k"}""", answer.ContentJson);
    }

    [Fact]
    public async Task DeclineAnswersWithoutContentAndSecondCompletionFails()
    {
        var publisher = new FakePublisher();
        var presenter = CreatePresenter(publisher);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = presenter.PresentAsync(BuildRequest(), deadline.Token);
        await Task.Delay(50, deadline.Token);
        var requestId = JsonSerializer
            .Deserialize<JsonElement>(
                publisher.Published.Single(frame => frame.Type == "input.request").Data.GetRawText())
            .GetProperty("requestId").GetString()!;

        Assert.True(presenter.TryCompleteInput(requestId, "decline", ""));

        var answer = await wait;
        Assert.Equal("decline", answer.Action);
        Assert.Null(answer.ContentJson);
        Assert.False(presenter.TryCompleteInput(requestId, "accept", "{}"));
    }

    [Fact]
    public async Task NonObjectAcceptValueIsDowngradedToCancel()
    {
        var publisher = new FakePublisher();
        var presenter = CreatePresenter(publisher);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = presenter.PresentAsync(BuildRequest(), deadline.Token);
        await Task.Delay(50, deadline.Token);
        var requestId = JsonSerializer
            .Deserialize<JsonElement>(
                publisher.Published.Single(frame => frame.Type == "input.request").Data.GetRawText())
            .GetProperty("requestId").GetString()!;

        Assert.True(presenter.TryCompleteInput(requestId, null, "just text"));

        var answer = await wait;
        Assert.Equal("cancel", answer.Action);
        Assert.Null(answer.ContentJson);
    }

    [Fact]
    public async Task UnpublishableSessionAnswersCancelImmediately()
    {
        var presenter = CreatePresenter(new FakePublisher(publishable: false));

        var answer = await presenter.PresentAsync(BuildRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("cancel", answer.Action);
    }
}
