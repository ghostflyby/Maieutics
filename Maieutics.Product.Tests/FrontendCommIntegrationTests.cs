using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Maieutics.DenoRepl;
using Maieutics.Frontend;

namespace Maieutics.Product.Tests;

/// <summary>
///     Integration coverage for the frontend comm plane (ADR 0024): the downlink relay
///     from a simulated REPL child, the uplink into the child, hello snapshot and replay
///     semantics, and the typed uplink rejections. Unix only — the child stub rides the
///     control host's Unix socket (see ReplCommChannelTests).
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendCommIntegrationTests
{
    [Fact(Timeout = 60_000)]
    public async Task CapabilitiesAdvertiseTheCommPlane()
    {
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var capabilities = await harness.Client.GetFromJsonAsync<JsonElement>(
            "/v1/agent/capabilities",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, capabilities.GetProperty("comm").GetProperty("version").GetInt32());
        Assert.Equal(16 * 1024 * 1024, capabilities.GetProperty("comm").GetProperty("maxMessageBytes").GetInt32());
    }

    [Fact(Timeout = 60_000)]
    public async Task DownlinkReachesTheSocketAndUplinkReachesTheChild()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(
                "The child stub rides the control host's Unix socket; Windows CI "
                + "covers the control channel with its own credential bootstrap instead.");
        }

        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, TestContext.Current.CancellationToken);
        var hello = await ReceiveHelloAsync(comms, TestContext.Current.CancellationToken);
        Assert.Equal(0, hello.GetProperty("live").GetArrayLength());
        Assert.False(hello.GetProperty("replayed").GetBoolean());
        Assert.False(hello.GetProperty("truncated").GetBoolean());

        using var child = await CommChildSocketStub.ConnectAsync(harness.ControlAddress, sessionId, TestContext.Current.CancellationToken);
        await CommChildSocketStub.ExpectReadyAsync(child, TestContext.Current.CancellationToken);
        await CommChildSocketStub.SendAsync(child, Open("w1", "anywidget", new { ready = true }), TestContext.Current.CancellationToken);

        var (sequence, downlink) = await ReceiveCommAsync(comms, TestContext.Current.CancellationToken);
        Assert.Equal(1, sequence);
        Assert.Equal(ReplCommKind.Open, downlink.Kind);
        Assert.Equal("w1", downlink.CommId);
        Assert.Equal("anywidget", downlink.TargetName);
        Assert.NotNull(downlink.Data);
        Assert.True(downlink.Data!.Value.GetProperty("ready").GetBoolean());

        await SendCommAsync(
            comms,
            new ReplCommMessage(
                ReplCommKind.Message,
                "w1",
                null,
                JsonSerializer.SerializeToElement(new { click = 1 }),
                null,
                [[0xEF]]),
            TestContext.Current.CancellationToken);

        var (opcode, payload) = await CommChildSocketStub.ReceiveFrameAsync(child, TestContext.Current.CancellationToken);
        Assert.Equal(0x2, opcode);
        var uplink = ReplCommCodec.Decode(payload);
        Assert.Equal(ReplCommKind.Message, uplink.Kind);
        Assert.Equal("w1", uplink.CommId);
        Assert.Equal(1, uplink.Data!.Value.GetProperty("click").GetInt32());
        Assert.Equal(new byte[] { 0xEF }, Assert.Single(uplink.Buffers));
    }

    [Fact(Timeout = 60_000)]
    public async Task HelloCarriesLiveCommsAndSinceReplay()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(
                "The child stub rides the control host's Unix socket; Windows CI "
                + "covers the control channel with its own credential bootstrap instead.");
        }

        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        harness.RegisterControlPeer(sessionId);

        // The child opens two comms before any frontend connects; the plane buffers them.
        using var child = await CommChildSocketStub.ConnectAsync(harness.ControlAddress, sessionId, TestContext.Current.CancellationToken);
        await CommChildSocketStub.ExpectReadyAsync(child, TestContext.Current.CancellationToken);
        await CommChildSocketStub.SendAsync(child, Open("w1", "tw1", null), TestContext.Current.CancellationToken);
        await CommChildSocketStub.SendAsync(child, Open("w2", "tw2", null), TestContext.Current.CancellationToken);

        // The child's open frames are in flight the moment SendAsync returns; the
        // plane's live set is the observable, and each mutation has a completion
        // signal. Capture the signal BEFORE checking the state (the signal-swap and
        // the mutation share the plane's lock, so a mutation between the check and
        // the capture would strand the waiter on an already-consumed signal), then
        // re-check after every wake — no throwaway probe connections.
        var plane = harness.CommRouter.PlaneFor(sessionId);
        while (true)
        {
            var changed = plane.LiveSetChanged;
            if (plane.SnapshotLive().Count >= 2) break;
            await changed.WaitAsync(TestContext.Current.CancellationToken);
        }

        using var first = await ConnectCommsAsync(harness, sessionId, TestContext.Current.CancellationToken);
        var firstHello = await ReceiveHelloAsync(first, TestContext.Current.CancellationToken);
        Assert.False(firstHello.GetProperty("replayed").GetBoolean());
        Assert.Equal(["w1", "w2"], firstHello.GetProperty("live").EnumerateArray().Select(entry => entry.GetProperty("commId").GetString()));
        var (one, _) = await ReceiveCommAsync(first, TestContext.Current.CancellationToken);
        var (two, _) = await ReceiveCommAsync(first, TestContext.Current.CancellationToken);
        Assert.Equal(1, one);
        Assert.Equal(2, two);

        using var resumed = await ConnectCommsAsync(harness, sessionId, TestContext.Current.CancellationToken, sinceSeq: 1);
        var resumedHello = await ReceiveHelloAsync(resumed, TestContext.Current.CancellationToken);
        Assert.True(resumedHello.GetProperty("replayed").GetBoolean());
        Assert.False(resumedHello.GetProperty("truncated").GetBoolean());
        var (only, message) = await ReceiveCommAsync(resumed, TestContext.Current.CancellationToken);
        Assert.Equal(2, only);
        Assert.Equal("w2", message.CommId);

        using var ahead = await ConnectCommsAsync(harness, sessionId, TestContext.Current.CancellationToken, sinceSeq: 5);
        var aheadHello = await ReceiveHelloAsync(ahead, TestContext.Current.CancellationToken);
        // The client claims a position that was never published: state is unknown.
        Assert.True(aheadHello.GetProperty("truncated").GetBoolean());
    }

    [Fact(Timeout = 60_000)]
    public async Task UnknownCommUplinkIsTypedErrorAndSocketSurvives()
    {
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, TestContext.Current.CancellationToken);
        await ReceiveHelloAsync(comms, TestContext.Current.CancellationToken);
        await SendCommAsync(comms, Message("ghost"), TestContext.Current.CancellationToken);

        var error = await ReceiveCommErrorAsync(comms, TestContext.Current.CancellationToken);
        Assert.Equal("comm.error", error.GetProperty("type").GetString());
        Assert.Equal("comm_not_found", error.GetProperty("code").GetString());
        Assert.Equal("ghost", error.GetProperty("commId").GetString());

        // The socket stays open: a follow-up uplink still gets a typed answer.
        await SendCommAsync(comms, Message("phantom"), TestContext.Current.CancellationToken);
        var second = await ReceiveCommErrorAsync(comms, TestContext.Current.CancellationToken);
        Assert.Equal("phantom", second.GetProperty("commId").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task UplinkOpenIsAPolicyViolationClose()
    {
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, TestContext.Current.CancellationToken);
        await ReceiveHelloAsync(comms, TestContext.Current.CancellationToken);
        await SendCommAsync(comms, Open("w1", "tw", null), TestContext.Current.CancellationToken);

        var result = await comms.ReceiveAsync(new byte[64], TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, comms.CloseStatus);
    }

    [Fact(Timeout = 60_000)]
    public async Task UplinkAfterChildDetachIsReplUnavailable()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(
                "The child stub rides the control host's Unix socket; Windows CI "
                + "covers the control channel with its own credential bootstrap instead.");
        }

        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, TestContext.Current.CancellationToken);
        await ReceiveHelloAsync(comms, TestContext.Current.CancellationToken);

        // The mid-test Dispose is the point of the test (the abrupt close detaches the
        // REPL session), so the child socket is owned explicitly instead of a using scope
        // that would auto-dispose behind the manual close.
        var child = await CommChildSocketStub.ConnectAsync(harness.ControlAddress, sessionId, TestContext.Current.CancellationToken);
        await CommChildSocketStub.ExpectReadyAsync(child, TestContext.Current.CancellationToken);
        await CommChildSocketStub.SendAsync(child, Open("w9", "tw9", null), TestContext.Current.CancellationToken);
        var (_, opened) = await ReceiveCommAsync(comms, TestContext.Current.CancellationToken);
        Assert.Equal("w9", opened.CommId);

        child.Dispose();

        // The detach is observed by the control host's own receive loop, so instead of a fixed
        // grace period the uplink is repeated until the detach is processed and the typed
        // repl_unavailable rejection arrives. Each attempt's receive is bounded; the whole loop
        // stays under the test deadline (a deadline cancellation propagates and fails the test).
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        while (true)
        {
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            await SendCommAsync(comms, Message("w9"), attempt.Token);
            try
            {
                var error = await ReceiveCommErrorAsync(comms, attempt.Token);
                Assert.Equal("repl_unavailable", error.GetProperty("code").GetString());
                Assert.Equal("w9", error.GetProperty("commId").GetString());
                break;
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                // The detach has not been observed yet (the send may still have been accepted);
                // retry the uplink until the rejection is typed.
            }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CommsAreServedPerSession()
    {
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        // An unknown session fails the handshake...
        await Assert.ThrowsAnyAsync<WebSocketException>(async () => await ConnectCommsAsync(
            harness,
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken));

        // ...while any known session's comms plane is served directly, whether
        // or not it is the foreground session.
        using var connection = await ConnectCommsAsync(harness, sessionId, TestContext.Current.CancellationToken);
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
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException(
                    $"The comms socket closed: {socket.CloseStatus} {socket.CloseStatusDescription}");
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var type = result.MessageType;
            if (result.EndOfMessage) return (text, type);
        }
    }

    private static async Task<(long Sequence, ReplCommMessage Message)> ReceiveCommAsync(
        ClientWebSocket socket,
        CancellationToken ct)
    {
        var payload = new byte[2 * 1024 * 1024];
        var offset = 0;
        WebSocketMessageType type;
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

        Assert.Equal(WebSocketMessageType.Binary, type);
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
