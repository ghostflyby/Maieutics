using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Frontend;
using Microsoft.Extensions.AI;
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
        frame.RequestId.Should().Be("input-abc-1");
        frame.Prompt.Should().Be("Name:");
        frame.Password.Should().BeTrue();
        frame.Data.Should().BeNull();
        stream.Unsubscribe(channel);
        await Task.CompletedTask;
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
        frame.DisplayId.Should().Be("display-1");
        frame.Data.Should().NotBeNull();
        frame.Data!.Value.GetProperty("text/plain").GetString().Should().Be("hi");
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
        channel.Reader.Completion.IsCompleted.Should().BeTrue();

        // run.started + run.status(busy) + deltas + run.completed + run.status(idle).
        initial.Count.Should().Be(deltas + 4);
        initial.Count.Should().BeGreaterThan(FrontendRunStream.SubscriberQueueCapacity);
        initial.Where(frame => frame.Sequence is { }).Select(frame => frame.Sequence!.Value)
            .Should().Equal(Enumerable.Range(1, deltas).Select(index => (long)index));
        initial[^2].Type.Should().Be("run.completed");
        initial[^1].Type.Should().Be("run.status");

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
        channel.Reader.Completion.IsCompleted.Should().BeTrue();

        initial[0].Type.Should().Be("run.missing");
        // The buffer retains the last ReplayRetention frames overall; the terminal pair
        // (run.completed plus the trailing run.status) occupies two of those slots, so the
        // oldest retained delta is deltas - (ReplayRetention - 2) + 1. The trailing
        // run.status carries no sequence and is not replayable for a resuming subscriber.
        var retainedDeltas = FrontendRunStream.ReplayRetention - 2;
        var oldestRetainedDelta = deltas - retainedDeltas + 1;
        initial.Count.Should().Be(1 + retainedDeltas + 1);
        initial[1].Sequence.Should().Be(oldestRetainedDelta);
        initial[^2].Sequence.Should().Be(deltas);
        initial[^1].Type.Should().Be("run.completed");
        initial.Where(frame => frame.Sequence is { }).Select(frame => frame.Sequence!.Value)
            .Should().OnlyHaveUniqueItems();

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

        // Poll until the buffer has been filled and its front evicted; the run is still in
        // flight, so every snapshot comes from the live branch (the queue never completed).
        IReadOnlyList<FrontendEventFrame> snapshot;
        while (true)
        {
            var (initial, channel) = stream.Subscribe(sinceSequence: 5);
            snapshot = initial;
            var filled = initial.Count >= 1 + FrontendRunStream.ReplayRetention;
            stream.Unsubscribe(channel);
            if (filled) break;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        snapshot[0].Type.Should().Be("run.missing");
        // Mid-run the buffer holds the last ReplayRetention deltas; the run is held before
        // any terminal frame publishes, so the oldest retained delta is
        // deltas - ReplayRetention + 1.
        snapshot[1].Sequence.Should().Be(deltas - FrontendRunStream.ReplayRetention + 1);

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
        run.CancelCount.Should().Be(1);
        run.DisposeCount.Should().Be(1);
        run.Completion.IsCompleted.Should().BeTrue();
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
        run.DisposeCount.Should().Be(1);
        run.CancelCount.Should().Be(0);
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
            await Task.Delay(10, deadline.Token);
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
            int? faultAfter)
        {
            var messageId = AgentMessageId.Create();
            for (var sequence = 1; sequence <= count; sequence++)
            {
                yield return new AgentTextDelta(run.Id, sequence, messageId, $"d{sequence}");
                if (faultAfter is { } fault && sequence == fault)
                    throw new InvalidOperationException("The content mapping failed unexpectedly.");
            }

            if (gate is not null) await gate.Task.ConfigureAwait(false);
            run.completion.TrySetResult(run.BuildResult());
        }
    }
}
