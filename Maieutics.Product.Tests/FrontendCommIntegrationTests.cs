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
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var capabilities = await harness.Client.GetFromJsonAsync<JsonElement>(
            "/v1/agent/capabilities",
            deadline.Token);
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

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, deadline.Token);
        var hello = await ReceiveHelloAsync(comms, deadline.Token);
        Assert.Equal(0, hello.GetProperty("live").GetArrayLength());
        Assert.False(hello.GetProperty("replayed").GetBoolean());
        Assert.False(hello.GetProperty("truncated").GetBoolean());

        using var child = await CommChildSocketStub.ConnectAsync(harness.ControlAddress, sessionId, deadline.Token);
        await CommChildSocketStub.ExpectReadyAsync(child, deadline.Token);
        await CommChildSocketStub.SendAsync(child, Open("w1", "anywidget", new { ready = true }), deadline.Token);

        var (sequence, downlink) = await ReceiveCommAsync(comms, deadline.Token);
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
            deadline.Token);

        var (opcode, payload) = await CommChildSocketStub.ReceiveFrameAsync(child, deadline.Token);
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

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        harness.RegisterControlPeer(sessionId);

        // The child opens two comms before any frontend connects; the plane buffers them.
        using var child = await CommChildSocketStub.ConnectAsync(harness.ControlAddress, sessionId, deadline.Token);
        await CommChildSocketStub.ExpectReadyAsync(child, deadline.Token);
        await CommChildSocketStub.SendAsync(child, Open("w1", "tw1", null), deadline.Token);
        await CommChildSocketStub.SendAsync(child, Open("w2", "tw2", null), deadline.Token);

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
            await changed.WaitAsync(deadline.Token);
        }

        using var first = await ConnectCommsAsync(harness, sessionId, deadline.Token);
        var firstHello = await ReceiveHelloAsync(first, deadline.Token);
        Assert.False(firstHello.GetProperty("replayed").GetBoolean());
        Assert.Equal(["w1", "w2"], firstHello.GetProperty("live").EnumerateArray().Select(entry => entry.GetProperty("commId").GetString()));
        var (one, _) = await ReceiveCommAsync(first, deadline.Token);
        var (two, _) = await ReceiveCommAsync(first, deadline.Token);
        Assert.Equal(1, one);
        Assert.Equal(2, two);

        using var resumed = await ConnectCommsAsync(harness, sessionId, deadline.Token, sinceSeq: 1);
        var resumedHello = await ReceiveHelloAsync(resumed, deadline.Token);
        Assert.True(resumedHello.GetProperty("replayed").GetBoolean());
        Assert.False(resumedHello.GetProperty("truncated").GetBoolean());
        var (only, message) = await ReceiveCommAsync(resumed, deadline.Token);
        Assert.Equal(2, only);
        Assert.Equal("w2", message.CommId);

        using var ahead = await ConnectCommsAsync(harness, sessionId, deadline.Token, sinceSeq: 5);
        var aheadHello = await ReceiveHelloAsync(ahead, deadline.Token);
        // The client claims a position that was never published: state is unknown.
        Assert.True(aheadHello.GetProperty("truncated").GetBoolean());
    }

    [Fact(Timeout = 60_000)]
    public async Task UnknownCommUplinkIsTypedErrorAndSocketSurvives()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, deadline.Token);
        await ReceiveHelloAsync(comms, deadline.Token);
        await SendCommAsync(comms, Message("ghost"), deadline.Token);

        var error = await ReceiveCommErrorAsync(comms, deadline.Token);
        Assert.Equal("comm.error", error.GetProperty("type").GetString());
        Assert.Equal("comm_not_found", error.GetProperty("code").GetString());
        Assert.Equal("ghost", error.GetProperty("commId").GetString());

        // The socket stays open: a follow-up uplink still gets a typed answer.
        await SendCommAsync(comms, Message("phantom"), deadline.Token);
        var second = await ReceiveCommErrorAsync(comms, deadline.Token);
        Assert.Equal("phantom", second.GetProperty("commId").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task UplinkOpenIsAPolicyViolationClose()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, deadline.Token);
        await ReceiveHelloAsync(comms, deadline.Token);
        await SendCommAsync(comms, Open("w1", "tw", null), deadline.Token);

        var result = await comms.ReceiveAsync(new byte[64], deadline.Token);
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

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        harness.RegisterControlPeer(sessionId);

        using var comms = await ConnectCommsAsync(harness, sessionId, deadline.Token);
        await ReceiveHelloAsync(comms, deadline.Token);

        // The mid-test Dispose is the point of the test (the abrupt close detaches the
        // REPL session), so the child socket is owned explicitly instead of a using scope
        // that would auto-dispose behind the manual close.
        var child = await CommChildSocketStub.ConnectAsync(harness.ControlAddress, sessionId, deadline.Token);
        await CommChildSocketStub.ExpectReadyAsync(child, deadline.Token);
        await CommChildSocketStub.SendAsync(child, Open("w9", "tw9", null), deadline.Token);
        var (_, opened) = await ReceiveCommAsync(comms, deadline.Token);
        Assert.Equal("w9", opened.CommId);

        child.Dispose();

        // The detach is observed by the control host's own receive loop, so instead of a fixed
        // grace period the uplink is repeated until the detach is processed and the typed
        // repl_unavailable rejection arrives. Each attempt's receive is bounded; the whole loop
        // stays under the test deadline (a deadline cancellation propagates and fails the test).
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
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
            catch (OperationCanceledException) when (!deadline.Token.IsCancellationRequested)
            {
                // The detach has not been observed yet (the send may still have been accepted);
                // retry the uplink until the rejection is typed.
            }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CommsAreServedPerSession()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        // An unknown session fails the handshake...
        await Assert.ThrowsAnyAsync<WebSocketException>(async () => await ConnectCommsAsync(
            harness,
            Guid.NewGuid().ToString("N"),
            deadline.Token));

        // ...while any known session's comms plane is served directly, whether
        // or not it is the foreground session.
        using var connection = await ConnectCommsAsync(harness, sessionId, deadline.Token);
    }

    private static CancellationTokenSource CreateDeadline(
        CancellationToken cancellationToken,
        TimeSpan timeout) => TestDeadlines.Create(cancellationToken, timeout);

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
