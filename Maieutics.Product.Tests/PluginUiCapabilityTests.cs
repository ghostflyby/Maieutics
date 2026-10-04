using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.DenoRepl;
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

    [Fact]
    public async Task UiModelsFrameReachesTheSinkAndAnswersOk()
    {
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(
            TestContext.Current.CancellationToken);
        harness.Manager.SetCapabilityGrants("plugin-1", [ReplCapabilityName.UiModels]);
        var frames = new List<PluginUiFramePayload>();
        harness.Manager.UiFrameSink = (pluginId, frame, _) =>
        {
            pluginId.Should().Be("plugin-1");
            frames.Add(frame);
            return ValueTask.CompletedTask;
        };

        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"capability.invoke\",\"correlationId\":\"ui-1\"," +
            "\"payload\":{\"pluginId\":\"plugin-1\",\"capability\":\"ui.models\"," +
            "\"payload\":{\"kind\":\"open\",\"commId\":\"u1\"," +
            "\"targetName\":\"maieutics.view/maieutics/form\",\"data\":{\"state\":{\"title\":\"hi\"}}}}}");

        var sent = await harness.Host!.ReadSentAsync(TestContext.Current.CancellationToken);
        sent.Should().Contain("\"capability.result\"");
        sent.Should().Contain("\"ui-1\"");
        frames.Should().ContainSingle().Which.CommId.Should().Be("u1");
        frames[0].TargetName.Should().Be("maieutics.view/maieutics/form");
        frames[0].Data!.Value.GetProperty("state").GetProperty("title").GetString().Should().Be("hi");
    }

    [Fact]
    public async Task UiModelsWithoutGrantIsDenied()
    {
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

        var sent = await harness.Host!.ReadSentAsync(TestContext.Current.CancellationToken);
        sent.Should().Contain("\"capability_denied\"");
        sinkRan.Should().BeFalse();
    }

    [Fact]
    public async Task UiModelsRejectsMalformedFramesBeforeTheSink()
    {
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

        var sent = await harness.Host!.ReadSentAsync(TestContext.Current.CancellationToken);
        sent.Should().Contain("\"invalid_capability_invoke\"");
        sinkRan.Should().BeFalse();
    }

    [Fact]
    public async Task UiModelsSurfacesTypedSinkRejections()
    {
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(
            TestContext.Current.CancellationToken);
        harness.Manager.SetCapabilityGrants("plugin-1", [ReplCapabilityName.UiModels]);
        harness.Manager.UiFrameSink = (_, _, _) =>
            throw new InvalidOperationException("No active session to publish plugin UI into.");

        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"capability.invoke\",\"correlationId\":\"ui-4\"," +
            "\"payload\":{\"pluginId\":\"plugin-1\",\"capability\":\"ui.models\"," +
            "\"payload\":{\"kind\":\"message\",\"commId\":\"u1\",\"data\":{}}}}");

        var sent = await harness.Host!.ReadSentAsync(TestContext.Current.CancellationToken);
        sent.Should().Contain("\"ui_frame_rejected\"");
        sent.Should().Contain("No active session");
    }

    [Fact]
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

        router.PlaneFor(session.ToString()).SnapshotLive().Should().ContainSingle()
            .Which.TargetName.Should().Be("maieutics.view/maieutics/form");
        var announcement = publisher.Published.Should().ContainSingle().Which;
        announcement.Type.Should().Be("repl.display");
        var view = announcement.Data.GetProperty("application/vnd.maieutics.view+json");
        view.GetProperty("viewFamily").GetString().Should().Be("maieutics/form");
        view.GetProperty("modelId").GetString().Should().Be("p1");
        view.GetProperty("state").GetProperty("title").GetString().Should().Be("panel");

        // Without a live stream the open is rejected and the comm is closed again.
        publisher.Publishable = false;
        var act = () => MaieuticsHost.PublishPluginUiFrameAsync(
            harness.Manager,
            router,
            session.ToString(),
            publisher,
            "plugin-1",
            new PluginUiFramePayload("open", "p2", "maieutics.view/maieutics/form", null),
            TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
        var live = router.PlaneFor(session.ToString()).SnapshotLive();
        live.Should().ContainSingle().Which.CommId.Should().Be("p1", "the rejected open's comm is closed");
    }
}
