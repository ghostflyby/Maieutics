using System.Text.Json;
using Maieutics.Agent;
using Maieutics.DenoRepl;
using System.Net.Http.Json;
using Maieutics.Frontend;
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

    [Fact(Timeout = 30_000)]
    public async Task KernelFormPublishesCommOpenAndDisplayAnnouncement()
    {
        var (router, _) = CreateRouter();
        var publisher = new RecordingFramePublisher();
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

        Assert.NotNull(model);
        var live = router.PlaneFor(session.ToString()).SnapshotLive();
        Assert.Equal("maieutics.view/maieutics/form", Assert.Single(live).TargetName);

        var display = Assert.Single(publisher.Published);
        Assert.Equal("repl.display", display.Type);
        var announcement = display.Data.GetProperty("application/vnd.maieutics.view+json");
        Assert.Equal(model.CommId, announcement.GetProperty("modelId").GetString());
        Assert.Equal("maieutics/form", announcement.GetProperty("viewFamily").GetString());
        Assert.Equal("1.0", announcement.GetProperty("version").GetString());
        Assert.Equal("Approve deployment?", announcement.GetProperty("state").GetProperty("title").GetString());
        Assert.Equal("Approve deployment?", display.Data.GetProperty("text/plain").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task KernelFormUplinkRoutesToTheOwnerAndNeverToTheChild()
    {
        var (router, childPushes) = CreateRouter();
        var publisher = new RecordingFramePublisher();
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
        Assert.NotNull(model);

        await router.PushToReplAsync(
            session.ToString(),
            Uplink(
                model.CommId,
                new { method = "event", name = "submit", payload = new { values = new { note = "ship it" } } }),
            TestContext.Current.CancellationToken);

        Assert.Empty(childPushes);
        var values = await submitted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ship it", values["note"]);
    }

    [Fact(Timeout = 30_000)]
    public async Task KernelFormCancelDeclinesAndFrontendCloseReleasesTheComm()
    {
        var (router, _) = CreateRouter();
        var publisher = new RecordingFramePublisher();
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
        Assert.Empty(router.PlaneFor(session.ToString()).SnapshotLive());
    }

    [Fact(Timeout = 30_000)]
    public async Task MalformedKernelUplinkIsIgnoredWithoutBreakingThePlane()
    {
        var (router, _) = CreateRouter();
        var publisher = new RecordingFramePublisher();
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
        await act.Invoke();
        Assert.Single(router.PlaneFor(session.ToString()).SnapshotLive());
    }

    [Fact(Timeout = 30_000)]
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

        Assert.Equal("w1", Assert.Single(childPushes).CommId);
    }

    [Fact(Timeout = 30_000)]
    public async Task FormWithoutALiveStreamIsClosedAndReportsNoModel()
    {
        var (router, _) = CreateRouter();
        var publisher = new RecordingFramePublisher(publishable: false);
        var host = new FrontendFormModelHost(router, publisher);
        var session = AgentSessionId.Create();

        var model = await host.TryCreateFormAsync(
            session,
            new FrontendUiFormState(),
            new FrontendFormHandlers(),
            TestContext.Current.CancellationToken);

        Assert.Null(model);
        Assert.Empty(router.PlaneFor(session.ToString()).SnapshotLive());
    }

    [Fact(Timeout = 60_000)]
    public async Task CapabilitiesAdvertiseTheUiFamilies()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = TestContext.Current.CancellationToken;
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var capabilities = await harness.Client.GetFromJsonAsync<JsonElement>(
            "/v1/agent/capabilities",
            token);
        Assert.Equal(1, capabilities.GetProperty("ui").GetProperty("version").GetInt32());
        Assert.Contains("maieutics/form", capabilities.GetProperty("ui").GetProperty("families")
            .EnumerateArray().Select(family => family.GetString()));
    }

    [Fact(Timeout = 30_000)]
    public async Task ElicitationWithSchemaPublishesTheFormAnnouncement()
    {
        var (router, childPushes) = CreateRouter();
        var publisher = new RecordingFramePublisher();
        var presenter = new FrontendElicitationPresenter(
            publisher,
            new FrontendFormModelHost(router, publisher));
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(10));
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

        // Both presentations ride the run stream: the fallback input.request frame is
        // synchronous, and the native form announcement lands after the form model's
        // router publish. The publication signal replaces a settle window.
        await publisher.WaitForCountAsync(2, deadline.Token);
        Assert.Equivalent(new[] { "input.request", "repl.display" }, publisher.Published.Select(frame => frame.Type));
        var announcement = publisher.Published
            .Single(frame => frame.Type == "repl.display")
            .Data.GetProperty("application/vnd.maieutics.view+json");
        Assert.Equal("maieutics/form", announcement.GetProperty("viewFamily").GetString());
        var fields = announcement.GetProperty("state").GetProperty("fields");
        Assert.Equal(3, fields.GetArrayLength());
        Assert.Equal("token", fields[0].GetProperty("name").GetString());
        Assert.Equal("text", fields[0].GetProperty("type").GetString());
        Assert.True(fields[0].GetProperty("required").GetBoolean());
        Assert.Equal("choice", fields[2].GetProperty("type").GetString());

        // Submitting through the comm plane answers the elicitation directly.
        var modelId = announcement.GetProperty("modelId").GetString()!;
        await router.PushToReplAsync(
            session.ToString(),
            Uplink(
                modelId,
                new { method = "event", name = "submit", payload = new { values = new { token = "t0k" } } }),
            deadline.Token);

        var answer = await wait;
        Assert.Equal("accept", answer.Action);
        Assert.Contains("t0k", answer.ContentJson);
        Assert.Empty(childPushes);
        Assert.Empty(router.PlaneFor(session.ToString()).SnapshotLive());
    }

    [Fact(Timeout = 30_000)]
    public async Task KernelFormOpenCarriesTheStatePayloadAndDoubleDisposeIsIdempotent()
    {
        var (router, _) = CreateRouter();
        var publisher = new RecordingFramePublisher();
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
        var open = Assert.Single(plane.Subscribe(0).Initial);
        Assert.Equal("maieutics.view/maieutics/form", open.Message.TargetName);
        Assert.NotNull(open.Message.Data);
        Assert.Equal("T", open.Message.Data!.Value.GetProperty("state").GetProperty("title").GetString());

        // Frontend close uplink and the owner's own dispose race in production;
        // the closed handler fires exactly once across both paths.
        await router.PushToReplAsync(
            session.ToString(),
            Uplink(model!.CommId, null, ReplCommKind.Close),
            TestContext.Current.CancellationToken);
        await model.DisposeAsync();
        Assert.Equal(1, closedCount);
        Assert.Empty(plane.SnapshotLive());
    }
}
