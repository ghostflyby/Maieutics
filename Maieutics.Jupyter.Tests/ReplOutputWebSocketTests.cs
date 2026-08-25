using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.DenoRepl;
using Microsoft.AspNetCore.Http;

namespace Maieutics.Jupyter.Tests;

public sealed class ReplOutputWebSocketTests
{
    [Fact]
    public void ProtocolExposesItsDomainPathAndCeiling()
    {
        ReplOutputProtocol.Version.Should().Be(1);
        ReplOutputProtocol.OutputPath.Should().Be("/v1/repl/output/ws");
        ReplOutputProtocol.MaximumMessageBytes.Should().Be(64 * 1024 * 1024);
        ReplOutputProtocol.MaximumMessageBytes.Should().BeGreaterThan(1024 * 1024);
    }

    [Fact]
    public void StdoutAndStderrFramesRoundTrip()
    {
        var stdout = ReplOutputFrameReader.Decode(Frame.Stdout(7, "execution-1", "hello\n"));
        stdout.Should().Be(new ReplOutputConsoleFrame(7, "execution-1", "stdout", "hello\n"));

        var stderr = ReplOutputFrameReader.Decode(Frame.Stderr(8, "execution-2", "boom\n"));
        stderr.Should().Be(new ReplOutputConsoleFrame(8, "execution-2", "stderr", "boom\n"));
    }

    [Fact]
    public void DisplayFramesCarryStringMimeValuesVerbatim()
    {
        var frame = ReplOutputFrameReader.Decode(Frame.Display(
            3,
            "execution-1",
            """{"text/plain":"1 + 1 = 2","text/html":"<b>2</b>","application/vnd.vega.v5+json":{"width":200}}""",
            """{"isolated":false}""",
            "display-1",
            isUpdate: false));

        var display = frame.Should().BeOfType<ReplOutputDisplayFrame>().Which;
        display.Seq.Should().Be(3);
        display.ExecutionId.Should().Be("execution-1");
        display.Data["text/plain"].GetString().Should().Be("1 + 1 = 2");
        display.Data["text/html"].GetString().Should().Be("<b>2</b>");
        display.Data["application/vnd.vega.v5+json"].GetProperty("width").GetInt32().Should().Be(200);
        display.Metadata["isolated"].GetBoolean().Should().BeFalse();
        display.DisplayId.Should().Be("display-1");
        display.IsUpdate.Should().BeFalse();
        display.Buffers.Should().BeEmpty();
    }

