using System.Threading.Channels;
using Maieutics.Agent;
using Microsoft.Extensions.Logging;

namespace Maieutics.Frontend;

/// <summary>
///     Owns one Agent run's frontend event stream: a single-consumer pump that drains
///     <see cref="IAgentRun.Events" />, appends every rendered frame to a bounded replay
///     buffer, and fans out to live WebSocket subscribers. The pump runs regardless of
///     connections, so a client that attaches late — or reconnects after a backpressure
///     disconnect — replays from <c>sinceSequence</c> (invariant 16: events are never
///     silently dropped; the only loss is explicit retention eviction, surfaced as a
///     <c>run.missing</c> frame). The pump disposes the run exactly once when the terminal
///     frame is published.
/// </summary>
internal sealed class FrontendRunStream : IAsyncDisposable
{
    private const int ReplayRetention = 4096;
    internal const int SubscriberQueueCapacity = 1024;
    private static readonly TimeSpan ShutdownCancelTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock gate = new();
    private readonly List<FrontendEventFrame> replay = [];
    private readonly List<Subscriber> subscribers = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly AgentSessionId sessionId;
    private readonly IAgentRun run;
    private readonly FrontendDenoReplPresentationRouter? presentationRouter;
    private readonly ILogger logger;
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task pump = Task.CompletedTask;
    private IAsyncDisposable? presentationScope;
    private long firstSequence;
    private int startState;
    private int disposeState;
    private bool completed;

    private FrontendRunStream(
        AgentSessionId sessionId,
        IAgentRun run,
        FrontendDenoReplPresentationRouter? presentationRouter,
        IAsyncDisposable? presentationScope,
        ILogger logger)
    {
        this.sessionId = sessionId;
        this.run = run;
        this.presentationRouter = presentationRouter;
        this.presentationScope = presentationScope;
        this.logger = logger;
    }

    /// <summary>Creates the stream for a run without starting its pump, so the presentation
    /// scope can attach to it before events flow.</summary>
    internal static FrontendRunStream Create(
        AgentSessionId sessionId,
        IAgentRun run,
        FrontendDenoReplPresentationRouter? presentationRouter,
        ILogger logger)
    {
        return new FrontendRunStream(sessionId, run, presentationRouter, null, logger);
    }

    /// <summary>Starts the pump. The stream owns the run's disposal and detaches the
    /// presentation scope once the terminal frame is published.</summary>
    internal void Start(IAsyncDisposable? presentationScope)
    {
        if (Interlocked.CompareExchange(ref startState, 1, 0) != 0)
            throw new InvalidOperationException("The frontend run stream is already started.");

        this.presentationScope = presentationScope;
        pump = Task.Run(RunPumpAsync);
    }

    /// <summary>Gets the owning run identifier.</summary>
    internal AgentRunId RunId => run.Id;

    internal IAgentRun Run => run;

    /// <summary>Waits for the run to terminate; failures and cancellation surface here.</summary>
    internal Task<AgentRunResult> Completion => run.Completion;

    /// <summary>Requests cooperative cancellation and waits for the run to terminate.</summary>
    internal Task CancelAsync(CancellationToken cancellationToken) => run.CancelAsync(cancellationToken);

    /// <summary>
    ///     Attaches a subscriber, returning the frames retained since
    ///     <paramref name="sinceSequence" /> and the live queue that continues from the
    ///     snapshot. Both are taken under one lock, so the concatenation of the snapshot and
    ///     the queue is exactly once and in order. When the requested sequence precedes
    ///     retained history the snapshot is a single <c>run.missing</c> frame instead of a
    ///     partial replay.
    /// </summary>
    internal (IReadOnlyList<FrontendEventFrame> Initial, Channel<FrontendEventFrame> Channel) Subscribe(
        long sinceSequence)
    {
        var channel = Channel.CreateBounded<FrontendEventFrame>(SubscriberQueueCapacity);
        var subscriber = new Subscriber(channel);
        lock (gate)
        {
            if (completed)
            {
                // The pump already published its terminal frame; complete the queue after the
                // snapshot so the subscriber's drain loop ends instead of waiting forever.
                subscribers.Add(subscriber);
                if (firstSequence > 0 && sinceSequence + 1 < firstSequence)
                {
                    channel.Writer.TryWrite(MissingFrame());
                }
                else
                {
                    foreach (var frame in replay)
                        if (IsReplayable(frame, sinceSequence))
                            channel.Writer.TryWrite(frame);
                }

                channel.Writer.TryComplete();
                return ([], channel);
            }

            subscribers.Add(subscriber);
            return (replay
                    .Where(frame => IsReplayable(frame, sinceSequence))
                    .ToArray(),
                channel);
        }
    }

