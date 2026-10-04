using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.DenoRepl;
using System.Net.Http.Json;
using Maieutics.Frontend;
using Maieutics.Providers.OpenAI;
using Maieutics.Mcp;

namespace Maieutics.Product.Tests;

/// <summary>
///     Kernel-owned UI models (ADR 0038 stage 2): the comm ownership routing on the
///     router (kernel uplink never reaches the child; REPL comms are unaffected), the
///     native form dialect bytes, the display-mime announcement, and the elicitation
///     form consumer.
/// </summary>
public sealed class FrontendUiModelTests
{
    private static ReplCommMessage Uplink(string commId, object? data, ReplCommKind kind = ReplCommKind.Message) =>
        new(
            kind,
            commId,
            null,
            data is null ? null : JsonSerializer.SerializeToElement(data),
            null,
            []);

    private static (FrontendCommRouter Router, List<ReplCommMessage> ChildPushes) CreateRouter()
    {
        var pushes = new List<ReplCommMessage>();
        var router = new FrontendCommRouter((_, message, _) =>
        {
            pushes.Add(message);
            return ValueTask.CompletedTask;
        });
        return (router, pushes);
    }

    [Fact]
    public async Task KernelFormPublishesCommOpenAndDisplayAnnouncement()
    {
        var (router, _) = CreateRouter();
        var publisher = new FakePublisher();
        var host = new FrontendFormModelHost(router, publisher);
        var session = AgentSessionId.Create();

        var model = await host.TryCreateFormAsync(
            session,
            new FrontendUiFormState(
                Title: "Approve deployment?",
                Fields: [new FrontendUiFormField("note", Type: "text")],
                SubmitLabel: "Go"),
            new FrontendFormHandlers(),
            TestContext.Current.CancellationToken);

        model.Should().NotBeNull();
        var live = router.PlaneFor(session.ToString()).SnapshotLive();
        live.Should().ContainSingle()
            .Which.TargetName.Should().Be("maieutics.view/maieutics.form");

        var display = publisher.Published.Should().ContainSingle().Which;
        display.Type.Should().Be("repl.display");
        var announcement = display.Data.GetProperty("application/vnd.maieutics.view+json");
        announcement.GetProperty("modelId").GetString().Should().Be(model!.CommId);
        announcement.GetProperty("viewFamily").GetString().Should().Be("maieutics/form");
        announcement.GetProperty("version").GetString().Should().Be("1.0");
        announcement.GetProperty("state").GetProperty("title").GetString().Should().Be("Approve deployment?");
        display.Data.GetProperty("text/plain").GetString().Should().Be("Approve deployment?");
    }

    [Fact]
    public async Task KernelFormUplinkRoutesToTheOwnerAndNeverToTheChild()
    {
        var (router, childPushes) = CreateRouter();
        var publisher = new FakePublisher();
        var host = new FrontendFormModelHost(router, publisher);
        var session = AgentSessionId.Create();
        var submitted = new TaskCompletionSource<Dictionary<string, object?>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var model = await host.TryCreateFormAsync(
            session,
            new FrontendUiFormState(Fields: [new FrontendUiFormField("note", Type: "text")]),
            new FrontendFormHandlers(
                OnSubmit: (values, _) =>
                {
                    submitted.TrySetResult(values);
                    return ValueTask.CompletedTask;
                }),
            TestContext.Current.CancellationToken);
        model.Should().NotBeNull();

        await router.PushToReplAsync(
            session.ToString(),
            Uplink(
                model!.CommId,
                new { method = "event", name = "submit", payload = new { values = new { note = "ship it" } } }),
            TestContext.Current.CancellationToken);

        childPushes.Should().BeEmpty("kernel-owned uplink never travels to the child");
        var values = await submitted.Task.WaitAsync(TestContext.Current.CancellationToken);
        values["note"].Should().Be("ship it");
    }

