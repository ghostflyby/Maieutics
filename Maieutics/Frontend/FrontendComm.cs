using System.Text.Json.Serialization;
using System.Threading.Channels;
using Maieutics.DenoRepl;

namespace Maieutics.Frontend;

/// <summary>
///     One session's comm plane (ADR 0024): the live-comm registry, a bounded replay
///     buffer of downlink frames with dense per-session sequences, and the fan-out to
///     live comms-socket subscribers. The plane exists regardless of connections, so a
///     late or reconnecting client resumes from <c>sinceSeq</c>; overflow disconnects a
///     subscriber instead of dropping frames (invariant 16). Widget state lives in the
///     session's REPL process, so the plane is discarded when the active session changes.
/// </summary>
internal sealed class FrontendCommStream
{
    /// <summary>Frame-count retention; the byte budget below usually binds first.</summary>
    private const int ReplayRetentionFrames = 4096;

    /// <summary>Byte retention for the replay buffer: comm frames can carry multi-megabyte
    /// widget buffers, so retention is budgeted by size as well as count.</summary>
    private const long ReplayRetentionBytes = 64L * 1024 * 1024;

    internal const int SubscriberQueueCapacity = 1024;

    private readonly Lock gate = new();
    private readonly List<CommRecord> replay = [];
    private readonly List<Subscriber> subscribers = [];
    private readonly Dictionary<string, string> liveCommTargets = new(StringComparer.Ordinal);
    private readonly Queue<string> liveCommOrder = [];
    private long replayBytes;
    private long nextSequence;
    private bool truncated;

    /// <summary>One replayed downlink frame with its session-local sequence and the
    /// encoded frame size the retention budget is accountable for.</summary>
    internal sealed record CommRecord(long Sequence, ReplCommMessage Message, long PayloadBytes);

    /// <summary>Accepts a downlink frame from the REPL, updates the registry for
    /// open/close, assigns the next sequence, and fans out to subscribers.</summary>
    internal void Publish(ReplCommMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (gate)
        {
            switch (message.Kind)
            {
                case ReplCommKind.Open:
                    if (!liveCommTargets.ContainsKey(message.CommId))
                    {
                        liveCommTargets[message.CommId] = message.TargetName ?? message.CommId;
                        liveCommOrder.Enqueue(message.CommId);
                    }

                    break;
                case ReplCommKind.Close:
                    ForgetComm(message.CommId);
                    break;
            }

            nextSequence++;
            var record = new CommRecord(
                nextSequence,
                message,
                ReplCommCodec.Encode(message).LongLength);
            replay.Add(record);
            replayBytes += record.PayloadBytes;
            while (replay.Count > ReplayRetentionFrames || replayBytes > ReplayRetentionBytes)
            {
                var oldest = replay[0];
                replayBytes -= oldest.PayloadBytes;
                replay.RemoveAt(0);
                // Once history is evicted, a resuming client can no longer rebuild full
                // state from replay alone; the sticky flag surfaces that in the hello.
                truncated = true;
            }

            foreach (var subscriber in subscribers)
                if (!subscriber.Channel.Writer.TryWrite(record))
                    subscriber.Faulted = true;
        }

        // Overflowing subscribers are completed with the marker outside the write lock,
        // mirroring the run stream: backpressure disconnects instead of dropping frames.
        lock (gate)
        {
            for (var index = subscribers.Count - 1; index >= 0; index--)
                if (subscribers[index].Faulted)
                {
                    subscribers[index].Channel.Writer.TryComplete(
                        new FrontendCommBackpressureException("The comms subscriber queue overflowed."));
                    subscribers.RemoveAt(index);
                }
        }
    }

    /// <summary>Removes a comm from the registry on a frontend-initiated close; the
    /// authoritative downlink close is idempotent with this.</summary>
    internal void CloseComm(string commId)
    {
        lock (gate)
        {
            ForgetComm(commId);
        }
    }

