using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Maieutics.Agent;
using Maieutics.Frontend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

public sealed class FrontendRunStreamTests
{
    [Fact(Timeout = 30_000)]
    public async Task InputRequestPresentationIsFlattenedOntoTheWireFrame()
    {
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            new NeverCompletingRun(),
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);

        stream.PublishPresentation(
            "input.request",
            displayId: null,
            JsonSerializer.SerializeToElement(
                new FrontendInputRequest("input-abc-1", "Name:", true),
                FrontendJsonContext.Default.FrontendInputRequest),
            TestContext.Current.CancellationToken);

        var (frames, channel) = stream.Subscribe(sinceSequence: 0);
        var frame = frames.Single(entry => entry.Type == "input.request");
        Assert.Equal("input-abc-1", frame.RequestId);
        Assert.Equal("Name:", frame.Prompt);
        Assert.True(frame.Password);
        Assert.Null(frame.Data);
        stream.Unsubscribe(channel);
        await Task.CompletedTask;
    }

    [Fact(Timeout = 30_000)]
    public async Task AnApprovalInputRequestKeepsItsDiscriminatorAndContextOnTheWire()
    {
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            new NeverCompletingRun(),
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);

        stream.PublishPresentation(
            "input.request",
            displayId: null,
            JsonSerializer.SerializeToElement(
                new FrontendInputRequest(
                    "approve-abc-1",
                    "Allow 'remove_file' to run?",
                    Password: false,
                    Schema: null,
                    ServerId: null,
                    Kind: FrontendInputKind.Approval,
                    Tool: "remove_file",
                    Reason: "It may destroy data."),
                FrontendJsonContext.Default.FrontendInputRequest),
            TestContext.Current.CancellationToken);

        var (frames, channel) = stream.Subscribe(sinceSequence: 0);
        var frame = frames.Single(entry => entry.Type == "input.request");

        // The lift is hand-written, so a forgotten field silently disappears from the wire
        // and the frontend can no longer tell an approval from a plain prompt.
        Assert.Equal(FrontendInputKind.Approval, frame.Kind);
        Assert.Equal("remove_file", frame.Tool);
        Assert.Equal("It may destroy data.", frame.Reason);
        Assert.Equal("approve-abc-1", frame.RequestId);
        stream.Unsubscribe(channel);
        await Task.CompletedTask;
    }

    [Fact(Timeout = 30_000)]
    public async Task APendingInputRequestReplaysForAReconnectingClient()
    {
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            new NeverCompletingRun(),
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);

        stream.PublishPresentation(
            "input.request",
            displayId: null,
            JsonSerializer.SerializeToElement(
                new FrontendInputRequest(
                    "approve-replay-1",
                    "Allow 'remove_file' to run?",
                    Password: false,
                    Schema: null,
                    ServerId: null,
                    Kind: FrontendInputKind.Approval,
                    Tool: "remove_file",
                    Reason: "It may destroy data."),
                FrontendJsonContext.Default.FrontendInputRequest),
            TestContext.Current.CancellationToken);

        // The reconnecting client resumes from the last sequence it observed. The request
        // carries no sequence of its own, so the replay decision is explicit: without it the
        // request is answerable in principle but invisible in practice, and the user can only
        // let it time out.
        var (frames, channel) = stream.Subscribe(sinceSequence: 1);
        var frame = frames.Single(entry => entry.Type == "input.request");
        Assert.Equal("approve-replay-1", frame.RequestId);
        Assert.Equal(FrontendInputKind.Approval, frame.Kind);
        Assert.Equal("remove_file", frame.Tool);
        Assert.Equal("It may destroy data.", frame.Reason);
        stream.Unsubscribe(channel);
        await Task.CompletedTask;
    }

    [Fact(Timeout = 30_000)]
    public async Task AnUnsequencedNonRequestFrameIsNotReplayed()
    {
        // The widening must be exactly "pending requests", not "every unsequenced frame":
        // replaying display noise would duplicate rendered output on every reconnect.
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            new NeverCompletingRun(),
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);

        stream.PublishPresentation(
            "repl.display",
            "display-1",
            JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                ["text/plain"] = JsonSerializer.SerializeToElement("hi")
            }),
            TestContext.Current.CancellationToken);

        var (frames, channel) = stream.Subscribe(sinceSequence: 1);
        Assert.DoesNotContain(frames, entry => entry.Type == "repl.display");
        stream.Unsubscribe(channel);
        await Task.CompletedTask;
    }

    [Fact]
    public void TheApprovalKindReachesTheWireAsAString()
    {
        // Every other assertion in this file round-trips through the same JSON context, so a
        // numeric enum encoding would pass all of them while no real client could read it.
        // This pins the raw JSON the extension actually parses.
        var frame = new FrontendEventFrame(
            "input.request",
            RequestId: "approve-wire-1",
            Prompt: "Allow 'remove_file' to run?",
            Password: false,
            Kind: FrontendInputKind.Approval,
            Tool: "remove_file",
            Reason: "It may destroy data.");

        var json = JsonSerializer.Serialize(frame, FrontendJsonContext.Default.FrontendEventFrame);

        Assert.Contains("\"kind\":\"approval\"", json);
        Assert.Contains("\"tool\":\"remove_file\"", json);
        Assert.Contains("\"reason\":\"It may destroy data.\"", json);
        // The numeric form is what the default source-generated converter emits; it must not
        // reappear.
        Assert.DoesNotContain("\"kind\":2", json);
    }

    [Fact(Timeout = 30_000)]
    public async Task OtherPresentationFramesKeepTheDisplayIdAndBundleData()
    {
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            new NeverCompletingRun(),
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);
        var bundle = JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>
            {
                ["text/plain"] = JsonSerializer.SerializeToElement("hi")
            });

        stream.PublishPresentation(
            "repl.display",
            "display-1",
            bundle,
            TestContext.Current.CancellationToken);

        var (frames, channel) = stream.Subscribe(sinceSequence: 0);
        var frame = frames.Single(entry => entry.Type == "repl.display");
        Assert.Equal("display-1", frame.DisplayId);
        Assert.NotNull(frame.Data);
        Assert.Equal("hi", frame.Data!.Value.GetProperty("text/plain").GetString());
        stream.Unsubscribe(channel);
        await Task.CompletedTask;
    }

    [Fact(Timeout = 30_000)]
    public async Task CompletedRunReplaysTheEntireBufferThroughTheSnapshot()
    {
        // A completed run holding more frames than the subscriber queue capacity must
        // deliver every retained frame: the replay travels as the initial snapshot instead
        // of being written into the bounded queue before any reader exists.
        var deltas = 1500;
        var run = ScriptedRun.Deltas(deltas);
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            run,
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);
        stream.Start(null);

        var (initial, channel) = await WaitForCompletedReplayAsync(stream, sinceSequence: 0, TestContext.Current.CancellationToken);
        Assert.True(channel.Reader.Completion.IsCompleted);

        // run.started + run.status(busy) + deltas + run.completed + run.status(idle).
        Assert.Equal(deltas + 4, initial.Count);
        Assert.True(initial.Count > FrontendRunStream.SubscriberQueueCapacity);
        Assert.Equal(Enumerable.Range(1, deltas).Select(index => (long)index), initial.Where(frame => frame.Sequence is { }).Select(frame => frame.Sequence!.Value));
        Assert.Equal("run.completed", initial[^2].Type);
        Assert.Equal("run.status", initial[^1].Type);

        await stream.DisposeAsync();
        await run.Completion;
    }

    [Fact(Timeout = 30_000)]
    public async Task ResumeBehindRetentionYieldsMissingFrameAndTheRetainedTail()
    {
        // After retention evicts the front of the buffer, a resuming client whose next
        // expected frame precedes the oldest retained sequence must receive run.missing
        // followed by the retained tail — never a silently truncated replay.
        var deltas = 4200;
        var run = ScriptedRun.Deltas(deltas);
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            run,
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);
        stream.Start(null);

        var (initial, channel) = await WaitForCompletedReplayAsync(stream, sinceSequence: 5, TestContext.Current.CancellationToken);
        Assert.True(channel.Reader.Completion.IsCompleted);

        Assert.Equal("run.missing", initial[0].Type);
        // The buffer retains the last ReplayRetention frames overall; the terminal pair
        // (run.completed plus the trailing run.status) occupies two of those slots, so the
        // oldest retained delta is deltas - (ReplayRetention - 2) + 1. The trailing
        // run.status carries no sequence and is not replayable for a resuming subscriber.
        var retainedDeltas = FrontendRunStream.ReplayRetention - 2;
        var oldestRetainedDelta = deltas - retainedDeltas + 1;
        Assert.Equal(1 + retainedDeltas + 1, initial.Count);
        Assert.Equal(oldestRetainedDelta, initial[1].Sequence);
        Assert.Equal(deltas, initial[^2].Sequence);
        Assert.Equal("run.completed", initial[^1].Type);
        Assert.Equal(initial.Where(frame => frame.Sequence is { }).Select(frame => frame.Sequence!.Value).Count(), initial.Where(frame => frame.Sequence is { }).Select(frame => frame.Sequence!.Value).Distinct().Count());

        await stream.DisposeAsync();
        await run.Completion;
    }

    [Fact(Timeout = 30_000)]
    public async Task LiveResumeBehindRetentionYieldsMissingFrameDuringTheRun()
    {
        // The live branch carries the same gap detection: resuming behind the retained
        // window while the run is still in flight opens with run.missing plus the tail.
        var deltas = 4200;
        var run = ScriptedRun.HeldDeltas(deltas);
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            run,
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);
        stream.Start(null);

        // Wait until the buffer has been filled to capacity AND the pump has published the
        // whole held delta range: "count reached the cap" alone also matches mid-fill (the
        // gap frame plus a full window appears as soon as the first eviction fires, while
        // the oldest retained delta is still drifting), which asserted a sequence the run
        // had not reached yet — a race that only slow runners lost. Each buffer append
        // completes the replay signal, so the wait is append-driven: one snapshot per
        // append, and the settled shape is asserted synchronously once observed.
        var oldestRetainedDelta = deltas - FrontendRunStream.ReplayRetention + 1;
        IReadOnlyList<FrontendEventFrame> snapshot;
        while (true)
        {
            var (initial, channel) = stream.Subscribe(sinceSequence: 5);
            snapshot = initial;
            var settled = initial.Count >= 1 + FrontendRunStream.ReplayRetention &&
                initial[1].Sequence == oldestRetainedDelta;
            stream.Unsubscribe(channel);
            if (settled) break;

            await stream.ReplayAppended.WaitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("run.missing", snapshot[0].Type);
        // Mid-run the buffer holds the last ReplayRetention deltas; the run is held before
        // any terminal frame publishes, so the oldest retained delta is
        // deltas - ReplayRetention + 1.
        Assert.Equal(deltas - FrontendRunStream.ReplayRetention + 1, snapshot[1].Sequence);

        run.Release();
        await WaitForCompletedReplayAsync(stream, sinceSequence: 0, TestContext.Current.CancellationToken);
        await stream.DisposeAsync();
        await run.Completion;
    }

    [Fact(Timeout = 30_000)]
    public async Task PumpFailureCancelsAndDisposesTheRunExactlyOnce()
    {
        // A pump that dies on an unanticipated exception must settle the run it owns: the
        // producer parks in the run's bounded events channel, so without a bounded cancel
        // the completion task, the stream disposal, and the session's turn gate never
        // release.
        var run = ScriptedRun.FaultingAfter(2);
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            run,
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);
        stream.Start(null);

        var (_, channel) = stream.Subscribe(sinceSequence: 0);
        await foreach (var frame in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            if (frame.Type == "run.failed")
                break;

        // Before the fix this disposal hung on the pump's completion: nothing settled the
        // run, so the pump never reached its disposal task. The pump must cancel and
        // dispose the run exactly once.
        await stream.DisposeAsync();
        Assert.Equal(1, run.CancelCount);
        Assert.Equal(1, run.DisposeCount);
        Assert.True(run.Completion.IsCompleted);
        stream.Unsubscribe(channel);
    }

    [Fact(Timeout = 30_000)]
    public async Task DisposingACompletedStreamDisposesTheRunExactlyOnce()
    {
        var run = ScriptedRun.Deltas(3);
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            run,
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);
        stream.Start(null);
        await WaitForCompletedReplayAsync(stream, sinceSequence: 0, TestContext.Current.CancellationToken);

        await stream.DisposeAsync();
        await stream.DisposeAsync();
        Assert.Equal(1, run.DisposeCount);
        Assert.Equal(0, run.CancelCount);
        await run.Completion;
    }

    /// <summary>Waits for the run's pump to reach its terminal frame and returns the
    /// completed-run replay snapshot taken at <paramref name="sinceSequence" />.</summary>
    private static async Task<(IReadOnlyList<FrontendEventFrame> Initial, Channel<FrontendEventFrame> Channel)>
        WaitForCompletedReplayAsync(
            FrontendRunStream stream,
            long sinceSequence,
            CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var (initial, channel) = stream.Subscribe(sinceSequence);
            if (channel.Reader.Completion.IsCompleted) return (initial, channel);

            stream.Unsubscribe(channel);
            await stream.ReplayAppended.WaitAsync(deadline.Token);
        }
    }

    /// <summary>
    ///     An Agent fault is observable only through the frontend: `Maieutics.Agent` emits no
    ///     log records by design (docs/logging.md), and the `run.failed` frame carries its
    ///     code only to clients still attached. Pin the log record, or a failed run becomes
    ///     an unexplained 4xx/timeout for whoever investigates.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnAgentFailureLogsItsProtocolCode()
    {
        var logger = new CapturingLogger();
        var run = ScriptedRun.FaultingWith(
            count: 2,
            new AgentContentCompatibilityException("text/unknown", new InvalidOperationException("boom")));
        var stream = FrontendRunStream.Create(AgentSessionId.Create(), run, presentationRouter: null, logger);
        stream.Start(null);

        var (_, channel) = stream.Subscribe(sinceSequence: 0);
        await foreach (var frame in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            if (frame.Type == "run.failed")
                break;
        stream.Unsubscribe(channel);
        await stream.DisposeAsync();

        var runId = run.Id.Value.ToString("N");
        var record = Assert.Single(logger.Records, entry => entry.Message.Contains("agent_unsupported_response", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains(runId, record.Message);
        Assert.IsType<AgentContentCompatibilityException>(record.Exception);
    }

    /// <summary>
    ///     When the pump stops with the run still incomplete, the settlement cancels it, so
    ///     the outcome a caller observes is that cancellation rather than the run's own
    ///     result. Without a record the two are indistinguishable in the logs.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnIncompletePumpSettlementLogsThatItCancelledTheRun()
    {
        var logger = new CapturingLogger();
        var run = ScriptedRun.FaultingAfter(2);
        var stream = FrontendRunStream.Create(AgentSessionId.Create(), run, presentationRouter: null, logger);
        stream.Start(null);

        var (_, channel) = stream.Subscribe(sinceSequence: 0);
        await foreach (var frame in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            if (frame.Type == "run.failed")
                break;
        stream.Unsubscribe(channel);
        await stream.DisposeAsync();

        var runId = run.Id.Value.ToString("N");
        Assert.Contains(logger.Records, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains(runId, StringComparison.Ordinal) &&
            entry.Message.Contains("still incomplete", StringComparison.Ordinal));
        // The settle line must state the outcome; the cancellation the sweep issues is what
        // the run's completion reports, so a reader can tell it apart from the run's own result.
        Assert.Contains(logger.Records, entry =>
            entry.Message.Contains(runId, StringComparison.Ordinal) &&
            entry.Message.Contains("settled with outcome", StringComparison.Ordinal));
    }

    /// <summary>Captures records so tests can assert on the diagnostic surface.</summary>
    private sealed class CapturingLogger : ILogger
    {
        // The pump thread appends while the test thread asserts; a plain list would
        // both tear enumerations and race the settlement records into the snapshot.
        private readonly Lock gate = new();
        private List<(LogLevel Level, string Message, Exception? Exception)> records = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Records
        {
            get
            {
                lock (gate)
                {
                    return records.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (gate)
            {
                records.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    /// <summary>A run whose pump is never started; presentation publishing only needs
    /// the identity and the replay buffer.</summary>
    private sealed class NeverCompletingRun : IAgentRun
    {
        public AgentRunId Id { get; } = AgentRunId.Create();

        public AgentSessionId SessionId { get; } = AgentSessionId.Create();

        public IAsyncEnumerable<AgentEvent> Events => AsyncEnumerableEmpty();

        // Never completed: the fake never starts a pump, so nobody observes it.
        public Task<AgentRunResult> Completion { get; } = new TaskCompletionSource<AgentRunResult>().Task;

        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<AgentEvent> AsyncEnumerableEmpty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>A scripted run whose events, completion, cancellation, and disposal the
    /// test drives directly. It models the real run's producer: the completion task settles
    /// when the event iterator finishes normally, or when the cancellation unblocks a
    /// producer parked in the bounded events channel — never on its own after a fault.</summary>
    private sealed class ScriptedRun : IAgentRun
    {
        private readonly Func<ScriptedRun, IAsyncEnumerable<AgentEvent>> events;
        private readonly TaskCompletionSource<AgentRunResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int cancelCount;
        private int disposeCount;

        private ScriptedRun(Func<ScriptedRun, IAsyncEnumerable<AgentEvent>> events) => this.events = events;

        public AgentRunId Id { get; } = AgentRunId.Create();

        public AgentSessionId SessionId { get; } = AgentSessionId.Create();

        public IAsyncEnumerable<AgentEvent> Events => events(this);

        public Task<AgentRunResult> Completion => completion.Task;

        public int CancelCount => Volatile.Read(ref cancelCount);

        public int DisposeCount => Volatile.Read(ref disposeCount);

        /// <summary>Releases a run held mid-flight by <see cref="HeldDeltas" />.</summary>
        public void Release() => hold.TrySetResult();

        /// <summary>A run streaming <paramref name="count" /> text deltas and completing.</summary>
        public static ScriptedRun Deltas(int count)
        {
            return new ScriptedRun(run => Iterate(run, count, gate: null, faultAfter: null));
        }

        /// <summary>A run streaming <paramref name="count" /> deltas and then waiting on a
        /// hold gate before completing, so tests can inspect mid-run replay state.</summary>
        public static ScriptedRun HeldDeltas(int count)
        {
            return new ScriptedRun(run => Iterate(run, count, gate: run.hold, faultAfter: null));
        }

        /// <summary>A run that throws after <paramref name="count" /> deltas and never
        /// completes on its own, modeling a producer parked in the bounded event channel
        /// while the pump dies on an unanticipated failure.</summary>
        public static ScriptedRun FaultingAfter(int count)
        {
            return new ScriptedRun(run => Iterate(run, count, gate: null, faultAfter: count));
        }

        /// <summary>A run whose event stream faults with a typed <see cref="AgentException" />,
        /// modeling a run that fails with a protocol-visible cause.</summary>
        public static ScriptedRun FaultingWith(int count, Exception failure)
        {
            return new ScriptedRun(run => Iterate(run, count, gate: null, faultAfter: count, failure: failure));
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref cancelCount);
            // Mirrors the real run: cancellation unblocks the parked producer, whose
            // termination settles the completion task and releases the session's turn gate.
            await Task.Yield();
            completion.TrySetResult(BuildResult());
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCount);
            return ValueTask.CompletedTask;
        }

        private AgentRunResult BuildResult()
        {
            return new AgentRunResult(
                Id,
                new ChatMessage(ChatRole.User, "user"),
                new ChatMessage(ChatRole.Assistant, "answer"),
                new AgentTranscript(SessionId, 0, ImmutableArray<AgentTranscriptTurn>.Empty));
        }

        private static async IAsyncEnumerable<AgentEvent> Iterate(
            ScriptedRun run,
            int count,
            TaskCompletionSource? gate,
            int? faultAfter,
            Exception? failure = null)
        {
            var messageId = AgentMessageId.Create();
            for (var sequence = 1; sequence <= count; sequence++)
            {
                yield return new AgentTextDelta(run.Id, sequence, messageId, $"d{sequence}");
                if (faultAfter is { } fault && sequence == fault)
                    throw failure ?? new InvalidOperationException("The content mapping failed unexpectedly.");
            }

            if (gate is not null) await gate.Task.ConfigureAwait(false);
            run.completion.TrySetResult(run.BuildResult());
        }
    }
}
