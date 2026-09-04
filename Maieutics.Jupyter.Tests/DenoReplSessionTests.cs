using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.DenoRepl;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoReplSessionTests
{
    [Fact(Timeout = 15_000)]
    public async Task SameSessionSerializesWhileDifferentSessionsCanExecuteConcurrently()
    {
        var probe = new ConcurrencyProbe();
        var firstFactory = new ControlledFactory(() => new ControlledGeneration(probe));
        var secondFactory = new ControlledFactory(() => new ControlledGeneration(probe));
        var owner = AgentSessionId.Create();
        await using var firstSession = CreateSession(owner, "first", LongRunningOptions(), firstFactory);
        await using var secondSession = CreateSession(owner, "second", LongRunningOptions(), secondFactory);

        var first = firstSession.ExecuteAsync(
            "first",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var firstGeneration = await firstFactory.NextGenerationAsync(TestContext.Current.CancellationToken);
        var firstExecution = await firstGeneration.ConnectionImpl.NextExecutionAsync(
            TestContext.Current.CancellationToken);
        var queued = firstSession.ExecuteAsync(
            "queued",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);

        var parallel = secondSession.ExecuteAsync(
            "parallel",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var secondGeneration = await secondFactory.NextGenerationAsync(TestContext.Current.CancellationToken);
        var parallelExecution = await secondGeneration.ConnectionImpl.NextExecutionAsync(
            TestContext.Current.CancellationToken);
        probe.Active.Should().Be(2);
        probe.Maximum.Should().Be(2);

        firstExecution.CompleteResult(1);
        parallelExecution.CompleteResult(2);
        await Task.WhenAll(first, parallel);
        var queuedExecution = await firstGeneration.ConnectionImpl.NextExecutionAsync(
            TestContext.Current.CancellationToken);
        queuedExecution.CompleteResult(3);
        await queued;

        probe.Maximum.Should().Be(2);
        probe.Active.Should().Be(0);
    }

    [Theory(Timeout = 15_000)]
    [InlineData(true, "idle", 0)]
    [InlineData(false, "faulted", 1)]
    public async Task CancellationDrainsBeforeEscalatingToGenerationTermination(
        bool releaseOnCancel,
        string expectedState,
        int expectedTerminateCount)
    {
        var factory = new ControlledFactory(() => new ControlledGeneration
        {
            ConnectionImpl = { ReleaseOnCancel = releaseOnCancel }
        });
        await using var session = CreateSession(
            AgentSessionId.Create(),
            "cancel",
            LongRunningOptions(),
            factory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var execution = session.ExecuteAsync("wait", AgentToolCallId.Create(), cancellation.Token);
        var generation = await factory.NextGenerationAsync(TestContext.Current.CancellationToken);
        await generation.ConnectionImpl.NextExecutionAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await execution.Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        generation.ConnectionImpl.CancelCount.Should().Be(1);
        generation.TerminateCount.Should().Be(expectedTerminateCount);
        session.GetSnapshot().State.Should().Be(expectedState);
    }

    [Fact(Timeout = 15_000)]
    public async Task ExecutionTimeoutReturnsTypedFailureAfterCancellationDrain()
    {
        var factory = new ControlledFactory(() => new ControlledGeneration
        {
            ConnectionImpl = { ReleaseOnCancel = true }
        });
        await using var session = CreateSession(
            AgentSessionId.Create(),
            "timeout",
            LongRunningOptions(TimeSpan.FromMilliseconds(25)),
            factory);

        var execution = session.ExecuteAsync(
            "wait",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var generation = await factory.NextGenerationAsync(TestContext.Current.CancellationToken);
        await generation.ConnectionImpl.NextExecutionAsync(TestContext.Current.CancellationToken);

        var failure = await execution.Invoking(static task => task).Should().ThrowAsync<AgentToolException>();
        failure.Which.Code.Should().Be("repl_timeout");
        generation.ConnectionImpl.CancelCount.Should().Be(1);
        generation.TerminateCount.Should().Be(0);
        session.GetSnapshot().State.Should().Be("idle");
    }

    [Fact(Timeout = 15_000)]
    public async Task RestartWaitsForActiveExecutionThenDisposesOldGenerationAndStartsTheNext()
    {
        var factory = new ControlledFactory();
        await using var session = CreateSession(
            AgentSessionId.Create(),
            "restart",
            LongRunningOptions(),
            factory);
        var execution = session.ExecuteAsync(
            "wait",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var firstGeneration = await factory.NextGenerationAsync(TestContext.Current.CancellationToken);
        var controlled = await firstGeneration.ConnectionImpl.NextExecutionAsync(
            TestContext.Current.CancellationToken);

        var restart = session.RestartAsync(TestContext.Current.CancellationToken);
        restart.IsCompleted.Should().BeFalse();
        controlled.CompleteResult(42);

        await execution;
        var result = await restart;
        var secondGeneration = await factory.NextGenerationAsync(TestContext.Current.CancellationToken);
        result.Generation.Should().Be(2);
        result.State.Should().Be("idle");
        firstGeneration.DisposeCount.Should().Be(1);
        secondGeneration.Generation.Should().Be(2);
        factory.Starts.Select(static start => start.Generation).Should().Equal(1, 2);
    }

    [Fact(Timeout = 15_000)]
    public async Task ConcurrentDisposeCallsWaitForTheSameGenerationCleanup()
    {
        var generation = new ControlledGeneration { BlockDisposal = true };
        var factory = new ControlledFactory(() => generation);
        var session = CreateSession(
            AgentSessionId.Create(),
            "dispose",
            LongRunningOptions(),
            factory);

        await session.StartAsync(TestContext.Current.CancellationToken);
        var first = session.DisposeAsync().AsTask();
        await generation.DisposalStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = session.DisposeAsync().AsTask();
        second.IsCompleted.Should().BeFalse();

        generation.AllowDisposal.TrySetResult();
        await Task.WhenAll(first, second);
        generation.DisposeCount.Should().Be(1);
        session.GetSnapshot().State.Should().Be("closed");
    }

    private static DenoReplOptions LongRunningOptions(TimeSpan? executionTimeout = null)
    {
        return new DenoReplOptions
        {
            ExecutionTimeout = executionTimeout ?? TimeSpan.FromSeconds(5),
            InterruptGracePeriod = TimeSpan.FromSeconds(1)
        };
    }

    private static DenoReplSession CreateSession(
        AgentSessionId owner,
        string id,
        DenoReplOptions options,
        IDenoReplSessionFactory factory,
        IDenoReplPresentationRouter? presentationRouter = null)
    {
        return new DenoReplSession(
            owner,
            id,
            false,
            Directory.GetCurrentDirectory(),
            options,
            factory,
            presentationRouter ?? new ImmediatePresentationRouter(),
            NullLogger<DenoReplSession>.Instance);
    }

    internal sealed class ControlledFactory(Func<ControlledGeneration>? create = null) : IDenoReplSessionFactory
    {
        private readonly Func<ControlledGeneration> create = create ?? (static () => new ControlledGeneration());
        private readonly Channel<ControlledGeneration> started = Channel.CreateUnbounded<ControlledGeneration>();

        internal List<ControlledGeneration> Generations { get; } = [];

        internal List<(string WorkingDirectory, string SessionId, int Generation)> Starts { get; } = [];

        public Task<IDenoReplGeneration> StartAsync(
            string workingDirectory,
            string sessionId,
            int generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = create();
            value.Generation = generation;
            Generations.Add(value);
            Starts.Add((workingDirectory, sessionId, generation));
            started.Writer.TryWrite(value);
            return Task.FromResult<IDenoReplGeneration>(value);
        }

        internal ValueTask<ControlledGeneration> NextGenerationAsync(CancellationToken cancellationToken)
        {
            return started.Reader.ReadAsync(cancellationToken);
        }
    }

    internal sealed class ControlledGeneration : IDenoReplGeneration
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposalCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposeState;

        internal ControlledGeneration(ConcurrencyProbe? probe = null, ControlledOutputConnection? output = null)
        {
            ConnectionImpl = new ControlledConnection(probe);
            if (output is not null) OutputEvents = Task.FromResult<IAsyncEnumerable<ReplOutputFrame>>(output);
        }

        internal ControlledConnection ConnectionImpl { get; init; }

        /// <summary>Optional output frame stream. Null when the harness has no output endpoint;
        /// the collector then degrades to the eval control plane only.</summary>
        public Task<IAsyncEnumerable<ReplOutputFrame>>? OutputEvents { get; init; }

        public IDenoReplConnection Connection => ConnectionImpl;

        public Task Completion => completion.Task;

        public int? ExitCode { get; private set; }

        internal int Generation { get; set; }

        internal int ShutdownCount { get; private set; }

        internal int TerminateCount { get; private set; }

        internal int DisposeCount { get; private set; }

        internal bool BlockDisposal { get; set; }

        internal TaskCompletionSource DisposalStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowDisposal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShutdownCount++;
            ConnectionImpl.CompleteConnection();
            completion.TrySetResult();
            return Task.CompletedTask;
        }

        public Task TerminateAsync()
        {
            TerminateCount++;
            ConnectionImpl.TerminateExecutions();
            ConnectionImpl.CompleteConnection();
            completion.TrySetResult();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref disposeState, 1, 0) == 0)
            {
                try
                {
                    DisposeCount++;
                    DisposalStarted.TrySetResult();
                    if (BlockDisposal)
                        await AllowDisposal.Task.ConfigureAwait(false);
                    ConnectionImpl.TerminateExecutions();
                    ConnectionImpl.CompleteConnection();
                    completion.TrySetResult();
                    disposalCompletion.TrySetResult();
                }
                catch (Exception exception)
                {
                    disposalCompletion.TrySetException(exception);
                }
            }

            await disposalCompletion.Task.ConfigureAwait(false);
        }

        internal void Complete(int? exitCode = 0)
        {
            ExitCode = exitCode;
            ConnectionImpl.CompleteConnection();
            completion.TrySetResult();
        }
    }

    internal sealed class ControlledConnection(ConcurrencyProbe? probe = null) : IDenoReplConnection
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock gate = new();
        private readonly List<ControlledEval> executions = [];
        private readonly Channel<ControlledEval> started = Channel.CreateUnbounded<ControlledEval>();
        private int nextExecutionId;

        public Task Completion => completion.Task;

        internal bool ReleaseOnCancel { get; set; }

        internal int CancelCount { get; private set; }

        internal int ShutdownCount { get; private set; }

        internal List<(ReplEvalInputRequestEvent Request, string Value)> InputReplies { get; } = [];

        public Task<ReplEvalExecution> ExecuteAsync(string code, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var execution = new ControlledEval($"execution-{Interlocked.Increment(ref nextExecutionId)}", probe);
            lock (gate)
            {
                executions.Add(execution);
            }
            started.Writer.TryWrite(execution);
            return Task.FromResult(execution.Execution);
        }

        public Task CancelAsync(string executionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancelCount++;
            if (ReleaseOnCancel) Find(executionId).CompleteCancelled();
            return Task.CompletedTask;
        }

        public Task ReplyInputAsync(
            ReplEvalInputRequestEvent request,
            string value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InputReplies.Add((request, value));
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShutdownCount++;
            TerminateExecutions();
            completion.TrySetResult();
            return Task.CompletedTask;
        }

        internal ValueTask<ControlledEval> NextExecutionAsync(CancellationToken cancellationToken)
        {
            return started.Reader.ReadAsync(cancellationToken);
        }

        internal void TerminateExecutions()
        {
            ControlledEval[] snapshot;
            lock (gate)
            {
                snapshot = executions.ToArray();
            }
            foreach (var execution in snapshot) execution.CompleteCancelled();
        }

        internal void CompleteConnection()
        {
            completion.TrySetResult();
        }

        private ControlledEval Find(string executionId)
        {
            lock (gate)
            {
                return executions.Single(execution => execution.Execution.ExecutionId == executionId);
            }
        }
    }

    internal sealed class ControlledEval
    {
        private readonly TaskCompletionSource<ReplEvalTerminal> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrencyProbe? probe;
        private readonly Channel<ReplEvalEvent> events = Channel.CreateUnbounded<ReplEvalEvent>();
        private int terminalState;

        internal ControlledEval(string executionId, ConcurrencyProbe? probe = null)
        {
            this.probe = probe;
            Execution = new ReplEvalExecution(executionId, events.Reader, completion.Task);
            probe?.Enter();
        }

        internal ReplEvalExecution Execution { get; }

        internal bool Publish(ReplEvalEvent replEvent)
        {
            return events.Writer.TryWrite(replEvent);
        }

        internal void CompleteResult(JsonElement? value = null)
        {
            Complete(new ReplEvalResultTerminal(Execution.ExecutionId, value));
        }

        internal void CompleteResult(int value)
        {
            CompleteResult(JsonSerializer.SerializeToElement(value));
        }

        internal void CompleteError(string code, string message, bool fatal = false)
        {
            Complete(new ReplEvalErrorTerminal(Execution.ExecutionId, code, message, fatal));
        }

        internal void CompleteCancelled()
        {
            Complete(new ReplEvalCancelledTerminal(Execution.ExecutionId));
        }

        private void Complete(ReplEvalTerminal terminal)
        {
            if (Interlocked.Exchange(ref terminalState, 1) != 0) return;
            events.Writer.TryComplete();
            probe?.Exit();
            completion.TrySetResult(terminal);
        }
    }

    internal sealed class ConcurrencyProbe
    {
        private int active;
        private int maximum;

        internal int Active => Volatile.Read(ref active);

        internal int Maximum => Volatile.Read(ref maximum);

        internal void Enter()
        {
            var current = Interlocked.Increment(ref active);
            var observed = Volatile.Read(ref maximum);
            while (current > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximum, current, observed);
                if (previous == observed) break;
                observed = previous;
            }
        }

        internal void Exit()
        {
            Interlocked.Decrement(ref active);
        }
    }

    internal sealed class ImmediatePresentationRouter(IDenoReplPresentationSink? currentSink = null)
        : IDenoReplPresentationRouter
    {
        private readonly IDenoReplPresentationSink sink = currentSink ?? new NoopPresentationSink();

        public ValueTask<IDenoReplPresentationSink> WaitForCallAsync(
            AgentSessionId sessionId,
            AgentToolCallId callId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(sink);
        }

        public bool TryGetCurrentSink(
            AgentSessionId sessionId,
            [NotNullWhen(true)] out IDenoReplPresentationSink? current)
        {
            current = sink;
            return true;
        }
    }

    private sealed class NoopPresentationSink : IDenoReplPresentationSink
    {
        public ValueTask DisplayAsync(
            ReplDisplayBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<ReplDisplayId> DisplayTrackedAsync(
            ReplDisplayBundle data,
            ReplDisplayId displayId,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken) => ValueTask.FromResult(displayId);

        public ValueTask UpdateDisplayAsync(
            ReplDisplayId displayId,
            ReplDisplayBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ClearOutputAsync(bool wait, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask WriteStderrAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask PublishErrorAsync(
            string name,
            string value,
            IReadOnlyList<string> traceback,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public Task<string> RequestInputAsync(
            string prompt,
            bool password,
            CancellationToken cancellationToken) => Task.FromResult(string.Empty);
    }

    /// <summary>A bounded output frame channel the collector drains. Frames must be published for
    /// an execution, then <see cref="End" /> closes the stream so the collector's terminal wait
    /// does not block on the read.</summary>
    internal sealed class ControlledOutputConnection : IAsyncEnumerable<ReplOutputFrame>
    {
        private readonly Channel<ReplOutputFrame> frames = Channel.CreateUnbounded<ReplOutputFrame>();

        internal void Publish(ReplOutputFrame frame)
        {
            frames.Writer.TryWrite(frame);
        }

        internal void End()
        {
            frames.Writer.TryComplete();
        }

        public IAsyncEnumerator<ReplOutputFrame> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            return frames.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
    }
}