    /// <summary>
    ///     Attaches a subscriber, returning the retained records since
    ///     <paramref name="sinceSequence" />, the live queue continuing from the snapshot,
    ///     and whether the requested position precedes retained history (a gap the client
    ///     must surface instead of silently rendering).
    /// </summary>
    internal (IReadOnlyList<CommRecord> Initial, Channel<CommRecord> Channel, bool Truncated) Subscribe(
        long sinceSequence)
    {
        var channel = Channel.CreateBounded<CommRecord>(SubscriberQueueCapacity);
        lock (gate)
        {
            subscribers.Add(new Subscriber(channel));
            var initial = sinceSequence <= 0
                ? replay.ToArray()
                : replay.Where(record => record.Sequence > sinceSequence).ToArray();
            // A gap is real when the client is ahead of anything ever published, when
            // retention emptied the buffer, or when rotation removed exactly the frames the
            // client still needs. It must resync widget state instead of rendering silence.
            var hadGap = sinceSequence > 0 &&
                (replay.Count == 0 ||
                 sinceSequence >= nextSequence ||
                 replay[0].Sequence > sinceSequence + 1);
            return (initial, channel, truncated || hadGap);
        }
    }

    /// <summary>Removes a subscriber when its WebSocket closes.</summary>
    internal void Unsubscribe(Channel<CommRecord> channel)
    {
        lock (gate)
        {
            for (var index = subscribers.Count - 1; index >= 0; index--)
                if (ReferenceEquals(subscribers[index].Channel, channel))
                {
                    subscribers[index].Channel.Writer.TryComplete();
                    subscribers.RemoveAt(index);
                }
        }
    }

    /// <summary>Snapshot of currently open comms for the hello frame.</summary>
    internal IReadOnlyList<FrontendCommDescriptor> SnapshotLive()
    {
        lock (gate)
        {
            return liveCommOrder
                .Select(id => new FrontendCommDescriptor(id, liveCommTargets[id]))
                .ToArray();
        }
    }

    private void ForgetComm(string commId)
    {
        if (!liveCommTargets.Remove(commId)) return;

        var remaining = liveCommOrder.Where(id => id != commId).ToArray();
        liveCommOrder.Clear();
        foreach (var id in remaining) liveCommOrder.Enqueue(id);
    }

    private sealed class Subscriber(Channel<CommRecord> channel)
    {
        internal Channel<CommRecord> Channel { get; } = channel;

        internal bool Faulted { get; set; }
    }

    /// <summary>Completes a subscriber's queue with this marker so its WebSocket handler can
    /// close the socket as a backpressure disconnect.</summary>
    internal sealed class FrontendCommBackpressureException : ChannelClosedException
    {
        internal FrontendCommBackpressureException(string message)
            : base(message)
        {
        }
    }
}

/// <summary>One currently open comm as announced in the hello frame.</summary>
internal sealed record FrontendCommDescriptor(
    [property: JsonPropertyName("commId")] string CommId,
    [property: JsonPropertyName("targetName")] string? TargetName);

