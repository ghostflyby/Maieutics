using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Frontend;
using Maieutics.Mcp;

namespace Maieutics.Product.Tests;

public sealed class FrontendElicitationPresenterTests
{
    private static FrontendElicitationPresenter CreatePresenter(RecordingFramePublisher publisher) =>
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

    [Fact(Timeout = 30_000)]
    public async Task PresentPublishesFrameAndAcceptCompletesWithObjectContent()
    {
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(10));
        var session = AgentSessionId.Create();

        var wait = presenter.PresentAsync(
            new McpElicitationRequest(session.Value.ToString("N"), session.Value.ToString("N"), "msg", null, false),
            deadline.Token);

        // The frame is published synchronously before the await, and this request carries
        // no schema, so only the input.request frame is published. The signal makes the
        // observation deterministic without a settle window.
        await publisher.WaitForCountAsync(1, deadline.Token);
        var (publishedSession, type, data) = Assert.Single(publisher.Published);
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

    [Fact(Timeout = 30_000)]
    public async Task DeclineAnswersWithoutContentAndSecondCompletionFails()
    {
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(10));

        var wait = presenter.PresentAsync(BuildRequest(), deadline.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);
        var requestId = JsonSerializer
            .Deserialize<JsonElement>(publisher.Single("input.request").Data.GetRawText())
            .GetProperty("requestId").GetString()!;

        Assert.True(presenter.TryCompleteInput(requestId, "decline", ""));

        var answer = await wait;
        Assert.Equal("decline", answer.Action);
        Assert.Null(answer.ContentJson);
        Assert.False(presenter.TryCompleteInput(requestId, "accept", "{}"));
    }

    [Fact(Timeout = 30_000)]
    public async Task NonObjectAcceptValueIsDowngradedToCancel()
    {
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(10));

        var wait = presenter.PresentAsync(BuildRequest(), deadline.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);
        var requestId = JsonSerializer
            .Deserialize<JsonElement>(publisher.Single("input.request").Data.GetRawText())
            .GetProperty("requestId").GetString()!;

        Assert.True(presenter.TryCompleteInput(requestId, null, "just text"));

        var answer = await wait;
        Assert.Equal("cancel", answer.Action);
        Assert.Null(answer.ContentJson);
    }

    [Fact(Timeout = 30_000)]
    public async Task UnpublishableSessionAnswersCancelImmediately()
    {
        var presenter = CreatePresenter(new RecordingFramePublisher(publishable: false));

        var answer = await presenter.PresentAsync(BuildRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("cancel", answer.Action);
    }
}
