using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Maieutics.Testing;

/// <summary>One in-memory fake <see cref="WebSocket" /> for the suite, unifying the
/// host-socket doubles: the sent-message capture channel, the queued inbound frames
/// (text, binary, close), and the close-parks-receive state machine. A receive on an empty queue waits for the
/// next inbound frame or the socket's close/dispose, whichever comes first; close wins
/// and yields the close frame.</summary>
public class TestWebSocket : WebSocket
{
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<InboundFrame> inbound = Channel.CreateUnbounded<InboundFrame>();
    private readonly Channel<SentFrame> sent = Channel.CreateUnbounded<SentFrame>();
    private WebSocketCloseStatus? closeStatus;
    private string? closeStatusDescription;
    private WebSocketState state = WebSocketState.Open;

    /// <summary>Completes when the socket is closed, aborted, or disposed.</summary>
    public Task Closed => closed.Task;

    public override WebSocketState State => state;

    public override WebSocketCloseStatus? CloseStatus => closeStatus;

    public override string? CloseStatusDescription => closeStatusDescription;

    public override string? SubProtocol => null;

    /// <summary>Queues one complete inbound text message.</summary>
    public void QueueText(string text)
    {
        inbound.Writer.TryWrite(new InboundFrame(Encoding.UTF8.GetBytes(text), IsClose: false, IsText: true));
    }

    /// <summary>Queues one complete inbound binary message.</summary>
    public void QueueBinary(byte[] bytes)
    {
        inbound.Writer.TryWrite(new InboundFrame(bytes, IsClose: false, IsText: false));
    }

    /// <summary>Queues the inbound close frame: the next receive reports it.</summary>
    public void QueueClose()
    {
        inbound.Writer.TryWrite(new InboundFrame([], IsClose: true, IsText: false));
    }

    /// <summary>Reads the next text message the peer sent, in send order, decoded as a
    /// UTF-8 string.</summary>
    public async ValueTask<string> ReadSentAsync(CancellationToken cancellationToken)
    {
        var frame = await sent.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(frame.Bytes);
    }

    /// <summary>Reads the next message the peer sent with its type, in send order.</summary>
    public ValueTask<SentFrame> ReadSentFrameAsync(CancellationToken cancellationToken)
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
        var frame = await ReadNextInboundAsync(cancellationToken).ConfigureAwait(false);
        if (frame.IsClose) return CloseResult();
        frame.Bytes.AsMemory().CopyTo(buffer.AsMemory());
        return new WebSocketReceiveResult(frame.Bytes.Length, frame.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true);
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var frame = await ReadNextInboundAsync(cancellationToken).ConfigureAwait(false);
        if (frame.IsClose) return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        frame.Bytes.AsMemory().CopyTo(buffer);
        return new ValueWebSocketReceiveResult(frame.Bytes.Length, frame.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true);
    }

    public override async Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        if (buffer.Array is not { } array)
            throw new ArgumentException("The test WebSocket send buffer requires a backing array.", nameof(buffer));
        await SendAsync(
            array.AsMemory(buffer.Offset, buffer.Count),
            messageType,
            endOfMessage,
            cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!endOfMessage)
            throw new InvalidOperationException("The test socket only accepts complete messages.");
        await sent.Writer.WriteAsync(new SentFrame(messageType, buffer.ToArray()), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits for the next queued inbound frame; an empty queue waits for one to
    /// arrive or for the socket to close, and close wins with the close frame.</summary>
    private async Task<InboundFrame> ReadNextInboundAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (inbound.Reader.TryRead(out var frame)) return frame;

            var hasInbound = inbound.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var winner = await Task.WhenAny(hasInbound, closed.Task).ConfigureAwait(false);
            if (ReferenceEquals(winner, closed.Task))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CloseFrame;
            }

            if (!await hasInbound.ConfigureAwait(false))
            {
                // The writer completed (dispose) without another frame; the close signal
                // follows immediately, so fall through to the close frame on the next
                // pass or the closed wait above.
                return CloseFrame;
            }
        }
    }

    private WebSocketReceiveResult CloseResult()
    {
        state = WebSocketState.CloseReceived;
        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
    }

    private static readonly InboundFrame CloseFrame = new([], IsClose: true, IsText: false);

    /// <summary>One queued inbound message, or the close frame.</summary>
    /// <param name="Bytes">The complete message payload.</param>
    /// <param name="IsClose">True when this frame is the close frame.</param>
    /// <param name="IsText">True for text, false for binary (ignored on close).</param>
    protected internal readonly record struct InboundFrame(byte[] Bytes, bool IsClose, bool IsText);

    /// <summary>One complete message the peer sent.</summary>
    /// <param name="Type">The message type.</param>
    /// <param name="Bytes">The complete message payload.</param>
    public readonly record struct SentFrame(WebSocketMessageType Type, byte[] Bytes);
}