    [Fact]
    public async Task KernelFormCancelDeclinesAndFrontendCloseReleasesTheComm()
    {
        var (router, _) = CreateRouter();
        var publisher = new FakePublisher();
        var host = new FrontendFormModelHost(router, publisher);
        var session = AgentSessionId.Create();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = await host.TryCreateFormAsync(
            session,
            new FrontendUiFormState(),
            new FrontendFormHandlers(OnCancel: _ =>
            {
                cancelled.TrySetResult();
                return ValueTask.CompletedTask;
            }),
            TestContext.Current.CancellationToken);

        await router.PushToReplAsync(
            session.ToString(),
            Uplink(model!.CommId, new { method = "event", name = "cancel" }),
            TestContext.Current.CancellationToken);
        await cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);

        // A frontend-initiated close releases the registry entry and disposes the model.
        await router.PushToReplAsync(
            session.ToString(),
            Uplink(model.CommId, null, ReplCommKind.Close),
            TestContext.Current.CancellationToken);
        router.PlaneFor(session.ToString()).SnapshotLive().Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedKernelUplinkIsIgnoredWithoutBreakingThePlane()
    {
        var (router, _) = CreateRouter();
        var publisher = new FakePublisher();
        var host = new FrontendFormModelHost(router, publisher);
        var session = AgentSessionId.Create();
        var model = await host.TryCreateFormAsync(
            session,
            new FrontendUiFormState(),
            new FrontendFormHandlers(),
            TestContext.Current.CancellationToken);

        var act = async () =>
        {
            await router.PushToReplAsync(
                session.ToString(),
                Uplink(model!.CommId, new { method = "nonsense" }),
                TestContext.Current.CancellationToken);
            await router.PushToReplAsync(
                session.ToString(),
                Uplink(model.CommId, "not-an-object"),
                TestContext.Current.CancellationToken);
        };
        await act.Should().NotThrowAsync();
        router.PlaneFor(session.ToString()).SnapshotLive().Should().HaveCount(1);
    }

    [Fact]
    public async Task ReplCommsStillRouteUplinkToTheChild()
    {
        var (router, childPushes) = CreateRouter();
        var session = AgentSessionId.Create();
        await router.AcceptFromReplAsync(
            session.ToString(),
            new ReplCommMessage(ReplCommKind.Open, "w1", "jupyter.widget", null, null, []),
            TestContext.Current.CancellationToken);

        await router.PushToReplAsync(
            session.ToString(),
            new ReplCommMessage(
                ReplCommKind.Message,
                "w1",
                null,
                JsonSerializer.SerializeToElement(new { method = "update", state = new { value = 3 } }),
                null,
                []),
            TestContext.Current.CancellationToken);

        childPushes.Should().ContainSingle().Which.CommId.Should().Be("w1");
    }

    [Fact]
    public async Task FormWithoutALiveStreamIsClosedAndReportsNoModel()
    {
        var (router, _) = CreateRouter();
        var publisher = new FakePublisher(publishable: false);
        var host = new FrontendFormModelHost(router, publisher);
        var session = AgentSessionId.Create();

        var model = await host.TryCreateFormAsync(
            session,
            new FrontendUiFormState(),
            new FrontendFormHandlers(),
            TestContext.Current.CancellationToken);

        model.Should().BeNull();
        router.PlaneFor(session.ToString()).SnapshotLive().Should().BeEmpty("the opened comm is closed again");
    }