    /// <summary>Removes a subscriber when its WebSocket closes.</summary>
    internal void Unsubscribe(Channel<FrontendEventFrame> channel)
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

    /// <summary>Publishes a REPL presentation frame that carries no run-local sequence.</summary>
    internal void PublishPresentation(
        string type,
        string? displayId,
        System.Text.Json.JsonElement data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(new FrontendEventFrame(type, DisplayId: displayId, Data: data));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            await disposal.Task.ConfigureAwait(false);
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        await disposal.Task.ConfigureAwait(false);
        lifetime.Dispose();
    }

    /// <summary>Cancels the run so shutdown does not wait on a provider stream.</summary>
    internal void BeginShutdown()
    {
        _ = lifetime.CancelAsync();
    }

    private async Task RunPumpAsync()
    {
        try
        {
            Publish(new FrontendEventFrame("run.started", RunId: run.Id.Value.ToString("N")));
            Publish(new FrontendEventFrame("run.status", State: "busy"));
            var truncated = false;
            await foreach (var agentEvent in run.Events
                               .WithCancellation(lifetime.Token)
                               .ConfigureAwait(false))
                switch (agentEvent)
                {
                    case AgentTextDelta { Text.Length: > 0 } delta:
                        Publish(new FrontendEventFrame(
                            "text.delta",
                            RunId: run.Id.Value.ToString("N"),
                            Sequence: delta.Sequence,
                            MessageId: delta.MessageId.Value.ToString("N"),
                            Text: delta.Text));
                        break;
                    case AgentMessageCompleted Message:
                        Publish(new FrontendEventFrame(
                            "message.completed",
                            RunId: run.Id.Value.ToString("N"),
                            Sequence: Message.Sequence,
                            MessageId: Message.AgentMessageId.Value.ToString("N"),
                            AgentMessage: FrontendTranscriptMapper.ToMessage(Message.Message)));
                        break;
                    case AgentToolStarted started:
                        Publish(new FrontendEventFrame(
                            "tool.started",
                            RunId: run.Id.Value.ToString("N"),
                            Sequence: started.Sequence,
                            CallId: started.CallId.Value.ToString("N"),
                            Tool: started.ToolName,
                            Arguments: started.Arguments));
                        presentationRouter?.OpenCall(sessionId, started.CallId);
                        break;
                    case AgentToolProgress progress:
                        Publish(new FrontendEventFrame(
                            "tool.progress",
                            RunId: run.Id.Value.ToString("N"),
                            Sequence: progress.Sequence,
                            CallId: progress.CallId.Value.ToString("N"),
                            Content: FrontendTranscriptMapper.ToProgressContent(progress.Content)));
                        break;
                    case AgentToolFinished finished:
                        Publish(new FrontendEventFrame(
                            "tool.finished",
                            RunId: run.Id.Value.ToString("N"),
                            Sequence: finished.Sequence,
                            CallId: finished.CallId.Value.ToString("N"),
                            Result: finished.Result));
                        break;
                    case AgentTurnTruncated turnTruncated:
                        truncated = true;
                        Publish(new FrontendEventFrame(
                            "turn.truncated",
                            RunId: run.Id.Value.ToString("N"),
                            Sequence: turnTruncated.Sequence));
                        break;
                }

            PublishTerminal(await run.Completion.ConfigureAwait(false), truncated);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            await CancelForShutdownAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            // The run itself was cancelled (frontend cancel endpoint); the completion task
            // already carried the failure, so only the pump's own observation path lands here.
            Publish(new FrontendEventFrame(
                "run.failed",
                RunId: run.Id.Value.ToString("N"),
                Code: "run_cancelled",
                Message: exception.Message));
            Publish(new FrontendEventFrame("run.status", State: "idle"));
            await ObserveCompletionAsync().ConfigureAwait(false);
        }
        catch (AgentException exception)
        {
            Publish(new FrontendEventFrame(
                "run.failed",
                RunId: run.Id.Value.ToString("N"),
                Code: FrontendErrors.MapAgentException(exception),
                Message: exception.Message));
            Publish(new FrontendEventFrame("run.status", State: "idle"));
            await ObserveCompletionAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The frontend event pump for run {RunId} failed unexpectedly.", run.Id);
            Publish(new FrontendEventFrame(
                "run.failed",
                RunId: run.Id.Value.ToString("N"),
                Code: "agent_error",
                Message: "The agent turn failed."));
            Publish(new FrontendEventFrame("run.status", State: "idle"));
            await ObserveCompletionAsync().ConfigureAwait(false);
        }
        finally
        {
            MarkCompleted();
            if (presentationScope is not null) await presentationScope.DisposeAsync().ConfigureAwait(false);
            disposal.TrySetResult();
        }
    }