    [Fact]
    public void DisplayFramesCarryBinaryBuffersAsNativeBytesViaPlaceholders()
    {
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01, 0x02 };
        var jpeg = new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0xff, 0xd9 };
        var frame = ReplOutputFrameReader.Decode(Frame.Display(
            4,
            "execution-2",
            """{"image/png":{"$buffer":0},"image/jpeg":{"$buffer":1},"text/plain":"two images"}""",
            "{}",
            displayId: "",
            isUpdate: true,
            png,
            jpeg));

        var display = frame.Should().BeOfType<ReplOutputDisplayFrame>().Which;
        display.IsUpdate.Should().BeTrue();
        display.ExecutionId.Should().Be("execution-2");
        display.Data["image/png"].GetProperty("$buffer").GetInt32().Should().Be(0);
        display.Data["image/jpeg"].GetProperty("$buffer").GetInt32().Should().Be(1);
        display.Data["text/plain"].GetString().Should().Be("two images");
        display.Buffers.Should().HaveCount(2);
        display.ResolveBuffer(0).Should().Equal(png);
        display.ResolveBuffer(1).Should().Equal(jpeg);
    }

    [Fact]
    public void DisplayFramesTolerateEmptyDisplayIdAndEmptyMetadata()
    {
        var frame = ReplOutputFrameReader.Decode(Frame.Display(
            9,
            "execution-3",
            """{"text/plain":"solo"}""",
            "{}",
            displayId: "",
            isUpdate: true));

        var display = frame.Should().BeOfType<ReplOutputDisplayFrame>().Which;
        display.DisplayId.Should().BeNull();
        display.Metadata.Should().BeEmpty();
        display.IsUpdate.Should().BeTrue();
    }

    [Fact]
    public void ClearOutputFramesRoundTripBothWaitValues()
    {
        ReplOutputFrameReader.Decode(Frame.Clear(2, "execution-1", wait: true))
            .Should().Be(new ReplOutputClearOutputFrame(2, "execution-1", true));
        ReplOutputFrameReader.Decode(Frame.Clear(3, "execution-1", wait: false))
            .Should().Be(new ReplOutputClearOutputFrame(3, "execution-1", false));
    }

    [Fact]
    public void TheFrameSequenceMustBeAPositiveSafeInteger()
    {
        var tooLarge = (long)(ReplOutputProtocol.MaximumSafeSequence + 1);
        foreach (var seq in new[] { 0L, -1L, tooLarge })
            FluentActions.Invoking(() => ReplOutputFrameReader.Decode(Frame.Stdout(seq, "e", "x")))
                .Should().Throw<ReplOutputProtocolException>().Which.Code.Should().Be("invalid_sequence");
    }

    [Fact]
    public void TheFrameExecutionIdMustBeNonEmpty()
    {
        FluentActions.Invoking(() => ReplOutputFrameReader.Decode(Frame.Stdout(1, "", "x")))
            .Should().Throw<ReplOutputProtocolException>().Which.Code.Should().Be("invalid_execution_id");
    }

    [Fact]
    public void UnknownFrameTypesFailWithATypedProtocolError()
    {
        var frame = Frame.Stdout(1, "e", "x");
        frame[0] = 4;
        FluentActions.Invoking(() => ReplOutputFrameReader.Decode(frame))
            .Should().Throw<ReplOutputProtocolException>().Which.Code.Should().Be("unknown_frame_type");
    }

    [Fact]
    public void TruncatedFramesFailWithATypedLengthError()
    {
        var frame = Frame.Display(
            1,
            "execution-1",
            """{"image/png":{"$buffer":0}}""",
            "{}",
            displayId: "",
            isUpdate: false,
            new byte[] { 1, 2, 3 });
        foreach (var length in new[] { 0, 10, frame.Length - 1 })
        {
            var truncated = frame.AsSpan(0, length).ToArray();
            FluentActions.Invoking(() => ReplOutputFrameReader.Decode(truncated))
                .Should().Throw<ReplOutputProtocolException>().Which.Code.Should().Be("invalid_frame");
        }
    }

    [Fact]
    public void OutOfRangeBufferReferencesFailWithATypedProtocolError()
    {
        // Hand-crafted display frame whose bundle references buffer index 1 while only one buffer
        // is present: header + bundle + metadata + displayId + flags + buffer.
        var executionId = Encoding.UTF8.GetBytes("execution-1");
        var bundle = Encoding.UTF8.GetBytes("""{"image/png":{"$buffer":1}}""");
        var metadata = Encoding.UTF8.GetBytes("{}");
        var buffer = new byte[] { 1, 2, 3 };
        var total = 1 + 8 + 2 + executionId.Length + 4 + bundle.Length + 4 +
                    metadata.Length + 2 + 2 + 1 + 4 + buffer.Length;
        var frame = new byte[total];
        var offset = 0;
        frame[offset++] = 2;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(offset, 8), 1);
        offset += 8;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset, 2), (ushort)executionId.Length);
        offset += 2;
        executionId.CopyTo(frame, offset);
        offset += executionId.Length;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset, 4), (uint)bundle.Length);
        offset += 4;
        bundle.CopyTo(frame, offset);
        offset += bundle.Length;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset, 4), (uint)metadata.Length);
        offset += 4;
        metadata.CopyTo(frame, offset);
        offset += metadata.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset, 2), 0);
        offset += 2;
        frame[offset++] = 0; // isUpdate = display
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset, 2), 1);
        offset += 2;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset, 4), (uint)buffer.Length);
        offset += 4;
        buffer.CopyTo(frame, offset);

        // The decode itself keeps the placeholder; the out-of-range reference is only observable
        // when a consumer resolves it (the collector's rebuild path).
        var display = ReplOutputFrameReader.Decode(frame).Should().BeOfType<ReplOutputDisplayFrame>().Which;
        FluentActions.Invoking(() => display.ResolveBuffer(1))
            .Should().Throw<ReplOutputProtocolException>().Which.Code.Should().Be("invalid_buffer_reference");
    }

    [Fact(Timeout = 30_000)]
    public async Task SequenceMustIncreaseStrictlyPerExecution()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var (host, context) = CreateAuthenticatedHost("session-1");
        await using (host)
        {
            var socket = new TestBinarySocket();
            socket.QueueText(Hello("session-1", 1));
            var connectionWait = host.WaitForConnectionAsync("session-1", 1, deadline.Token);
            var attached = host.AttachAsync(socket, 41, context, deadline.Token);
            var connection = await connectionWait;

            socket.QueueBinary(Frame.Stdout(1, "execution-1", "first"));
            socket.QueueBinary(Frame.Stdout(2, "execution-1", "second"));
            await using var events = connection.Events.GetAsyncEnumerator(deadline.Token);
            (await events.MoveNextAsync()).Should().BeTrue();
            (await events.MoveNextAsync()).Should().BeTrue();

            // A skipped sequence (3 -> 5) for the same execution terminates the connection.
            socket.QueueBinary(Frame.Stdout(5, "execution-1", "skip"));
            (await connection.Completion.Awaiting(static task => task).Should()
                    .ThrowAsync<ReplOutputProtocolException>())
                .Which.Code.Should().Be("sequence_mismatch");
            await attached.WaitAsync(deadline.Token);
            socket.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task SequencesAreIndependentAcrossExecutions()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var (host, context) = CreateAuthenticatedHost("session-1");
        await using (host)
        {
            var socket = new TestBinarySocket();
            socket.QueueText(Hello("session-1", 1));
            var connectionWait = host.WaitForConnectionAsync("session-1", 1, deadline.Token);
            var attached = host.AttachAsync(socket, 41, context, deadline.Token);
            var connection = await connectionWait;

            socket.QueueBinary(Frame.Stdout(1, "execution-1", "one"));
            socket.QueueBinary(Frame.Stdout(1, "execution-2", "two"));
            socket.QueueBinary(Frame.Stdout(2, "execution-1", "three"));
            socket.QueueBinary(Frame.Stdout(2, "execution-2", "four"));
            // End the stream so the enumeration terminates: the output endpoint is process -> host
            // only, and the connection completes when the peer closes the channel.
            socket.QueueClose();

            var seen = new List<ReplOutputFrame>();
            await foreach (var frame in connection.Events.WithCancellation(deadline.Token))
                seen.Add(frame);
            seen.Select(static frame => frame.Seq).Should().Equal(1, 1, 2, 2);
            await attached.WaitAsync(deadline.Token);
            socket.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task WaitForConnectionAsyncPublishesAndDisposeCompletes()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var (host, context) = CreateAuthenticatedHost("session-1");
        await using (host)
        {
            var socket = new TestBinarySocket();
            socket.QueueText(Hello("session-1", 1));
            var connectionWait = host.WaitForConnectionAsync("session-1", 1, deadline.Token);
            var attached = host.AttachAsync(socket, 41, context, deadline.Token);

            var connection = await connectionWait;
            connection.Should().NotBeNull();
            socket.QueueBinary(Frame.Stdout(1, "execution-1", "alive"));
            await using (var events = connection.Events.GetAsyncEnumerator(deadline.Token))
            {
                (await events.MoveNextAsync()).Should().BeTrue();
            }

            await host.DisposeAsync();
            await attached.WaitAsync(deadline.Token);
            socket.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task UnauthenticatedPeerIsRejected()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        await using var host = new ReplOutputWebSocketHost(
            new ReplControlSessionRegistry(),
            new ReplControlCredentialRegistry());
        var socket = new TestBinarySocket();
        socket.QueueText(Hello("unknown-session", 1));
        var attached = host.AttachAsync(socket, 0, new DefaultHttpContext(), deadline.Token);

        await socket.Closed.WaitAsync(deadline.Token);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        await attached.WaitAsync(deadline.Token);
        socket.Dispose();
    }

    /// <summary>Registers a session in the host's registry so a peer pid resolves to it, matching
    /// the production startup sequence (the kernel registers the pid before the child connects).</summary>
    private static (ReplOutputWebSocketHost Host, DefaultHttpContext Context) CreateAuthenticatedHost(
        string sessionId)
    {
        var registry = new ReplControlSessionRegistry();
        registry.Register(41, sessionId);
        return (new ReplOutputWebSocketHost(registry, new ReplControlCredentialRegistry()), new DefaultHttpContext());
    }

    /// <summary>The JSON hello frame the TS client sends first on the output endpoint, declaring
    /// the session and generation so the host keys the slot by (session, generation).</summary>
    private static string Hello(string sessionId, int generation)
    {
        return $$"""{"sessionId":"{{sessionId}}","generation":{{generation}}}""";
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    /// <summary>Encodes output frames byte-for-byte as the TS <c>encodeOutputFrame</c> does
    /// (all integers big-endian).</summary>
    private static class Frame
    {
        internal static byte[] Stdout(long seq, string executionId, string text)
        {
            return Console(0, seq, executionId, text);
        }

        internal static byte[] Stderr(long seq, string executionId, string text)
        {
            return Console(1, seq, executionId, text);
        }

        private static byte[] Console(byte type, long seq, string executionId, string text)
        {
            var executionIdBytes = Encoding.UTF8.GetBytes(executionId);
            var textBytes = Encoding.UTF8.GetBytes(text);
            var result = new byte[1 + 8 + 2 + executionIdBytes.Length + textBytes.Length];
            var offset = 0;
            result[offset++] = type;
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(offset, 8), (ulong)seq);
            offset += 8;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset, 2), (ushort)executionIdBytes.Length);
            offset += 2;
            executionIdBytes.CopyTo(result, offset);
            offset += executionIdBytes.Length;
            textBytes.CopyTo(result, offset);
            return result;
        }

        internal static byte[] Display(
            long seq,
            string executionId,
            string bundleJson,
            string metadataJson,
            string displayId,
            bool isUpdate,
            params byte[][] buffers)
        {
            var executionIdBytes = Encoding.UTF8.GetBytes(executionId);
            var bundle = Encoding.UTF8.GetBytes(bundleJson);
            var metadata = Encoding.UTF8.GetBytes(metadataJson);
            var displayIdBytes = Encoding.UTF8.GetBytes(displayId);
            var total = 1 + 8 + 2 + executionIdBytes.Length + 4 + bundle.Length + 4 + metadata.Length +
                        2 + displayIdBytes.Length + 1 + 2;
            foreach (var buffer in buffers) total += 4 + buffer.Length;
            var result = new byte[total];
            var offset = 0;
            result[offset++] = 2;
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(offset, 8), (ulong)seq);
            offset += 8;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset, 2), (ushort)executionIdBytes.Length);
            offset += 2;
            executionIdBytes.CopyTo(result, offset);
            offset += executionIdBytes.Length;
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset, 4), (uint)bundle.Length);
            offset += 4;
            bundle.CopyTo(result, offset);
            offset += bundle.Length;
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset, 4), (uint)metadata.Length);
            offset += 4;
            metadata.CopyTo(result, offset);
            offset += metadata.Length;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset, 2), (ushort)displayIdBytes.Length);
            offset += 2;
            displayIdBytes.CopyTo(result, offset);
            offset += displayIdBytes.Length;
            result[offset++] = (byte)(isUpdate ? 1 : 0);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset, 2), (ushort)buffers.Length);
            offset += 2;
            foreach (var buffer in buffers)
            {
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset, 4), (uint)buffer.Length);
                offset += 4;
                buffer.CopyTo(result, offset);
                offset += buffer.Length;
            }

            return result;
        }

        internal static byte[] Clear(long seq, string executionId, bool wait)
        {
            var executionIdBytes = Encoding.UTF8.GetBytes(executionId);
            var result = new byte[1 + 8 + 2 + executionIdBytes.Length + 1];
            var offset = 0;
            result[offset++] = 3;
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(offset, 8), (ulong)seq);
            offset += 8;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset, 2), (ushort)executionIdBytes.Length);
            offset += 2;
            executionIdBytes.CopyTo(result, offset);
            result[^1] = wait ? (byte)1 : (byte)0;
            return result;
        }
    }

    private sealed class TestBinarySocket : WebSocket
    {
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<InboundFrame> inbound = Channel.CreateUnbounded<InboundFrame>();
        private WebSocketCloseStatus? closeStatus;
        private string? closeStatusDescription;
        private WebSocketState state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => closeStatus;

        public override string? CloseStatusDescription => closeStatusDescription;

        public override WebSocketState State => state;

        public override string? SubProtocol => null;

        internal Task Closed => closed.Task;

        internal void QueueBinary(byte[] bytes)
        {
            inbound.Writer.TryWrite(new InboundFrame(bytes, false, false));
        }

        internal void QueueText(string text)
        {
            inbound.Writer.TryWrite(new InboundFrame(Encoding.UTF8.GetBytes(text), false, true));
        }

        internal void QueueClose()
        {
            inbound.Writer.TryWrite(new InboundFrame([], true, false));
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
            return new WebSocketReceiveResult(
                frame.Bytes.Length,
                frame.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                true);
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
            return new ValueWebSocketReceiveResult(
                frame.Bytes.Length,
                frame.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The REPL output endpoint is half-duplex; the host never sends.");
        }

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The REPL output endpoint is half-duplex; the host never sends.");
        }

        private sealed record InboundFrame(byte[] Bytes, bool IsClose, bool IsText);
    }
}
