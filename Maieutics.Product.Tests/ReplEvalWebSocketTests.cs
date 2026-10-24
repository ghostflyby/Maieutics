using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Control;
using Maieutics.DenoRepl;
using System.Text;

namespace Maieutics.Product.Tests;

public sealed class ReplEvalWebSocketTests
{
    [Fact]
    public void ProtocolEnvelopeRoundTripsWithSourceGeneratedMetadata()
    {
        var payload = new ReplEvalExecutePayload("execution-1", "1 + 1");
        var envelope = new ReplEvalEnvelope(
            ReplEvalProtocol.Version,
            ReplEvalMessageType.Execute,
            payload.ExecutionId,
            ReplEvalProtocol.Payload(payload, ReplEvalJsonContext.Default.ReplEvalExecutePayload));

        var decoded = ReplEvalProtocol.Deserialize(Encoding.UTF8.GetString(ReplEvalProtocol.Serialize(envelope)));
        var decodedPayload = ReplEvalProtocol.ParsePayload(
            decoded,
            ReplEvalJsonContext.Default.ReplEvalExecutePayload);

        Assert.Equal(envelope.Version, decoded.Version);
        Assert.Equal(envelope.Type, decoded.Type);
        Assert.Equal(envelope.CorrelationId, decoded.CorrelationId);
        Assert.Equal(envelope.Payload?.GetRawText(), decoded.Payload?.GetRawText());
        Assert.Equal(payload, decodedPayload);
        Assert.Equal("/v1/repl/eval/ws", ReplEvalProtocol.WebSocketPath);
    }

    [Fact(Timeout = 30_000)]
    public async Task OrderedExecutionRoutesInputAndDisposesGracefully()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var registry = new ReplControlSessionRegistry();
        registry.Register(41, "session-1");
        var credentials = new ReplControlCredentialRegistry();
        await using var host = new ReplEvalWebSocketHost(registry, credentials);
        using var socket = new TestWebSocket();
        var connectionWait = host.WaitForConnectionAsync("session-1", 2, deadline.Token);
        socket.QueueText(Envelope(
            ReplEvalMessageType.Hello,
            "hello-1",
            new ReplEvalIdentity("session-1", 2),
            ReplEvalJsonContext.Default.ReplEvalIdentity));
        var attached = host.AttachAsync(socket, 41, deadline.Token);

        var readyJson = await socket.ReadSentAsync(deadline.Token);
        Assert.DoesNotContain("\"credential\"", readyJson);
        var ready = ReplEvalProtocol.Deserialize(readyJson);
        Assert.Equal(ReplEvalMessageType.Ready, ready.Type);
        var connection = await connectionWait;
        var execution = await connection.ExecuteAsync("console.log('hello')", deadline.Token);
        var execute = ReplEvalProtocol.Deserialize(await socket.ReadSentAsync(deadline.Token));
        Assert.Equal(ReplEvalMessageType.Execute, execute.Type);
        Assert.Equal(execution.ExecutionId, execute.CorrelationId);

        await using var events = execution.Events.GetAsyncEnumerator(deadline.Token);
        var request = new ReplEvalInputRequestPayload(
            execution.ExecutionId,
            1,
            "input-1",
            "Name?",
            false);
        socket.QueueText(Envelope(
            ReplEvalMessageType.InputRequest,
            request.RequestId,
            request,
            ReplEvalJsonContext.Default.ReplEvalInputRequestPayload));
        Assert.True(await events.MoveNextAsync());
        var input = Assert.IsType<ReplEvalInputRequestEvent>(events.Current);
        await connection.ReplyInputAsync(input, "Ada", deadline.Token);
        var reply = ReplEvalProtocol.Deserialize(await socket.ReadSentAsync(deadline.Token));
        Assert.Equal(ReplEvalMessageType.InputReply, reply.Type);
        Assert.Equal(request.RequestId, reply.CorrelationId);

        using var resultDocument = JsonDocument.Parse("42");
        socket.QueueText(Envelope(
            ReplEvalMessageType.Result,
            execution.ExecutionId,
            new ReplEvalResultPayload(execution.ExecutionId, resultDocument.RootElement.Clone()),
            ReplEvalJsonContext.Default.ReplEvalResultPayload));
        Assert.IsType<ReplEvalResultTerminal>(await execution.Completion.WaitAsync(deadline.Token));
        Assert.False(await events.MoveNextAsync());

