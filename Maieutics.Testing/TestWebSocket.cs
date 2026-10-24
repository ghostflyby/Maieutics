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
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<InboundFrame> _inbound = Channel.CreateUnbounded<InboundFrame>();
    private readonly Channel<SentFrame> _sent = Channel.CreateUnbounded<SentFrame>();
    private WebSocketCloseStatus? _negotiatedCloseStatus;
    private string? _closeStatusDescription;
    private WebSocketState _state = WebSocketState.Open;

    /// <summary>Completes when the socket is closed, aborted, or disposed.</summary>
    public Task Closed => _closed.Task;

    public override WebSocketState State => _state;

    public override WebSocketCloseStatus? CloseStatus => _negotiatedCloseStatus;

    public override string? CloseStatusDescription => _closeStatusDescription;

    public override string? SubProtocol => null;

    /// <summary>Queues one complete inbound text message.</summary>
    public void QueueText(string text)
    {
        _inbound.Writer.TryWrite(new InboundFrame(Encoding.UTF8.GetBytes(text), IsClose: false, IsText: true));
    }

    /// <summary>Queues one complete inbound binary message.</summary>
    public void QueueBinary(byte[] bytes)
    {
        _inbound.Writer.TryWrite(new InboundFrame(bytes, IsClose: false, IsText: false));
    }

    /// <summary>Queues the inbound close frame: the next receive reports it.</summary>
    public void QueueClose()
    {
        _inbound.Writer.TryWrite(new InboundFrame([], IsClose: true, IsText: false));
    }

    /// <summary>Reads the next text message the peer sent, in send order, decoded as a
    /// UTF-8 string.</summary>
    public async ValueTask<string> ReadSentAsync(CancellationToken cancellationToken)
    {
        var frame = await _sent.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(frame.Bytes);
    }

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        _closed.TrySetResult();
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
        _negotiatedCloseStatus = closeStatus;
        _closeStatusDescription = statusDescription;
        _state = WebSocketState.Closed;
        _closed.TrySetResult();
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _state = WebSocketState.Closed;
        _inbound.Writer.TryComplete();
        _sent.Writer.TryComplete();
        _closed.TrySetResult();
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
        CancellationToken cancellationToken)
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!endOfMessage)
            throw new InvalidOperationException("The test socket only accepts complete messages.");
        await _sent.Writer.WriteAsync(new SentFrame(messageType, buffer.ToArray()), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits for the next queued inbound frame; an empty queue waits for one to
    /// arrive or for the socket to close, and close wins with the close frame.</summary>
    private async Task<InboundFrame> ReadNextInboundAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_inbound.Reader.TryRead(out var frame)) return frame;

            var hasInbound = _inbound.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var winner = await Task.WhenAny(hasInbound, _closed.Task).ConfigureAwait(false);
            if (ReferenceEquals(winner, _closed.Task))
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
        _state = WebSocketState.CloseReceived;
        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
    }

    private static readonly InboundFrame CloseFrame = new([], IsClose: true, IsText: false);

    /// <summary>One queued inbound message, or the close frame.</summary>
    /// <param name="Bytes">The complete message payload.</param>
    /// <param name="IsClose">True when this frame is the close frame.</param>
    /// <param name="IsText">True for text, false for binary (ignored on close).</param>
    protected internal readonly record struct InboundFrame(byte[] Bytes, bool IsClose, bool IsText);

    /// <summary>One complete message the peer sent. The type is carried so captures
    /// mirror the wire's frame metadata even where assertions read only the bytes.</summary>
    /// <param name="Type">The message type.</param>
    /// <param name="Bytes">The complete message payload.</param>
    // ReSharper disable once NotAccessedPositionalProperty.Global
    public readonly record struct SentFrame(WebSocketMessageType Type, byte[] Bytes);
}
