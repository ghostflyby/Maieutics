using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.Frontend;
using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

/// <summary>
///     The plugin UI lane (ADR 0038 stage 3): the <c>ui.models</c> capability dispatch in
///     the plugin host manager (catalog → grant → frame validation → sink), and the
///     composition-root publication of a plugin frame into the session's comm plane with
///     the plugin as the uplink owner.
/// </summary>
public sealed class PluginUiCapabilityTests
{
    [Fact]
    public void UiDataEntryInterpretsIntoATemplateAndFingerprintCoversIt()
    {
        // The interpreted template rides the descriptor (and thus the ADR 0037
        // fingerprint's generic `data` section) with zero fingerprint-code changes.
        var directory = Path.Combine(Path.GetTempPath(), "maieutics-ui-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Data entries take a string path: the kernel collects the referenced
            // file as raw JSON (ADR 0033's collection shape).
            File.WriteAllText(Path.Combine(directory, "ui-form.json"),
                """{"title":"Approve deploy?","fields":[{"name":"note","type":"text","label":"Note","required":true},{"name":"level","type":"choice","choices":["fast","slow"]}],"submitLabel":"Go","submitTool":"deploy"}""");
            File.WriteAllText(Path.Combine(directory, "maieutics.json"),
                """{"entrypoints":{"ui":"ui-form.json"}}""");
            File.WriteAllText(Path.Combine(directory, "deno.json"),
                """{"name":"probe-ui","version":"1.0.0","exports":"./main.ts"}""");

            var loaded = PluginManifest.TryLoad(directory, out var descriptor, out var loadError);
            Assert.True(loaded, loadError);
            Assert.NotNull(descriptor!.UiForm);
            Assert.Equal("Approve deploy?", descriptor.UiForm!.Title);
            Assert.Null(descriptor.UiForm.Error);
            Assert.Equal(2, descriptor.UiForm.Fields.Count());
            Assert.True(descriptor.UiForm.Fields[0].Required);
            Assert.Equivalent(new[] { new { Value = "fast", Label = "fast" }, new { Value = "slow", Label = "slow" } }, descriptor.UiForm.Fields[1].Choices);
            Assert.Equal("deploy", descriptor.UiForm.SubmitTool);

            // A changed ui entry changes the fingerprint's data section inputs.
            File.WriteAllText(Path.Combine(directory, "ui-form.json"),
                """{"title":"Changed","fields":[{"name":"note","type":"text"}]}""");
            var reloaded = PluginManifest.TryLoad(directory, out var reloadedDescriptor, out var reloadError);
            Assert.True(reloaded, reloadError);
            Assert.Equal("Changed", reloadedDescriptor!.UiForm!.Title);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task PublishUiFormRejectsPluginsWithoutAUsableEntry()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(
            TestContext.Current.CancellationToken);

        var assertion30 = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            await harness.Manager.PublishUiFormAsync("plugin-without-ui", TestContext.Current.CancellationToken);
        });
        Assert.Matches("(?is)^.*declares\\ no\\ ui\\ data\\ entry.*$", assertion30.Message);
    }

    private sealed class FakePublisher : IFrontendSessionFramePublisher
    {
        internal bool Publishable { get; set; } = true;

        internal List<(AgentSessionId SessionId, string Type, JsonElement Data)> Published { get; } = [];

        public bool TryPublishPresentation(AgentSessionId sessionId, string type, JsonElement data)
        {
            if (!Publishable) return false;
            Published.Add((sessionId, type, data.Clone()));
            return true;
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task UiModelsFrameReachesTheSinkAndAnswersOk()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(
            TestContext.Current.CancellationToken);
        harness.Manager.SetCapabilityGrants("plugin-1", [ReplCapabilityName.UiModels]);
        var frames = new List<PluginUiFramePayload>();
        // `pluginId` exists only for the sink's per-frame assertion.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        harness.Manager.UiFrameSink = (pluginId, frame, _) =>
        {
            Assert.Equal("plugin-1", pluginId);
            frames.Add(frame);
            return ValueTask.CompletedTask;
        };

        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"capability.invoke\",\"correlationId\":\"ui-1\"," +
            "\"payload\":{\"pluginId\":\"plugin-1\",\"capability\":\"ui.models\"," +
            "\"payload\":{\"kind\":\"open\",\"commId\":\"u1\"," +
            "\"targetName\":\"maieutics.view/maieutics/form\",\"data\":{\"state\":{\"title\":\"hi\"}}}}}");

        var sent = await harness.Host.ReadSentAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"capability.result\"", sent);
        Assert.Contains("\"ui-1\"", sent);
        Assert.Equal("u1", Assert.Single(frames).CommId);
        Assert.Equal("maieutics.view/maieutics/form", frames[0].TargetName);
        Assert.Equal("hi", frames[0].Data!.Value.GetProperty("state").GetProperty("title").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task UiModelsWithoutGrantIsDenied()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(
            TestContext.Current.CancellationToken);
        var sinkRan = false;
        harness.Manager.UiFrameSink = (_, _, _) =>
        {
            sinkRan = true;
            return ValueTask.CompletedTask;
        };

        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"capability.invoke\",\"correlationId\":\"ui-2\"," +
            "\"payload\":{\"pluginId\":\"plugin-1\",\"capability\":\"ui.models\"," +
            "\"payload\":{\"kind\":\"message\",\"commId\":\"u1\",\"data\":{}}}}");

        var sent = await harness.Host.ReadSentAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"capability_denied\"", sent);
        Assert.False(sinkRan);
    }

    [Fact(Timeout = 60_000)]
    public async Task UiModelsRejectsMalformedFramesBeforeTheSink()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(
            TestContext.Current.CancellationToken);
        harness.Manager.SetCapabilityGrants("plugin-1", [ReplCapabilityName.UiModels]);
        var sinkRan = false;
        harness.Manager.UiFrameSink = (_, _, _) =>
        {
            sinkRan = true;
            return ValueTask.CompletedTask;
        };

        // An open without a target name is malformed.
        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"capability.invoke\",\"correlationId\":\"ui-3\"," +
            "\"payload\":{\"pluginId\":\"plugin-1\",\"capability\":\"ui.models\"," +
            "\"payload\":{\"kind\":\"open\",\"commId\":\"u1\"}}}");

        var sent = await harness.Host.ReadSentAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"invalid_capability_invoke\"", sent);
        Assert.False(sinkRan);
    }

    [Fact(Timeout = 60_000)]
    public async Task UiModelsSurfacesTypedSinkRejections()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(
            TestContext.Current.CancellationToken);
        harness.Manager.SetCapabilityGrants("plugin-1", [ReplCapabilityName.UiModels]);
        harness.Manager.UiFrameSink = (_, _, _) =>
            throw new InvalidOperationException("No active session to publish plugin UI into.");

        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"capability.invoke\",\"correlationId\":\"ui-4\"," +
            "\"payload\":{\"pluginId\":\"plugin-1\",\"capability\":\"ui.models\"," +
            "\"payload\":{\"kind\":\"message\",\"commId\":\"u1\",\"data\":{}}}}");

        var sent = await harness.Host.ReadSentAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"ui_frame_rejected\"", sent);
        Assert.Contains("No active session", sent);
    }

    [Fact(Timeout = 60_000)]
    public async Task CompositionPublishesOpenAndAnnouncementAndClosesOnMissingStream()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(
            TestContext.Current.CancellationToken);
        var router = new FrontendCommRouter((_, _, _) => ValueTask.CompletedTask);
        var publisher = new FakePublisher();
        var session = AgentSessionId.Create();
        using var state = JsonDocument.Parse("{\"state\":{\"title\":\"panel\"}}");

        // Open: the comm plane registers the model and the run stream announces it.
        await MaieuticsHost.PublishPluginUiFrameAsync(
            harness.Manager,
            router,
            session.ToString(),
            publisher,
            "plugin-1",
            new PluginUiFramePayload("open", "p1", "maieutics.view/maieutics/form", state.RootElement.Clone()),
            TestContext.Current.CancellationToken);

        Assert.Equal("maieutics.view/maieutics/form", Assert.Single(router.PlaneFor(session.ToString()).SnapshotLive()).TargetName);
        var announcement = Assert.Single(publisher.Published);
        Assert.Equal("repl.display", announcement.Type);
        var view = announcement.Data.GetProperty("application/vnd.maieutics.view+json");
        Assert.Equal("maieutics/form", view.GetProperty("viewFamily").GetString());
        Assert.Equal("p1", view.GetProperty("modelId").GetString());
        Assert.Equal("panel", view.GetProperty("state").GetProperty("title").GetString());

        // Without a live stream the open is rejected and the comm is closed again.
        publisher.Publishable = false;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            await MaieuticsHost.PublishPluginUiFrameAsync(
                harness.Manager,
                router,
                session.ToString(),
                publisher,
                "plugin-1",
                new PluginUiFramePayload("open", "p2", "maieutics.view/maieutics/form"),
                TestContext.Current.CancellationToken);
        });
        var live = router.PlaneFor(session.ToString()).SnapshotLive();
        Assert.Equal("p1", Assert.Single(live).CommId);
    }
}