/// <summary>The comms WebSocket hello: live identities plus replay status.</summary>
internal sealed record FrontendCommHello(
    [property: JsonPropertyName("live")] IReadOnlyList<FrontendCommDescriptor> Live,
    [property: JsonPropertyName("replayed")] bool Replayed,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>
///     Routes comm traffic between the REPL control host and the frontend comms plane
///     (ADR 0024). The downlink arrives through the host's <c>commFrontendSink</c>
///     delegate; the uplink is pushed into the child by the injected delegate so this
///     namespace never references <c>Control</c> directly.
/// </summary>
internal sealed class FrontendCommRouter
{
    /// <summary>The comm protocol version advertised in capabilities.</summary>
    internal const int Version = 1;

    private const int RetainedSessionPlanes = 4;

    private readonly Func<string, ReplCommMessage, CancellationToken, ValueTask> pushToChild;
    private readonly Lock gate = new();
    private readonly Dictionary<string, FrontendCommStream> planes = new(StringComparer.Ordinal);
    private readonly Queue<string> planeOrder = [];

    public FrontendCommRouter(Func<string, ReplCommMessage, CancellationToken, ValueTask> pushToChild)
    {
        this.pushToChild = pushToChild ?? throw new ArgumentNullException(nameof(pushToChild));
    }

    /// <summary>Downlink: a REPL comm frame arrived for a session. Widget state belongs to
    /// the session's REPL, so a session switch discards the previous plane.</summary>
    internal ValueTask AcceptFromReplAsync(
        string sessionId,
        ReplCommMessage message,
        CancellationToken cancellationToken)
    {
        PlaneFor(sessionId).Publish(message);
        return ValueTask.CompletedTask;
    }

    /// <summary>Uplink: validates the frame against the plane's registry and pushes it to
    /// the session's REPL child. Rejections carry a stable code plus the comm id so the
    /// endpoint can answer with a typed comm.error frame.</summary>
    internal async ValueTask PushToReplAsync(
        string sessionId,
        ReplCommMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Kind == ReplCommKind.Open)
            throw new FrontendCommRejectException(
                FrontendErrors.InvalidRequest,
                message.CommId,
                "comm.open is REPL-originated only; the frontend cannot open comms.");

        var plane = PlaneFor(sessionId);
        if (plane.SnapshotLive().All(live => live.CommId != message.CommId))
            throw new FrontendCommRejectException(
                "comm_not_found",
                message.CommId,
                $"No open comm matches '{message.CommId}'.");

        try
        {
            await pushToChild(sessionId, message, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new FrontendCommRejectException(
                "repl_unavailable",
                message.CommId,
                exception.Message);
        }

        // Only forget the comm once the close actually reached the child; a failed
        // push leaves it live in the REPL and the registry must keep saying so.
        if (message.Kind == ReplCommKind.Close) plane.CloseComm(message.CommId);
    }

    /// <summary>Returns the session's plane, creating it and recycling the oldest planes
    /// when sessions rotate beyond the retained window.</summary>
    internal FrontendCommStream PlaneFor(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (gate)
        {
            if (planes.TryGetValue(sessionId, out var existing)) return existing;

            var plane = new FrontendCommStream();
            planes[sessionId] = plane;
            planeOrder.Enqueue(sessionId);
            while (planeOrder.Count > RetainedSessionPlanes)
            {
                var oldest = planeOrder.Dequeue();
                if (!string.Equals(oldest, sessionId, StringComparison.Ordinal)) planes.Remove(oldest);
            }

            return plane;
        }
    }
}

/// <summary>
///     The frontend-hop framing around the shared comm codec: an 8-byte big-endian
///     session-local sequence prepended to the codec frame so clients can resume with
///     <c>sinceSeq</c>. Uplink frames carry sequence 0; the server assigns ordering.
/// </summary>
internal static class FrontendCommEnvelope
{
    internal const int SequenceBytes = 8;

    internal static byte[] Encode(long sequence, ReplCommMessage message)
    {
        var frame = ReplCommCodec.Encode(message);
        var payload = new byte[SequenceBytes + frame.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(payload, (ulong)sequence);
        frame.CopyTo(payload, SequenceBytes);
        return payload;
    }

    internal static (long Sequence, ReplCommMessage Message) Decode(byte[] payload)
    {
        if (payload.Length < SequenceBytes)
            throw new InvalidDataException("The comm frame is missing its envelope sequence.");

        var sequence = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(payload);
        return (sequence, ReplCommCodec.Decode(payload[SequenceBytes..]));
    }
}

/// <summary>Typed uplink rejection rendered as a comm.error frame with the offending comm id.</summary>
internal sealed class FrontendCommRejectException(string code, string commId, string message)
    : Exception(message)
{
    internal string Code { get; } = code;

    internal string CommId { get; } = commId;
}
