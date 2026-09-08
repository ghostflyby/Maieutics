using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.DenoRepl;
using Maieutics.Frontend;
using Maieutics.Providers.OpenAI;

namespace Maieutics.Jupyter.Tests;

/// <summary>
///     Integration coverage for the frontend comm plane (ADR 0024): the downlink relay
///     from a simulated REPL child, the uplink into the child, hello snapshot and replay
///     semantics, and the typed uplink rejections. Unix only — the child stub rides the
///     control host's Unix socket (see ReplCommChannelTests).
/// </summary>
[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class FrontendCommIntegrationTests
{
    [Fact(Timeout = 60_000)]
    public async Task CapabilitiesAdvertiseTheCommPlane()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);
        var capabilities = await harness.Client.GetFromJsonAsync<JsonElement>(
            "/v1/agent/capabilities",
            deadline.Token);
        capabilities.GetProperty("comm").GetProperty("version").GetInt32().Should().Be(1);
        capabilities.GetProperty("comm").GetProperty("maxMessageBytes").GetInt32().Should().Be(16 * 1024 * 1024);
    }

    [Fact(Timeout = 60_000)]
    public async Task DownlinkReachesTheSocketAndUplinkReachesTheChild()
    {
        // The child stub rides the control host's Unix socket; Windows CI
        // covers the control channel with its own credential bootstrap instead.
        if (OperatingSystem.IsWindows()) return;

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, deadline.Token);
        var hello = await ReceiveHelloAsync(comms, deadline.Token);
        hello.GetProperty("live").GetArrayLength().Should().Be(0);
        hello.GetProperty("replayed").GetBoolean().Should().BeFalse();
        hello.GetProperty("truncated").GetBoolean().Should().BeFalse();

        using var child = await CommChildSocketStub.ConnectAsync(harness.ControlAddress, sessionId, deadline.Token);
        await CommChildSocketStub.ExpectReadyAsync(child, deadline.Token);
        await CommChildSocketStub.SendAsync(child, Open("w1", "anywidget", new { ready = true }), deadline.Token);

        var (sequence, downlink) = await ReceiveCommAsync(comms, deadline.Token);
        sequence.Should().Be(1);
        downlink.Kind.Should().Be(ReplCommKind.Open);
        downlink.CommId.Should().Be("w1");
        downlink.TargetName.Should().Be("anywidget");
        downlink.Data.Should().NotBeNull();
        downlink.Data!.Value.GetProperty("ready").GetBoolean().Should().BeTrue();

        await SendCommAsync(
            comms,
            new ReplCommMessage(
                ReplCommKind.Message,
                "w1",
                null,
                JsonSerializer.SerializeToElement(new { click = 1 }),
                null,
                [new byte[] { 0xEF }]),
            deadline.Token);

        var (opcode, payload) = await CommChildSocketStub.ReceiveFrameAsync(child, deadline.Token);
        opcode.Should().Be(0x2);
        var uplink = ReplCommCodec.Decode(payload);
        uplink.Kind.Should().Be(ReplCommKind.Message);
        uplink.CommId.Should().Be("w1");
        uplink.Data!.Value.GetProperty("click").GetInt32().Should().Be(1);
        uplink.Buffers.Should().ContainSingle().Which.Should().Equal(new byte[] { 0xEF });
    }

    [Fact(Timeout = 60_000)]
    public async Task HelloCarriesLiveCommsAndSinceReplay()
    {
        // The child stub rides the control host's Unix socket; Windows CI
        // covers the control channel with its own credential bootstrap instead.
        if (OperatingSystem.IsWindows()) return;

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        harness.RegisterControlPeer(sessionId);

        // The child opens two comms before any frontend connects; the plane buffers them.
        using var child = await CommChildSocketStub.ConnectAsync(harness.ControlAddress, sessionId, deadline.Token);
        await CommChildSocketStub.ExpectReadyAsync(child, deadline.Token);
        await CommChildSocketStub.SendAsync(child, Open("w1", "tw1", null), deadline.Token);
        await CommChildSocketStub.SendAsync(child, Open("w2", "tw2", null), deadline.Token);

        using var first = await ConnectCommsAsync(harness, sessionId, deadline.Token);
        var firstHello = await ReceiveHelloAsync(first, deadline.Token);
        firstHello.GetProperty("replayed").GetBoolean().Should().BeFalse();
        firstHello.GetProperty("live").EnumerateArray().Select(entry => entry.GetProperty("commId").GetString())
            .Should().Equal("w1", "w2");
        var (one, _) = await ReceiveCommAsync(first, deadline.Token);
        var (two, _) = await ReceiveCommAsync(first, deadline.Token);
        one.Should().Be(1);
        two.Should().Be(2);

        using var resumed = await ConnectCommsAsync(harness, sessionId, deadline.Token, sinceSeq: 1);
        var resumedHello = await ReceiveHelloAsync(resumed, deadline.Token);
        resumedHello.GetProperty("replayed").GetBoolean().Should().BeTrue();
        resumedHello.GetProperty("truncated").GetBoolean().Should().BeFalse();
        var (only, message) = await ReceiveCommAsync(resumed, deadline.Token);
        only.Should().Be(2);
        message.CommId.Should().Be("w2");

        using var ahead = await ConnectCommsAsync(harness, sessionId, deadline.Token, sinceSeq: 5);
        var aheadHello = await ReceiveHelloAsync(ahead, deadline.Token);
        // The client claims a position that was never published: state is unknown.
        aheadHello.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task UnknownCommUplinkIsTypedErrorAndSocketSurvives()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, deadline.Token);
        await ReceiveHelloAsync(comms, deadline.Token);
        await SendCommAsync(comms, Message("ghost"), deadline.Token);

        var error = await ReceiveCommErrorAsync(comms, deadline.Token);
        error.GetProperty("type").GetString().Should().Be("comm.error");
        error.GetProperty("code").GetString().Should().Be("comm_not_found");
        error.GetProperty("commId").GetString().Should().Be("ghost");

        // The socket stays open: a follow-up uplink still gets a typed answer.
        await SendCommAsync(comms, Message("phantom"), deadline.Token);
        var second = await ReceiveCommErrorAsync(comms, deadline.Token);
        second.GetProperty("commId").GetString().Should().Be("phantom");
    }

    [Fact(Timeout = 60_000)]
    public async Task UplinkOpenIsAPolicyViolationClose()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, deadline.Token);
        await ReceiveHelloAsync(comms, deadline.Token);
        await SendCommAsync(comms, Open("w1", "tw", null), deadline.Token);

        var result = await comms.ReceiveAsync(new byte[64], deadline.Token);
        result.MessageType.Should().Be(WebSocketMessageType.Close);
        comms.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
    }

    [Fact(Timeout = 60_000)]
    public async Task UplinkAfterChildDetachIsReplUnavailable()
    {
        // The child stub rides the control host's Unix socket; Windows CI
        // covers the control channel with its own credential bootstrap instead.
        if (OperatingSystem.IsWindows()) return;

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, deadline.Token);
        await ReceiveHelloAsync(comms, deadline.Token);

        using var child = await CommChildSocketStub.ConnectAsync(harness.ControlAddress, sessionId, deadline.Token);
        await CommChildSocketStub.ExpectReadyAsync(child, deadline.Token);
        await CommChildSocketStub.SendAsync(child, Open("w9", "tw9", null), deadline.Token);
        var (_, opened) = await ReceiveCommAsync(comms, deadline.Token);
        opened.CommId.Should().Be("w9");

        child.Dispose();
        await Task.Delay(300, deadline.Token);

        await SendCommAsync(comms, Message("w9"), deadline.Token);
        var error = await ReceiveCommErrorAsync(comms, deadline.Token);
        error.GetProperty("code").GetString().Should().Be("repl_unavailable");
    }

    [Fact(Timeout = 60_000)]
    public async Task CommsServeTheActiveSessionOnly()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);
        await harness.GetSessionIdAsync(deadline.Token);

        var connect = async () => await ConnectCommsAsync(
            harness,
            Guid.NewGuid().ToString("N"),
            deadline.Token);
        await connect.Should().ThrowAsync<WebSocketException>();
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private static ReplCommMessage Open(string commId, string targetName, object? state)
    {
        return new ReplCommMessage(
            ReplCommKind.Open,
            commId,
            targetName,
            state is null ? null : JsonSerializer.SerializeToElement(state),
            null,
            []);
    }

    private static ReplCommMessage Message(string commId)
    {
        return new ReplCommMessage(
            ReplCommKind.Message,
            commId,
            null,
            JsonSerializer.SerializeToElement(new { marker = 1 }),
            null,
            []);
    }

    private static async Task<ClientWebSocket> ConnectCommsAsync(
        FrontendApiIntegrationTests.FrontendHarness harness,
        string sessionId,
        CancellationToken cancellationToken,
        long sinceSeq = 0)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {harness.Token}");
        await socket.ConnectAsync(
            new Uri(
                $"{harness.Url.Replace("http://", "ws://")}/v1/agent/sessions/{sessionId}/comms?sinceSeq={sinceSeq}"),
            cancellationToken);
        return socket;
    }

    private static async Task<JsonElement> ReceiveHelloAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var (text, _) = await ReceiveTextAsync(socket, ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> ReceiveCommErrorAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var (text, _) = await ReceiveTextAsync(socket, ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<(string Text, WebSocketMessageType Type)> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        string text;
        WebSocketMessageType type;
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException(
                    $"The comms socket closed: {socket.CloseStatus} {socket.CloseStatusDescription}");
            text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            type = result.MessageType;
            if (result.EndOfMessage) return (text, type);
        }
    }

    private static async Task<(long Sequence, ReplCommMessage Message)> ReceiveCommAsync(
        ClientWebSocket socket,
        CancellationToken ct)
    {
        var payload = new byte[2 * 1024 * 1024];
        var offset = 0;
        WebSocketMessageType type = WebSocketMessageType.Binary;
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(payload, offset, payload.Length - offset), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException(
                    $"The comms socket closed: {socket.CloseStatus} {socket.CloseStatusDescription}");
            type = result.MessageType;
            offset += result.Count;
            if (result.EndOfMessage) break;
        }

        type.Should().Be(WebSocketMessageType.Binary);
        var sequence = (long)BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(0, 8));
        return (sequence, ReplCommCodec.Decode(payload[8..offset]));
    }

    private static async Task SendCommAsync(ClientWebSocket socket, ReplCommMessage message, CancellationToken ct)
    {
        var payload = FrontendCommEnvelope.Encode(0, message);
        await socket.SendAsync(
            new ArraySegment<byte>(payload),
            WebSocketMessageType.Binary,
            true,
            ct);
    }
}