    private async Task CancelForShutdownAsync()
    {
        Publish(new FrontendEventFrame(
            "run.failed",
            RunId: run.Id.Value.ToString("N"),
            Code: "run_cancelled",
            Message: "The host is shutting down."));
        Publish(new FrontendEventFrame("run.status", State: "idle"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            timeout.CancelAfter(ShutdownCancelTimeout);
            await run.CancelAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cancelling run {RunId} during shutdown did not settle.", run.Id);
        }

        await ObserveCompletionAsync().ConfigureAwait(false);
    }

    private async Task ObserveCompletionAsync()
    {
        try
        {
            await run.Completion.ConfigureAwait(false);
        }
        catch
        {
            // The terminal frame already carries the failure; this only observes the task.
        }
    }

    private void PublishTerminal(AgentRunResult result, bool truncated)
    {
        Publish(new FrontendEventFrame(
            "run.completed",
            RunId: run.Id.Value.ToString("N"),
            Truncated: result.Truncated || truncated));
        Publish(new FrontendEventFrame("run.status", State: "idle"));
    }

    private FrontendEventFrame MissingFrame()
    {
        return new FrontendEventFrame("run.missing", RunId: run.Id.Value.ToString("N"));
    }

    private static bool IsReplayable(FrontendEventFrame frame, long sinceSequence)
    {
        // Sequenced frames replay by sequence; run lifecycle announcements (started/busy) do
        // not — a reconnecting client already implies the run is in flight. Terminal frames
        // carry no sequence and must always reach the client.
        if (frame.Sequence is { } sequence) return sequence > sinceSequence;

        return frame.Type is "run.completed" or "run.failed" or "run.missing";
    }

    private void MarkCompleted()
    {
        lock (gate)
        {
            completed = true;
            // Complete live queues so subscriber loops advance to the next run; late
            // subscribers still replay the buffer through Subscribe.
            foreach (var subscriber in subscribers) subscriber.Channel.Writer.TryComplete();

            subscribers.Clear();
        }
    }

    private void Publish(FrontendEventFrame frame)
    {
        lock (gate)
        {
            if (frame.Sequence is { } sequence)
                firstSequence = firstSequence == 0 ? sequence : Math.Min(firstSequence, sequence);

            replay.Add(frame);
            if (replay.Count > ReplayRetention) replay.RemoveAt(0);

            foreach (var subscriber in subscribers)
                if (!subscriber.Channel.Writer.TryWrite(frame))
                    subscriber.Faulted = true;
        }

        // Close overflowing subscribers outside the lock: backpressure disconnects instead of
        // dropping frames, and the client reconnects with its last observed sequence. The
        // queue completes with a marker exception after its buffered frames, so the handler
        // closes the socket as a backpressure disconnect rather than a normal run end.
        List<Subscriber>? overflowing = null;
        lock (gate)
        {
            for (var index = subscribers.Count - 1; index >= 0; index--)
                if (subscribers[index].Faulted)
                {
                    overflowing ??= [];
                    overflowing.Add(subscribers[index]);
                    subscribers[index].Channel.Writer.TryComplete(
                        new FrontendBackpressureException("The frontend subscriber queue overflowed."));
                    subscribers.RemoveAt(index);
                }
        }

        if (overflowing is not null)
            logger.LogWarning(
                "Disconnected {Count} frontend subscriber(s) of run {RunId} for backpressure.",
                overflowing.Count,
                run.Id);
    }

    private sealed class Subscriber(Channel<FrontendEventFrame> channel)
    {
        internal Channel<FrontendEventFrame> Channel { get; } = channel;

        internal bool Faulted { get; set; }
    }

    /// <summary>Completes a subscriber's queue with this marker so its WebSocket handler can
    /// close the socket as a backpressure disconnect instead of a normal run end.</summary>
    internal sealed class FrontendBackpressureException : ChannelClosedException
    {
        internal FrontendBackpressureException(string message)
            : base(message)
        {
        }
    }
}

/// <summary>Keeps every run's frontend stream addressable while it is replayable.</summary>
internal sealed class FrontendRunRegistry
{
    private const int RetainedCompletedStreams = 16;

