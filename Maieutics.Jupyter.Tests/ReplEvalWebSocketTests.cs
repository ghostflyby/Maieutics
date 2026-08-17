using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.DenoRepl;

namespace Maieutics.Jupyter.Tests;

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

        decoded.Version.Should().Be(envelope.Version);
        decoded.Type.Should().Be(envelope.Type);
        decoded.CorrelationId.Should().Be(envelope.CorrelationId);
        decoded.Payload?.GetRawText().Should().Be(envelope.Payload?.GetRawText());
        decodedPayload.Should().Be(payload);
        ReplEvalProtocol.WebSocketPath.Should().Be("/v1/repl/eval/ws");
    }

    [Fact(Timeout = 30_000)]
    public async Task OrderedExecutionRoutesInputAndDisposesGracefully()
    {
        using var deadline = CreateDeadline();
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
        readyJson.Should().NotContain("\"credential\"");
        var ready = ReplEvalProtocol.Deserialize(readyJson);
        ready.Type.Should().Be(ReplEvalMessageType.Ready);
        var connection = await connectionWait;
        var execution = await connection.ExecuteAsync("console.log('hello')", deadline.Token);
        var execute = ReplEvalProtocol.Deserialize(await socket.ReadSentAsync(deadline.Token));
        execute.Type.Should().Be(ReplEvalMessageType.Execute);
        execute.CorrelationId.Should().Be(execution.ExecutionId);

        await using var events = execution.Events.GetAsyncEnumerator(deadline.Token);
        socket.QueueText(Envelope(
            ReplEvalMessageType.Console,
            execution.ExecutionId,
            new ReplEvalConsolePayload(execution.ExecutionId, 1, "stdout", "hello"),
            ReplEvalJsonContext.Default.ReplEvalConsolePayload));
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Should().Be(new ReplEvalConsoleEvent(execution.ExecutionId, 1, "stdout", "hello"));

        using var displayDocument = JsonDocument.Parse("""{"text/plain":"42"}""");
        socket.QueueText(Envelope(
            ReplEvalMessageType.Display,
            execution.ExecutionId,
            new ReplEvalDisplayPayload(
                execution.ExecutionId,
                2,
                "display-1",
                displayDocument.RootElement.Clone()),
            ReplEvalJsonContext.Default.ReplEvalDisplayPayload));
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Should().BeOfType<ReplEvalDisplayEvent>().Which.Should().BeEquivalentTo(new
        {
            execution.ExecutionId,
            Sequence = 2L,
            IsUpdate = false,
            DisplayId = "display-1"
        });

        var request = new ReplEvalInputRequestPayload(
            execution.ExecutionId,
            3,
            "input-1",
            "Name?",
            false);
        socket.QueueText(Envelope(
            ReplEvalMessageType.InputRequest,
            request.RequestId,
            request,
            ReplEvalJsonContext.Default.ReplEvalInputRequestPayload));
        (await events.MoveNextAsync()).Should().BeTrue();
        var input = events.Current.Should().BeOfType<ReplEvalInputRequestEvent>().Which;
        await connection.ReplyInputAsync(input, "Ada", deadline.Token);
        var reply = ReplEvalProtocol.Deserialize(await socket.ReadSentAsync(deadline.Token));
        reply.Type.Should().Be(ReplEvalMessageType.InputReply);
        reply.CorrelationId.Should().Be(request.RequestId);

        using var resultDocument = JsonDocument.Parse("42");
        socket.QueueText(Envelope(
            ReplEvalMessageType.Result,
            execution.ExecutionId,
            new ReplEvalResultPayload(execution.ExecutionId, resultDocument.RootElement.Clone()),
            ReplEvalJsonContext.Default.ReplEvalResultPayload));
        (await execution.Completion.WaitAsync(deadline.Token)).Should().BeOfType<ReplEvalResultTerminal>();
        (await events.MoveNextAsync()).Should().BeFalse();

        await CompleteGracefulShutdownAsync(connection, socket, attached, deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task HelloCredentialAuthenticatesButIsNotEchoed()
    {
        using var deadline = CreateDeadline();
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
        readyJson.Should().NotContain("\"credential\"");
        var ready = ReplEvalProtocol.Deserialize(readyJson);
        var identity = ReplEvalProtocol.ParsePayload(ready, ReplEvalJsonContext.Default.ReplEvalIdentity);
        identity.Should().Be(new ReplEvalIdentity("session-1", 1));
        var connection = await connectionWait;

        await CompleteGracefulShutdownAsync(connection, socket, attached, deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task UnauthenticatedHelloIsRejected()
    {
        using var deadline = CreateDeadline();
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

        socket.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplicateTerminalTerminatesConnection()
    {
        using var deadline = CreateDeadline();
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

        (await connection.Completion.Awaiting(static task => task).Should()
                .ThrowAsync<ReplEvalProtocolException>())
            .Which.Code.Should().Be("duplicate_terminal");
        await attached.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancelWaitsForCancelledTerminalThenConnectionClosesGracefully()
    {
        using var deadline = CreateDeadline();
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
        cancelEnvelope.Type.Should().Be(ReplEvalMessageType.Cancel);
        socket.QueueText(Envelope(
            ReplEvalMessageType.Cancelled,
            execution.ExecutionId,
            new ReplEvalCancelledPayload(execution.ExecutionId),
            ReplEvalJsonContext.Default.ReplEvalCancelledPayload));

        await cancel;
        (await execution.Completion).Should().BeOfType<ReplEvalCancelledTerminal>();
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
        disposeJson.Should().NotContain("\"credential\"");
        var dispose = ReplEvalProtocol.Deserialize(disposeJson);
        dispose.Type.Should().Be(ReplEvalMessageType.Dispose);
        var identity = ReplEvalProtocol.ParsePayload(dispose, ReplEvalJsonContext.Default.ReplEvalIdentity);
        identity.Credential.Should().BeNull();
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

    private static CancellationTokenSource CreateDeadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    private sealed class TestWebSocket : WebSocket
    {
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<InboundFrame> inbound = Channel.CreateUnbounded<InboundFrame>();
        private readonly Channel<string> sent = Channel.CreateUnbounded<string>();
        private WebSocketCloseStatus? closeStatus;
        private string? closeStatusDescription;
        private WebSocketState state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => closeStatus;

        public override string? CloseStatusDescription => closeStatusDescription;

        public override WebSocketState State => state;

        public override string? SubProtocol => null;

        internal Task Closed => closed.Task;

        internal void QueueText(string text)
        {
            inbound.Writer.TryWrite(new InboundFrame(Encoding.UTF8.GetBytes(text), false));
        }

        internal void QueueClose()
        {
            inbound.Writer.TryWrite(new InboundFrame([], true));
        }

        internal ValueTask<string> ReadSentAsync(CancellationToken cancellationToken)
        {
            return sent.Reader.ReadAsync(cancellationToken);
        }

        public override void Abort()
        {
            state = WebSocketState.Aborted;
            closed.TrySetResult();
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.closeStatus = closeStatus;
            closeStatusDescription = statusDescription;
            state = WebSocketState.Closed;
            closed.TrySetResult();
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            state = WebSocketState.Closed;
            inbound.Writer.TryComplete();
            sent.Writer.TryComplete();
            closed.TrySetResult();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            var frame = await inbound.Reader.ReadAsync(cancellationToken);
            if (frame.IsClose)
            {
                state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            frame.Bytes.AsMemory().CopyTo(buffer.AsMemory());
            return new WebSocketReceiveResult(frame.Bytes.Length, WebSocketMessageType.Text, true);
        }

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var frame = await inbound.Reader.ReadAsync(cancellationToken);
            if (frame.IsClose)
            {
                state = WebSocketState.CloseReceived;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            frame.Bytes.AsMemory().CopyTo(buffer);
            return new ValueWebSocketReceiveResult(frame.Bytes.Length, WebSocketMessageType.Text, true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (buffer.Array is not { } array)
                throw new ArgumentException("The test WebSocket send buffer requires a backing array.", nameof(buffer));
            return SendAsync(
                array.AsMemory(buffer.Offset, buffer.Count),
                messageType,
                endOfMessage,
                cancellationToken).AsTask();
        }

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (messageType != WebSocketMessageType.Text || !endOfMessage)
                throw new InvalidOperationException("The test socket only accepts complete text messages.");
            return sent.Writer.WriteAsync(Encoding.UTF8.GetString(buffer.Span), cancellationToken);
        }

        private sealed record InboundFrame(byte[] Bytes, bool IsClose);
    }
}
