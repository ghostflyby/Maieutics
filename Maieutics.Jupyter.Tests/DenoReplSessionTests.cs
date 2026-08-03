using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.Execution;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoReplSessionTests
{
    [Fact(Timeout = 15_000)]
    public async Task SameSessionSerializesWhileDifferentSessionsCanExecuteConcurrently()
    {
        var probe = new ConcurrencyProbe();
        var firstManager = new ControlledManager(probe, releaseOnInterrupt: true);
        var secondManager = new ControlledManager(probe, releaseOnInterrupt: true);
        var options = LongRunningOptions();
        var owner = AgentSessionId.Create();
        await using var firstSession = CreateSession(owner, "first", options, firstManager);
        await using var secondSession = CreateSession(owner, "second", options, secondManager);

        var first = firstSession.ExecuteAsync(
            "first",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var firstExecution = await firstManager.ClientImpl.NextExecutionAsync(
            TestContext.Current.CancellationToken);
        var queued = firstSession.ExecuteAsync(
            "queued",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);

        firstManager.ClientImpl.StartedCount.Should().Be(1);

        var parallel = secondSession.ExecuteAsync(
            "parallel",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var parallelExecution = await secondManager.ClientImpl.NextExecutionAsync(
            TestContext.Current.CancellationToken);
        probe.Active.Should().Be(2);
        probe.Maximum.Should().Be(2);

        firstExecution.Release();
        parallelExecution.Release();
        await Task.WhenAll(first, parallel);
        var queuedExecution = await firstManager.ClientImpl.NextExecutionAsync(
            TestContext.Current.CancellationToken);
        queuedExecution.Release();
        await queued;

        probe.Maximum.Should().Be(2);
        probe.Active.Should().Be(0);
    }

    [Theory(Timeout = 15_000)]
    [InlineData(true, "idle", 0)]
    [InlineData(false, "faulted", 1)]
    public async Task CancellationInterruptsAndEscalatesOnlyWhenGracefulCompletionFails(
        bool releaseOnInterrupt,
        string expectedState,
        int expectedTerminateCount)
    {
        var manager = new ControlledManager(new ConcurrencyProbe(), releaseOnInterrupt);
        var options = LongRunningOptions(interruptGracePeriod: TimeSpan.FromMilliseconds(25));
        await using var session = CreateSession(AgentSessionId.Create(), "cancel", options, manager);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var execution = session.ExecuteAsync("wait", AgentToolCallId.Create(), cancellation.Token);
        await manager.ClientImpl.NextExecutionAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await execution.Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        manager.InterruptCount.Should().Be(1);
        manager.TerminateCount.Should().Be(expectedTerminateCount);
        session.GetSnapshot().State.Should().Be(expectedState);
    }

    [Fact(Timeout = 15_000)]
    public async Task ExecutionTimeoutReturnsTypedFailureAfterInterrupt()
    {
        var manager = new ControlledManager(new ConcurrencyProbe(), releaseOnInterrupt: true);
        var options = LongRunningOptions(executionTimeout: TimeSpan.FromMilliseconds(25));
        await using var session = CreateSession(AgentSessionId.Create(), "timeout", options, manager);

        var execution = session.ExecuteAsync(
            "wait",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        await manager.ClientImpl.NextExecutionAsync(TestContext.Current.CancellationToken);

        var failure = await execution.Invoking(static task => task).Should().ThrowAsync<AgentToolException>();
        failure.Which.Code.Should().Be("repl_timeout");
        manager.InterruptCount.Should().Be(1);
        manager.ShutdownCount.Should().Be(0);
        session.GetSnapshot().State.Should().Be("idle");
    }

    [Fact(Timeout = 15_000)]
    public async Task RestartWaitsForActiveExecutionAndThenAdvancesGeneration()
    {
        var manager = new ControlledManager(new ConcurrencyProbe(), releaseOnInterrupt: true);
        await using var session = CreateSession(
            AgentSessionId.Create(),
            "restart",
            LongRunningOptions(),
            manager);
        var execution = session.ExecuteAsync(
            "wait",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var controlled = await manager.ClientImpl.NextExecutionAsync(TestContext.Current.CancellationToken);

        var restart = session.RestartAsync(TestContext.Current.CancellationToken);
        restart.IsCompleted.Should().BeFalse();
        controlled.Release();

        await execution;
        var result = await restart;
        result.Generation.Should().Be(2);
        result.State.Should().Be("idle");
        manager.RestartCount.Should().Be(1);
    }

    private static DenoReplOptions LongRunningOptions(
        TimeSpan? executionTimeout = null,
        TimeSpan? interruptGracePeriod = null) => new()
    {
        ExecutionTimeout = executionTimeout ?? TimeSpan.FromSeconds(5),
        InterruptGracePeriod = interruptGracePeriod ?? TimeSpan.FromSeconds(1)
    };

    private static DenoReplSession CreateSession(
        AgentSessionId owner,
        string id,
        DenoReplOptions options,
        ControlledManager manager) => new(
        owner,
        id,
        isDefault: false,
        Directory.GetCurrentDirectory(),
        options,
        new SingleManagerFactory(manager),
        new ImmediatePresentationRouter(),
        new ReplControlSessionRegistry(),
        NullLogger<DenoReplSession>.Instance);

    private sealed class SingleManagerFactory(ControlledManager manager) : IDenoReplSessionFactory
    {
        public Task<IJupyterKernelManager> StartAsync(
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IJupyterKernelManager>(manager);
        }
    }

    private sealed class ControlledManager(ConcurrencyProbe probe, bool releaseOnInterrupt) : IJupyterKernelManager
    {
        public ControlledClient ClientImpl { get; } = new(probe);

        public IJupyterClient Client => ClientImpl;

        public int? ProcessId => null;

        public int InterruptCount { get; private set; }

        public int RestartCount { get; private set; }

        public int ShutdownCount { get; private set; }

        public int TerminateCount { get; private set; }

        public Task InterruptAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InterruptCount++;
            if (releaseOnInterrupt)
            {
                ClientImpl.ReleaseAll();
            }

            return Task.CompletedTask;
        }

        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestartCount++;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShutdownCount++;
            ClientImpl.ReleaseAll();
            return Task.CompletedTask;
        }

        public Task TerminateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminateCount++;
            ClientImpl.ReleaseAll();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledClient(ConcurrencyProbe probe) : IJupyterClient
    {
        private readonly Lock gate = new();
        private readonly List<ControlledExecution> executions = [];
        private readonly Channel<ControlledExecution> started = Channel.CreateUnbounded<ControlledExecution>();
        private int startedCount;

        public int StartedCount => Volatile.Read(ref startedCount);

        public async IAsyncEnumerable<JupyterClientEvent> WatchEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new JupyterClientConnected();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task<IJupyterExecution> ExecuteAsync(
            JupyterExecuteRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var execution = new ControlledExecution(probe);
            lock (gate)
            {
                executions.Add(execution);
            }

            Interlocked.Increment(ref startedCount);
            started.Writer.TryWrite(execution).Should().BeTrue();
            return Task.FromResult<IJupyterExecution>(execution);
        }

        public Task<ControlledExecution> NextExecutionAsync(CancellationToken cancellationToken) =>
            started.Reader.ReadAsync(cancellationToken).AsTask();

        public void ReleaseAll()
        {
            ControlledExecution[] snapshot;
            lock (gate)
            {
                snapshot = executions.ToArray();
            }

            foreach (var execution in snapshot)
            {
                execution.Release();
            }
        }

        public Task<JupyterKernelInfo> GetKernelInfoAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JupyterCompleteReply> CompleteAsync(
            JupyterCompleteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JupyterInspectReply> InspectAsync(
            JupyterInspectRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JupyterIsCompleteReply> IsCompleteAsync(
            JupyterIsCompleteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledExecution : IJupyterExecution
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledExecution(ConcurrencyProbe probe)
        {
            RequestId = JupyterMessageId.Create();
            probe.Enter();
            Completion = CompleteAsync(probe);
        }

        public JupyterMessageId RequestId { get; }

        public IAsyncEnumerable<JupyterOutput> Outputs => ReadOutputsAsync();

        public Task<JupyterExecutionResult> Completion { get; }

        public void Release() => release.TrySetResult();

        public Task ReplyInputAsync(
            JupyterInputRequest request,
            string value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private async IAsyncEnumerable<JupyterOutput> ReadOutputsAsync()
        {
            await release.Task;
            yield break;
        }

        private async Task<JupyterExecutionResult> CompleteAsync(ConcurrencyProbe probe)
        {
            try
            {
                await release.Task;
                var reply = new JupyterExecuteReply("ok", 1);
                return new JupyterExecutionResult(
                    reply,
                    JupyterMessage.Create(
                        "execute_reply",
                        reply,
                        JupyterJsonContext.Default.JupyterExecuteReply,
                        new JupyterSessionIdentity("test", "tester")));
            }
            finally
            {
                probe.Exit();
            }
        }
    }

    private sealed class ConcurrencyProbe
    {
        private int active;
        private int maximum;

        public int Active => Volatile.Read(ref active);

        public int Maximum => Volatile.Read(ref maximum);

        public void Enter()
        {
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var observed = Volatile.Read(ref maximum);
                if (observed >= current || Interlocked.CompareExchange(ref maximum, current, observed) == observed)
                {
                    return;
                }
            }
        }

        public void Exit() => Interlocked.Decrement(ref active);
    }

    private sealed class ImmediatePresentationRouter : IDenoReplPresentationRouter
    {
        private static readonly IDenoReplPresentationSink Sink = new NoopPresentationSink();

        public ValueTask<IDenoReplPresentationSink> WaitForCallAsync(
            AgentSessionId sessionId,
            AgentToolCallId callId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Sink);
        }

        public bool TryGetCurrentSink(AgentSessionId sessionId, out IDenoReplPresentationSink? sink)
        {
            sink = null;
            return false;
        }
    }

    private sealed class NoopPresentationSink : IDenoReplPresentationSink
    {
        public ValueTask DisplayAsync(
            MimeBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<JupyterDisplayId> DisplayTrackedAsync(
            MimeBundle data,
            JupyterDisplayId displayId,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken) => ValueTask.FromResult(displayId);

        public ValueTask UpdateDisplayAsync(
            JupyterDisplayId displayId,
            MimeBundle data,
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
}