    private readonly Lock gate = new();
    private readonly Dictionary<AgentRunId, FrontendRunStream> streams = [];
    private readonly Queue<AgentRunId> completionOrder = [];

    /// <summary>Registers a running stream and prunes the oldest completed ones.</summary>
    internal void Add(FrontendRunStream stream)
    {
        lock (gate)
        {
            streams[stream.RunId] = stream;
        }

        // Watch completion outside the registry lock: the watch may complete synchronously
        // and re-enter the lock, and System.Threading.Lock is not reentrant.
        _ = TrackCompletionAsync(stream);
    }

    internal bool TryGet(AgentRunId runId, out FrontendRunStream? stream)
    {
        lock (gate)
        {
            return streams.TryGetValue(runId, out stream);
        }
    }

    private async Task TrackCompletionAsync(FrontendRunStream stream)
    {
        try
        {
            await stream.Completion.ConfigureAwait(false);
        }
        catch
        {
            // Completion failure is already surfaced to subscribers; pruning only observes.
        }

        List<FrontendRunStream>? evicted = null;
        lock (gate)
        {
            if (!streams.TryGetValue(stream.RunId, out var current) || !ReferenceEquals(current, stream)) return;

            completionOrder.Enqueue(stream.RunId);
            while (completionOrder.Count > RetainedCompletedStreams)
            {
                var oldest = completionOrder.Dequeue();
                if (streams.Remove(oldest, out var removed))
                {
                    evicted ??= [];
                    evicted.Add(removed);
                }
            }
        }

        // Disposal happens outside the lock: disposing a completed stream never blocks, but
        // the lock must not be held across awaits.
        if (evicted is not null)
            foreach (var removed in evicted)
                await removed.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Maps typed Agent failures onto stable frontend protocol error codes.</summary>
internal static class FrontendErrors
{
    internal const string Busy = "agent_busy";
    internal const string NotFound = "not_found";
    internal const string SessionNotActive = "session_not_active";
    internal const string InvalidRequest = "invalid_request";
    internal const string CommandError = "command_error";
    internal const string ConfigurationError = "agent_configuration_error";

    internal static string MapAgentException(AgentException exception)
    {
        return exception switch
        {
            AgentProviderException => "agent_provider_error",
            AgentInputLimitExceededException => "agent_input_too_large",
            AgentResponseLimitExceededException => "agent_response_too_large",
            AgentToolLimitExceededException => "agent_tool_limit_exceeded",
            AgentToolArgumentsException => "agent_tool_arguments_error",
            AgentToolInvocationException => "agent_tool_error",
            AgentTurnDurationExceededException => "agent_turn_duration_exceeded",
            AgentModelIterationLimitExceededException => "agent_model_iteration_limit",
            AgentModelCapabilityException => "agent_model_capability_error",
            AgentContentCompatibilityException => "agent_unsupported_response",
            AgentUnsupportedResponseException => "agent_unsupported_response",
            AgentTurnInProgressException => Busy,
            _ => "agent_error"
        };
    }
}