        await CompleteGracefulShutdownAsync(connection, socket, attached, deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task HelloCredentialAuthenticatesButIsNotEchoed()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var registry = new ReplControlSessionRegistry();
        var credentials = new ReplControlCredentialRegistry();
        var credential = credentials.Issue("session-1");
        await using var host = new ReplEvalWebSocketHost(registry, credentials);
        using var socket = new TestWebSocket();
        var connectionWait = host.WaitForConnectionAsync("session-1", 1, deadline.Token);
        socket.QueueText(Envelope(
            ReplEvalMessageType.Hello,
            "hello-1",
            new ReplEvalIdentity("session-1", 1, credential),
            ReplEvalJsonContext.Default.ReplEvalIdentity));
        var attached = host.AttachAsync(socket, 0, deadline.Token);

        var readyJson = await socket.ReadSentAsync(deadline.Token);
        Assert.DoesNotContain("\"credential\"", readyJson);
        var ready = ReplEvalProtocol.Deserialize(readyJson);
        var identity = ReplEvalProtocol.ParsePayload(ready, ReplEvalJsonContext.Default.ReplEvalIdentity);
        Assert.Equal(new ReplEvalIdentity("session-1", 1), identity);
        var connection = await connectionWait;

        await CompleteGracefulShutdownAsync(connection, socket, attached, deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task UnauthenticatedHelloIsRejected()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        await using var host = new ReplEvalWebSocketHost(
            new ReplControlSessionRegistry(),
            new ReplControlCredentialRegistry());
        using var socket = new TestWebSocket();
        socket.QueueText(Envelope(
            ReplEvalMessageType.Hello,
            "hello-1",
            new ReplEvalIdentity("session-1", 1),
            ReplEvalJsonContext.Default.ReplEvalIdentity));

        await host.AttachAsync(socket, 0, deadline.Token);
        await socket.Closed.WaitAsync(deadline.Token);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact(Timeout = 30_000)]
    public async Task OutputMessageTypesAreUnexpectedOnTheEvalChannel()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var registry = new ReplControlSessionRegistry();
        registry.Register(41, "session-1");
        await using var host = new ReplEvalWebSocketHost(registry, new ReplControlCredentialRegistry());
        using var socket = new TestWebSocket();
        var connectionWait = host.WaitForConnectionAsync("session-1", 1, deadline.Token);
        socket.QueueText(Envelope(
            ReplEvalMessageType.Hello,
            "hello-1",
            new ReplEvalIdentity("session-1", 1),
            ReplEvalJsonContext.Default.ReplEvalIdentity));
        var attached = host.AttachAsync(socket, 41, deadline.Token);
        await socket.ReadSentAsync(deadline.Token);
        var connection = await connectionWait;
        var execution = await connection.ExecuteAsync("1 + 1", deadline.Token);
        await socket.ReadSentAsync(deadline.Token);

        // Console, display, updateDisplay, and clearOutput left the eval control plane and travel
        // over the dedicated binary output endpoint (phase 2). An envelope claiming one of those
        // types is rejected during deserialization (the type is no longer in the known set).
        var consoleJson = JsonSerializer.Serialize(
            new ReplEvalEnvelope(
                ReplEvalProtocol.Version,
                "repl.eval.console",
                "console-1",
                JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["executionId"] = execution.ExecutionId,
                    ["sequence"] = 1,
                    ["stream"] = "stdout",
                    ["text"] = "hi"
                })),
            ReplEvalJsonContext.Default.ReplEvalEnvelope);
        socket.QueueText(consoleJson);
        Assert.Equal("unknown_message_type", (await Assert.ThrowsAnyAsync<ReplEvalProtocolException>(async () => { await connection.Completion; })).Code);
        await attached.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplicateTerminalTerminatesConnection()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var registry = new ReplControlSessionRegistry();
        registry.Register(41, "session-1");
        await using var host = new ReplEvalWebSocketHost(registry, new ReplControlCredentialRegistry());
        using var socket = new TestWebSocket();
        var connectionWait = host.WaitForConnectionAsync("session-1", 1, deadline.Token);
        socket.QueueText(Envelope(
            ReplEvalMessageType.Hello,
            "hello-1",
            new ReplEvalIdentity("session-1", 1),
            ReplEvalJsonContext.Default.ReplEvalIdentity));
        var attached = host.AttachAsync(socket, 41, deadline.Token);
        await socket.ReadSentAsync(deadline.Token);
        var connection = await connectionWait;
        var execution = await connection.ExecuteAsync("1 + 1", deadline.Token);
        await socket.ReadSentAsync(deadline.Token);
        var result = Envelope(
            ReplEvalMessageType.Result,
            execution.ExecutionId,
            new ReplEvalResultPayload(execution.ExecutionId),
            ReplEvalJsonContext.Default.ReplEvalResultPayload);
        socket.QueueText(result);
        await execution.Completion.WaitAsync(deadline.Token);

        socket.QueueText(result);

        Assert.Equal("duplicate_terminal", (await Assert.ThrowsAnyAsync<ReplEvalProtocolException>(async () => { await connection.Completion; })).Code);
        await attached.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancelWaitsForCancelledTerminalThenConnectionClosesGracefully()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var registry = new ReplControlSessionRegistry();
        registry.Register(41, "session-1");
        await using var host = new ReplEvalWebSocketHost(registry, new ReplControlCredentialRegistry());
        using var socket = new TestWebSocket();
        var connectionWait = host.WaitForConnectionAsync("session-1", 1, deadline.Token);
        socket.QueueText(Envelope(
            ReplEvalMessageType.Hello,
            "hello-1",
            new ReplEvalIdentity("session-1", 1),
            ReplEvalJsonContext.Default.ReplEvalIdentity));
        var attached = host.AttachAsync(socket, 41, deadline.Token);
        await socket.ReadSentAsync(deadline.Token);
        var connection = await connectionWait;
        var execution = await connection.ExecuteAsync("await pending", deadline.Token);
        await socket.ReadSentAsync(deadline.Token);

        var cancel = connection.CancelAsync(execution.ExecutionId, deadline.Token);
        var cancelEnvelope = ReplEvalProtocol.Deserialize(await socket.ReadSentAsync(deadline.Token));
        Assert.Equal(ReplEvalMessageType.Cancel, cancelEnvelope.Type);
        socket.QueueText(Envelope(
            ReplEvalMessageType.Cancelled,
            execution.ExecutionId,
            new ReplEvalCancelledPayload(execution.ExecutionId),
            ReplEvalJsonContext.Default.ReplEvalCancelledPayload));

        await cancel;
        Assert.IsType<ReplEvalCancelledTerminal>(await execution.Completion);
        await CompleteGracefulShutdownAsync(connection, socket, attached, deadline.Token);
    }

    private static async Task CompleteGracefulShutdownAsync(
        ReplEvalWebSocketConnection connection,
        TestWebSocket socket,
        Task attached,
        CancellationToken cancellationToken)
    {
        var shutdown = connection.ShutdownAsync(cancellationToken);
        var disposeJson = await socket.ReadSentAsync(cancellationToken);
        Assert.DoesNotContain("\"credential\"", disposeJson);
        var dispose = ReplEvalProtocol.Deserialize(disposeJson);
        Assert.Equal(ReplEvalMessageType.Dispose, dispose.Type);
        var identity = ReplEvalProtocol.ParsePayload(dispose, ReplEvalJsonContext.Default.ReplEvalIdentity);
        Assert.Null(identity.Credential);
        socket.QueueText(Envelope(
            ReplEvalMessageType.Result,
            dispose.CorrelationId,
            new ReplEvalResultPayload(),
            ReplEvalJsonContext.Default.ReplEvalResultPayload));
        socket.QueueClose();
        await shutdown.WaitAsync(cancellationToken);
        await attached.WaitAsync(cancellationToken);
    }

    private static string Envelope<T>(
        string type,
        string correlationId,
        T payload,
        JsonTypeInfo<T> typeInfo)
    {
        var envelope = new ReplEvalEnvelope(
            ReplEvalProtocol.Version,
            type,
            correlationId,
            ReplEvalProtocol.Payload(payload, typeInfo));
        return Encoding.UTF8.GetString(ReplEvalProtocol.Serialize(envelope));
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken) =>
        TestDeadlines.Create(cancellationToken);
}