    [Fact(Timeout = 60_000)]
    public async Task CapabilitiesAdvertiseTheUiFamilies()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = TestContext.Current.CancellationToken;
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);
        var capabilities = await harness.Client.GetFromJsonAsync<JsonElement>(
            "/v1/agent/capabilities",
            token);
        capabilities.GetProperty("ui").GetProperty("version").GetInt32().Should().Be(1);
        capabilities.GetProperty("ui").GetProperty("families")
            .EnumerateArray().Select(family => family.GetString())
            .Should().Contain("maieutics/form");
    }

    [Fact]
    public async Task ElicitationWithSchemaPublishesTheFormAnnouncement()
    {
        var (router, childPushes) = CreateRouter();
        var publisher = new FakePublisher();
        var presenter = new FrontendElicitationPresenter(
            publisher,
            new FrontendFormModelHost(router, publisher));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.Create();
        using var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"required\":[\"token\"],\"properties\":{\"token\":{\"type\":\"string\",\"title\":\"API token\"},\"level\":{\"type\":\"integer\"},\"mode\":{\"enum\":[\"fast\",\"slow\"]}}}");

        var wait = presenter.PresentAsync(
            new McpElicitationRequest(
                "srv",
                session.Value.ToString("N"),
                "Provide the token",
                schema.RootElement.Clone(),
                Password: false),
            deadline.Token);
        await Task.Delay(100, deadline.Token);

        // Both presentations ride the run stream: the fallback input.request frame
        // and the native form announcement (ADR 0038 stage 2).
        publisher.Published.Select(frame => frame.Type).Should().BeEquivalentTo(["input.request", "repl.display"]);
        var announcement = publisher.Published
            .Single(frame => frame.Type == "repl.display")
            .Data.GetProperty("application/vnd.maieutics.view+json");
        announcement.GetProperty("viewFamily").GetString().Should().Be("maieutics/form");
        var fields = announcement.GetProperty("state").GetProperty("fields");
        fields.GetArrayLength().Should().Be(3);
        fields[0].GetProperty("name").GetString().Should().Be("token");
        fields[0].GetProperty("type").GetString().Should().Be("text");
        fields[0].GetProperty("required").GetBoolean().Should().BeTrue();
        fields[2].GetProperty("type").GetString().Should().Be("choice");

        // Submitting through the comm plane answers the elicitation directly.
        var modelId = announcement.GetProperty("modelId").GetString()!;
        await router.PushToReplAsync(
            session.ToString(),
            Uplink(
                modelId,
                new { method = "event", name = "submit", payload = new { values = new { token = "t0k" } } }),
            deadline.Token);

        var answer = await wait;
        answer.Action.Should().Be("accept");
        answer.ContentJson.Should().Contain("t0k");
        childPushes.Should().BeEmpty("the kernel-owned form never reaches the child");
        router.PlaneFor(session.ToString()).SnapshotLive().Should().BeEmpty(
            "answering the elicitation closes the form's comm");
    }

    [Fact]
    public async Task KernelFormOpenCarriesTheStatePayloadAndDoubleDisposeIsIdempotent()
    {
        var (router, _) = CreateRouter();
        var publisher = new FakePublisher();
        var host = new FrontendFormModelHost(router, publisher);
        var session = AgentSessionId.Create();
        var closedCount = 0;
        var model = await host.TryCreateFormAsync(
            session,
            new FrontendUiFormState(Title: "T", Fields: [new FrontendUiFormField("note", Type: "text")]),
            new FrontendFormHandlers(Closed: () => Interlocked.Increment(ref closedCount)),
            TestContext.Current.CancellationToken);

        // The comm_open's replayed data carries the native state payload.
        var plane = router.PlaneFor(session.ToString());
        var open = plane.Subscribe(0).Initial.Should().ContainSingle().Which;
        open.Message.TargetName.Should().Be("maieutics.view/maieutics.form");
        open.Message.Data.Should().NotBeNull();
        open.Message.Data!.Value.GetProperty("state").GetProperty("title").GetString().Should().Be("T");

        // Frontend close uplink and the owner's own dispose race in production;
        // the closed handler fires exactly once across both paths.
        await router.PushToReplAsync(
            session.ToString(),
            Uplink(model!.CommId, null, ReplCommKind.Close),
            TestContext.Current.CancellationToken);
        await model.DisposeAsync();
        closedCount.Should().Be(1);
        plane.SnapshotLive().Should().BeEmpty();
    }

    private sealed class FakePublisher(bool publishable = true) : IFrontendSessionFramePublisher
    {
        internal List<(AgentSessionId SessionId, string Type, JsonElement Data)> Published { get; } = [];

        public bool TryPublishPresentation(AgentSessionId sessionId, string type, JsonElement data)
        {
            if (!publishable) return false;
            Published.Add((sessionId, type, data.Clone()));
            return true;
        }
    }
}
