using System.Text.Json;
using FluentAssertions;
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
        var presenter = new FrontendElicitationPresenter(publisher);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.Create();

        var wait = presenter.PresentAsync(
            new McpElicitationRequest(session.Value.ToString("N"), session.Value.ToString("N"), "msg", null, false),
            deadline.Token);

        // The frame is published synchronously before the await.
        await Task.Delay(50, deadline.Token);
        publisher.Published.Should().ContainSingle();
        var (publishedSession, type, data) = publisher.Published.Single();
        publishedSession.Value.Should().Be(session.Value);
        type.Should().Be("input.request");
        var payload = JsonSerializer.Deserialize<JsonElement>(data.GetRawText());
        payload.GetProperty("prompt").GetString().Should().Be("msg");
        payload.GetProperty("serverId").GetString().Should().Be(session.Value.ToString("N"));

        presenter.TryCompleteInput(
            payload.GetProperty("requestId").GetString()!,
            null,
            """{"token":"t0k"}""").Should().BeTrue();

        var answer = await wait;
        answer.Action.Should().Be("accept");
        answer.ContentJson.Should().Be("""{"token":"t0k"}""");
    }

    [Fact]
    public async Task DeclineAnswersWithoutContentAndSecondCompletionFails()
    {
        var publisher = new FakePublisher();
        var presenter = new FrontendElicitationPresenter(publisher);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = presenter.PresentAsync(BuildRequest(), deadline.Token);
        await Task.Delay(50, deadline.Token);
        var requestId = JsonSerializer
            .Deserialize<JsonElement>(publisher.Published.Single().Data.GetRawText())
            .GetProperty("requestId").GetString()!;

        presenter.TryCompleteInput(requestId, "decline", "").Should().BeTrue();

        var answer = await wait;
        answer.Action.Should().Be("decline");
        answer.ContentJson.Should().BeNull();
        presenter.TryCompleteInput(requestId, "accept", "{}").Should().BeFalse();
    }

    [Fact]
    public async Task NonObjectAcceptValueIsDowngradedToCancel()
    {
        var publisher = new FakePublisher();
        var presenter = new FrontendElicitationPresenter(publisher);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = presenter.PresentAsync(BuildRequest(), deadline.Token);
        await Task.Delay(50, deadline.Token);
        var requestId = JsonSerializer
            .Deserialize<JsonElement>(publisher.Published.Single().Data.GetRawText())
            .GetProperty("requestId").GetString()!;

        presenter.TryCompleteInput(requestId, null, "just text").Should().BeTrue();

        var answer = await wait;
        answer.Action.Should().Be("cancel");
        answer.ContentJson.Should().BeNull();
    }

    [Fact]
    public async Task UnpublishableSessionAnswersCancelImmediately()
    {
        var presenter = new FrontendElicitationPresenter(new FakePublisher(publishable: false));

        var answer = await presenter.PresentAsync(BuildRequest(), TestContext.Current.CancellationToken);

        answer.Action.Should().Be("cancel");
    }
}
