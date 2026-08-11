using System.Diagnostics.CodeAnalysis;
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
        var firstManager = new ControlledManager(probe, true);
        var secondManager = new ControlledManager(probe, true);
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
        await cancellation.CancelAsync();

        await execution.Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        manager.InterruptCount.Should().Be(1);
        manager.TerminateCount.Should().Be(expectedTerminateCount);
        session.GetSnapshot().State.Should().Be(expectedState);
    }

    [Fact(Timeout = 15_000)]
    public async Task ExecutionTimeoutReturnsTypedFailureAfterInterrupt()
    {
        var manager = new ControlledManager(new ConcurrencyProbe(), true);
        var options = LongRunningOptions(TimeSpan.FromMilliseconds(25));
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
        var manager = new ControlledManager(new ConcurrencyProbe(), true);
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

    [Fact(Timeout = 15_000)]
    public async Task StartupRetriesHiddenStdinProbeUntilInputRequestCanBeAnswered()
    {
        var manager = new ControlledManager(new ConcurrencyProbe(), true);
        manager.ClientImpl.SetReadinessFailures(1);
        await using var session = CreateSession(
            AgentSessionId.Create(),
            "stdin-readiness",
            LongRunningOptions(),
            manager);

        await session.StartAsync(TestContext.Current.CancellationToken);

        var probes = manager.ClientImpl.Requests
            .Where(request => request.Code.StartsWith(
                DenoReplSession.StdinReadinessProbeCodePrefix,
                StringComparison.Ordinal))
            .ToArray();
        probes.Should().HaveCount(2).And.OnlyContain(request =>
            request.Silent && !request.StoreHistory && request.AllowStdin);
        manager.ClientImpl.ReadinessReplyCount.Should().Be(1);
        manager.ClientImpl.StartedCount.Should().Be(0);
        session.GetSnapshot().State.Should().Be("idle");
    }

    [Fact(Timeout = 15_000)]
    public async Task LateOutputAlreadyIncludedInExecutionIsNotPresentedTwice()
    {
        var manager = new ControlledManager(new ConcurrencyProbe(), true);
        var presentedStderr = Channel.CreateUnbounded<string>();
        var presentation = new ImmediatePresentationRouter(new NoopPresentationSink(presentedStderr));
        await using var session = CreateSession(
            AgentSessionId.Create(),
            "late-output",
            LongRunningOptions(),
            manager,
            presentation);
        var resultTask = session.ExecuteAsync(
            "work",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var execution = await manager.ClientImpl.NextExecutionAsync(TestContext.Current.CancellationToken);
        var included = new JupyterStderr(execution.RequestId, "included");
        execution.AddOutput(included);
        manager.ClientImpl.PublishEvent(CreateLateOutput(included, true));
        manager.ClientImpl.PublishEvent(CreateLateOutput(
            new JupyterStderr(execution.RequestId, "after-completion")));
        execution.Release();

        var result = await resultTask;

        result.Outputs.Where(static output => output.Kind == "stderr")
            .Select(static output => output.Text).Should().Equal("included", "after-completion");
        var presented = new List<string>();
        while (presentedStderr.Reader.TryRead(out var text)) presented.Add(text);

        presented.Should().Equal("included", "after-completion");
    }

    [Fact(Timeout = 15_000)]
    public async Task LateOutputIsRequestCorrelatedAndBarrierDoesNotChangeUserHistory()
    {
        var manager = new ControlledManager(new ConcurrencyProbe(), true);
        await using var session = CreateSession(
            AgentSessionId.Create(),
            "late-correlation",
            LongRunningOptions(),
            manager);

        var firstTask = session.ExecuteAsync(
            "first",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var firstExecution = await manager.ClientImpl.NextExecutionAsync(TestContext.Current.CancellationToken);
        manager.ClientImpl.PublishEvent(CreateLateOutput(
            new JupyterStdout(firstExecution.RequestId, "first-late")));
        firstExecution.Release();
        var first = await firstTask;

        var secondTask = session.ExecuteAsync(
            "second",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var secondExecution = await manager.ClientImpl.NextExecutionAsync(TestContext.Current.CancellationToken);
        manager.ClientImpl.PublishEvent(CreateLateOutput(
            new JupyterStdout(firstExecution.RequestId, "stale-first")));
        manager.ClientImpl.PublishEvent(CreateLateOutput(
            new JupyterStdout(secondExecution.RequestId, "second-late")));
        secondExecution.Release();
        var second = await secondTask;

        first.ExecutionCount.Should().Be(1);
        first.Outputs.Should().ContainSingle().Which.Text.Should().Be("first-late");
        second.ExecutionCount.Should().Be(2);
        second.Outputs.Should().ContainSingle().Which.Text.Should().Be("second-late");

        var requests = manager.ClientImpl.Requests;
        requests.Should().HaveCount(5);
        requests[0].Should().Match<JupyterExecuteRequest>(request =>
            request.Silent && !request.StoreHistory && request.AllowStdin &&
            request.Code.StartsWith(DenoReplSession.StdinReadinessProbeCodePrefix, StringComparison.Ordinal));
        requests[1].Should().Match<JupyterExecuteRequest>(request =>
            !request.Silent && request.StoreHistory && request.Code == "first");
        requests[2].Should().Match<JupyterExecuteRequest>(request =>
            request.Silent && !request.StoreHistory && !request.AllowStdin);
        requests[3].Should().Match<JupyterExecuteRequest>(request =>
            !request.Silent && request.StoreHistory && request.Code == "second");
        requests[4].Should().Match<JupyterExecuteRequest>(request =>
            request.Silent && !request.StoreHistory && !request.AllowStdin);
        JsonSerializer.Serialize(second, DenoReplJsonSerializerContext.Default.DenoReplExecutionResult)
            .Should().NotContain(DenoReplSession.IopubBarrierMarkerPrefix)
            .And.NotContain(DenoReplSession.IopubBarrierMediaType);
    }

    [Fact(Timeout = 15_000)]
    public async Task BarrierMarkerInExecutionOutputCompletesWithoutLeakingMarker()
    {
        var manager = new ControlledManager(new ConcurrencyProbe(), true);
        manager.ClientImpl.BarrierMarkerInExecutionOutput = true;
        await using var session = CreateSession(
            AgentSessionId.Create(),
            "regular-barrier",
            LongRunningOptions(),
            manager);

        var resultTask = session.ExecuteAsync(
            "work",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var execution = await manager.ClientImpl.NextExecutionAsync(TestContext.Current.CancellationToken);
        execution.Release();

        var result = await resultTask;

        result.ExecutionCount.Should().Be(1);
        result.Outputs.Should().BeEmpty();
        JsonSerializer.Serialize(result, DenoReplJsonSerializerContext.Default.DenoReplExecutionResult)
            .Should().NotContain(DenoReplSession.IopubBarrierMarkerPrefix)
            .And.NotContain(DenoReplSession.IopubBarrierMediaType);
    }

    [Fact(Timeout = 15_000)]
    public async Task BarrierMarkerIncludedInExecutionLateEventCompletesWithoutLeakingMarker()
    {
        var manager = new ControlledManager(new ConcurrencyProbe(), true);
        manager.ClientImpl.BarrierMarkerInExecutionOutput = true;
        manager.ClientImpl.BarrierMarkerAsIncludedLateOutput = true;
        await using var session = CreateSession(
            AgentSessionId.Create(),
            "included-barrier",
            LongRunningOptions(),
            manager);

        var resultTask = session.ExecuteAsync(
            "work",
            AgentToolCallId.Create(),
            TestContext.Current.CancellationToken);
        var execution = await manager.ClientImpl.NextExecutionAsync(TestContext.Current.CancellationToken);
        execution.Release();

        var result = await resultTask;

        result.ExecutionCount.Should().Be(1);
        result.Outputs.Should().BeEmpty();
        JsonSerializer.Serialize(result, DenoReplJsonSerializerContext.Default.DenoReplExecutionResult)
            .Should().NotContain(DenoReplSession.IopubBarrierMarkerPrefix)
            .And.NotContain(DenoReplSession.IopubBarrierMediaType);
    }

    [Fact(Timeout = 15_000)]
    public async Task BarrierOutputDoesNotCompleteCaptureBeforeEarlierLateOutputEventIsConsumed()
    {
        var manager = new ControlledManager(new ConcurrencyProbe(), true);
        manager.ClientImpl.BarrierMarkerInExecutionOutput = true;
        await using var session = CreateSession(
            AgentSessionId.Create(),
            "barrier-event-order",
            LongRunningOptions(),
            manager);
        await session.StartAsync(TestContext.Current.CancellationToken);
        manager.ClientImpl.PauseEventConsumption();

        try
        {
            var resultTask = session.ExecuteAsync(
                "work",
                AgentToolCallId.Create(),
                TestContext.Current.CancellationToken);
            var execution = await manager.ClientImpl.NextExecutionAsync(TestContext.Current.CancellationToken);
            manager.ClientImpl.PublishEvent(CreateLateOutput(
                new JupyterStdout(execution.RequestId, "ordered-late")));
            execution.Release();

            await manager.ClientImpl.WaitForEventConsumptionBlockedAsync(TestContext.Current.CancellationToken);
            await manager.ClientImpl.WaitForBarrierOutputsDrainedAsync(TestContext.Current.CancellationToken);
            resultTask.IsCompleted.Should().BeFalse();

            manager.ClientImpl.ResumeEventConsumption();
            var result = await resultTask;

            result.Outputs.Should().ContainSingle().Which.Text.Should().Be("ordered-late");
            JsonSerializer.Serialize(result, DenoReplJsonSerializerContext.Default.DenoReplExecutionResult)
                .Should().NotContain(DenoReplSession.IopubBarrierMarkerPrefix)
                .And.NotContain(DenoReplSession.IopubBarrierMediaType);
        }
        finally
        {
            manager.ClientImpl.ResumeEventConsumption();
        }
    }

    private static DenoReplOptions LongRunningOptions(
        TimeSpan? executionTimeout = null,
        TimeSpan? interruptGracePeriod = null)
    {
        return new DenoReplOptions
        {
            ExecutionTimeout = executionTimeout ?? TimeSpan.FromSeconds(5),
            InterruptGracePeriod = interruptGracePeriod ?? TimeSpan.FromSeconds(1)
        };
    }

    private static JupyterLateOutput CreateLateOutput(
        JupyterOutput output,
        bool includedInExecution = false)
    {
        var message = JupyterMessage.Create(
            "stream",
            new JupyterStream("stdout", "wire"),
            JupyterJsonContext.Default.JupyterStream,
            new JupyterSessionIdentity("test", "tester"));
        return new JupyterLateOutput(output.RequestId, message)
        {
            Output = output,
            IncludedInExecution = includedInExecution
        };
    }

    private static DenoReplSession CreateSession(
        AgentSessionId owner,
        string id,
        DenoReplOptions options,
        ControlledManager manager,
        IDenoReplPresentationRouter? presentationRouter = null)
    {
        return new DenoReplSession(
            owner,
            id,
            false,
            Directory.GetCurrentDirectory(),
            options,
            new SingleManagerFactory(manager),
            presentationRouter ?? new ImmediatePresentationRouter(),
            new ReplControlSessionRegistry(),
            NullLogger<DenoReplSession>.Instance);
    }

    private sealed class SingleManagerFactory(ControlledManager manager) : IDenoReplSessionFactory
    {
        public Task<IJupyterKernelManager> StartAsync(
            string workingDirectory,
            string sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IJupyterKernelManager>(manager);
        }
    }

    private sealed class ControlledManager(ConcurrencyProbe probe, bool releaseOnInterrupt) : IJupyterKernelManager
    {
        public ControlledClient ClientImpl { get; } = new(probe);

        public int InterruptCount { get; private set; }

        public int RestartCount { get; private set; }

        public int ShutdownCount { get; private set; }

        public int TerminateCount { get; private set; }

        public IJupyterClient Client => ClientImpl;

        public int? ProcessId => null;

        public Task InterruptAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InterruptCount++;
            if (releaseOnInterrupt) ClientImpl.ReleaseAll();

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

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledClient(ConcurrencyProbe probe) : IJupyterClient
    {
        private readonly List<ControlledExecution> executions = [];

        private readonly TaskCompletionSource barrierOutputsDrained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Channel<JupyterClientEvent> events = Channel.CreateUnbounded<JupyterClientEvent>();
        private readonly Lock gate = new();
        private readonly List<JupyterExecuteRequest> requests = [];
        private readonly Channel<ControlledExecution> started = Channel.CreateUnbounded<ControlledExecution>();
        private TaskCompletionSource? eventConsumptionBlocked;
        private TaskCompletionSource? eventConsumptionRelease;
        private int executionCount;
        private int readinessFailuresRemaining;
        private int readinessReplyCount;
        private int startedCount;

        public bool BarrierMarkerInExecutionOutput { get; set; }

        public bool BarrierMarkerAsIncludedLateOutput { get; set; }

        public int ReadinessReplyCount => Volatile.Read(ref readinessReplyCount);

        public IReadOnlyList<JupyterExecuteRequest> Requests
        {
            get
            {
                lock (gate)
                {
                    return requests.ToArray();
                }
            }
        }

        public int StartedCount => Volatile.Read(ref startedCount);

        public void SetReadinessFailures(int value)
        {
            Volatile.Write(ref readinessFailuresRemaining, value);
        }

        public async IAsyncEnumerable<JupyterClientEvent> WatchEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new JupyterClientConnected();
            await foreach (var clientEvent in events.Reader.ReadAllAsync(cancellationToken))
            {
                Task? release;
                TaskCompletionSource? blocked;
                lock (gate)
                {
                    release = eventConsumptionRelease?.Task;
                    blocked = eventConsumptionBlocked;
                }

                if (release is not null)
                {
                    blocked?.TrySetResult();
                    await release.WaitAsync(cancellationToken);
                }

                yield return clientEvent;
            }
        }

        public Task<IJupyterExecution> ExecuteAsync(
            JupyterExecuteRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsyncCore(request, false, cancellationToken);
        }

        public Task<IJupyterExecution> ExecuteAsync(
            JupyterExecuteRequest request,
            JupyterExecutionOptions options,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsyncCore(request, options.ObserveOutputs, cancellationToken);
        }

        private Task<IJupyterExecution> ExecuteAsyncCore(
            JupyterExecuteRequest request,
            bool observeOutputs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetReadinessNonce(request.Code) is { } readinessNonce)
            {
                var inputReady = !TryConsumeReadinessFailure();
                lock (gate)
                {
                    requests.Add(request);
                }

                return Task.FromResult<IJupyterExecution>(new ReadinessExecution(
                    Volatile.Read(ref executionCount),
                    inputReady,
                    readinessNonce,
                    () => Interlocked.Increment(ref readinessReplyCount)));
            }

            var barrierMarker = TryGetBarrierMarker(request.Code);
            if (barrierMarker is not null)
            {
                var barrierRequestId = JupyterMessageId.Create();
                var barrierOutput = CreateBarrierOutput(barrierRequestId, barrierMarker);
                var barrier = new CompletedExecution(
                    barrierRequestId,
                    Volatile.Read(ref executionCount),
                    BarrierMarkerInExecutionOutput ? [barrierOutput] : [],
                    () => barrierOutputsDrained.TrySetResult());
                lock (gate)
                {
                    requests.Add(request);
                }

                var barrierEvent = CreateLateOutput(
                    barrierOutput,
                    BarrierMarkerInExecutionOutput);
                if (BarrierMarkerAsIncludedLateOutput)
                    PublishEvent(barrierEvent);
                else if (BarrierMarkerInExecutionOutput && observeOutputs)
                    PublishEvent(new JupyterExecutionOutputObserved(
                        barrierRequestId,
                        barrierEvent.Message,
                        barrierOutput));
                else
                    PublishEvent(barrierEvent);

                return Task.FromResult<IJupyterExecution>(barrier);
            }

            var execution = new ControlledExecution(probe, Interlocked.Increment(ref executionCount));
            lock (gate)
            {
                requests.Add(request);
                executions.Add(execution);
            }

            Interlocked.Increment(ref startedCount);
            started.Writer.TryWrite(execution).Should().BeTrue();
            return Task.FromResult<IJupyterExecution>(execution);
        }

        public Task<JupyterKernelInfo> GetKernelInfoAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<JupyterCompleteReply> CompleteAsync(
            JupyterCompleteRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<JupyterInspectReply> InspectAsync(
            JupyterInspectRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<JupyterIsCompleteReply> IsCompleteAsync(
            JupyterIsCompleteRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task<ControlledExecution> NextExecutionAsync(CancellationToken cancellationToken)
        {
            return started.Reader.ReadAsync(cancellationToken).AsTask();
        }

        public void PauseEventConsumption()
        {
            lock (gate)
            {
                if (eventConsumptionRelease is not null)
                    throw new InvalidOperationException("Event consumption is already paused.");

                eventConsumptionBlocked = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                eventConsumptionRelease = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task WaitForEventConsumptionBlockedAsync(CancellationToken cancellationToken)
        {
            lock (gate)
            {
                return (eventConsumptionBlocked?.Task ??
                        throw new InvalidOperationException("Event consumption is not paused."))
                    .WaitAsync(cancellationToken);
            }
        }

        public Task WaitForBarrierOutputsDrainedAsync(CancellationToken cancellationToken)
        {
            return barrierOutputsDrained.Task.WaitAsync(cancellationToken);
        }

        public void ResumeEventConsumption()
        {
            TaskCompletionSource? release;
            lock (gate)
            {
                release = eventConsumptionRelease;
                eventConsumptionRelease = null;
                eventConsumptionBlocked = null;
            }

            release?.TrySetResult();
        }

        public void PublishEvent(JupyterClientEvent clientEvent)
        {
            events.Writer.TryWrite(clientEvent).Should().BeTrue();
        }

        public void ReleaseAll()
        {
            ControlledExecution[] snapshot;
            lock (gate)
            {
                snapshot = executions.ToArray();
            }

            foreach (var execution in snapshot) execution.Release();
        }

        private static JupyterDisplayOutput CreateBarrierOutput(JupyterMessageId requestId, string marker)
        {
            var data = new Dictionary<string, JsonElement>
            {
                [DenoReplSession.IopubBarrierMediaType] = JsonSerializer.SerializeToElement(marker)
            };
            return new JupyterDisplayOutput(
                requestId,
                new MimeBundle(data),
                new Dictionary<string, JsonElement>());
        }

        private static string? TryGetBarrierMarker(string code)
        {
            var start = code.IndexOf(DenoReplSession.IopubBarrierMarkerPrefix, StringComparison.Ordinal);
            if (start < 0) return null;

            var end = start + DenoReplSession.IopubBarrierMarkerPrefix.Length;
            while (end < code.Length && char.IsAsciiHexDigit(code[end])) end++;

            return code[start..end];
        }

        private static string? TryGetReadinessNonce(string code)
        {
            if (!code.StartsWith(DenoReplSession.StdinReadinessProbeCodePrefix, StringComparison.Ordinal)) return null;

            var start = code.IndexOf(DenoReplSession.StdinReadinessNoncePrefix, StringComparison.Ordinal);
            if (start < 0) return null;

            var end = start + DenoReplSession.StdinReadinessNoncePrefix.Length;
            while (end < code.Length && char.IsAsciiHexDigit(code[end])) end++;
            return code[start..end];
        }

        private bool TryConsumeReadinessFailure()
        {
            while (true)
            {
                var remaining = Volatile.Read(ref readinessFailuresRemaining);
                if (remaining <= 0) return false;

                if (Interlocked.CompareExchange(ref readinessFailuresRemaining, remaining - 1, remaining) == remaining)
                    return true;
            }
        }
    }

    private sealed class ControlledExecution : IJupyterExecution
    {
        private readonly Lock gate = new();
        private readonly List<JupyterOutput> outputs = [];

        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledExecution(ConcurrencyProbe probe, int executionCount)
        {
            RequestId = JupyterMessageId.Create();
            probe.Enter();
            Completion = CompleteAsync(probe, executionCount);
        }

        public JupyterMessageId RequestId { get; }

        public IAsyncEnumerable<JupyterOutput> Outputs => ReadOutputsAsync();

        public Task<JupyterExecutionResult> Completion { get; }

        public Task ReplyInputAsync(
            JupyterInputRequest request,
            string value,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void Release()
        {
            release.TrySetResult();
        }

        public void AddOutput(JupyterOutput output)
        {
            lock (gate)
            {
                outputs.Add(output);
            }
        }

        private async IAsyncEnumerable<JupyterOutput> ReadOutputsAsync()
        {
            await release.Task;
            JupyterOutput[] snapshot;
            lock (gate)
            {
                snapshot = outputs.ToArray();
            }

            foreach (var output in snapshot) yield return output;
        }

        private async Task<JupyterExecutionResult> CompleteAsync(ConcurrencyProbe probe, int executionCount)
        {
            try
            {
                await release.Task;
                var reply = new JupyterExecuteReply("ok", executionCount);
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

    private sealed class CompletedExecution(
        JupyterMessageId requestId,
        int executionCount,
        IReadOnlyList<JupyterOutput> outputs,
        Action outputsDrained) : IJupyterExecution
    {
        public JupyterMessageId RequestId { get; } = requestId;

        public IAsyncEnumerable<JupyterOutput> Outputs => ReadOutputsAsync();

        public Task<JupyterExecutionResult> Completion { get; } = Task.FromResult(CreateCompletion(executionCount));

        public Task ReplyInputAsync(
            JupyterInputRequest request,
            string value,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<JupyterOutput> ReadOutputsAsync()
        {
            await Task.CompletedTask;
            foreach (var output in outputs) yield return output;
            outputsDrained();
        }

        private static JupyterExecutionResult CreateCompletion(int count)
        {
            var reply = new JupyterExecuteReply("ok", count);
            return new JupyterExecutionResult(
                reply,
                JupyterMessage.Create(
                    "execute_reply",
                    reply,
                    JupyterJsonContext.Default.JupyterExecuteReply,
                    new JupyterSessionIdentity("test", "tester")));
        }
    }

    private sealed class ReadinessExecution(
        int executionCount,
        bool inputReady,
        string expectedNonce,
        Action inputReplied) : IJupyterExecution
    {
        public JupyterMessageId RequestId { get; } = JupyterMessageId.Create();

        public IAsyncEnumerable<JupyterOutput> Outputs => ReadOutputsAsync();

        public Task<JupyterExecutionResult> Completion { get; } =
            Task.FromResult(CreateCompletion(executionCount, inputReady ? "ok" : "error"));

        public Task ReplyInputAsync(
            JupyterInputRequest request,
            string value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value.Should().Be(expectedNonce);
            inputReplied();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<JupyterOutput> ReadOutputsAsync()
        {
            await Task.CompletedTask;
            if (inputReady)
            {
                yield return new JupyterInputRequest(
                    RequestId,
                    JupyterMessageId.Create(),
                    string.Empty,
                    false);
            }
            else
            {
                yield return new JupyterExecuteInputOutput(RequestId, "hidden readiness probe", executionCount);
                yield return new JupyterExecutionError(
                    RequestId,
                    "Error",
                    "stdin readiness nonce mismatch",
                    []);
            }
        }

        private static JupyterExecutionResult CreateCompletion(int count, string status)
        {
            var reply = new JupyterExecuteReply("ok", count);
            if (status != "ok") reply = new JupyterExecuteReply(status, count, ErrorName: "Error");
            return new JupyterExecutionResult(
                reply,
                JupyterMessage.Create(
                    "execute_reply",
                    reply,
                    JupyterJsonContext.Default.JupyterExecuteReply,
                    new JupyterSessionIdentity("test", "tester")));
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
                if (observed >= current ||
                    Interlocked.CompareExchange(ref maximum, current, observed) == observed) return;
            }
        }

        public void Exit()
        {
            Interlocked.Decrement(ref active);
        }
    }

    internal sealed class ImmediatePresentationRouter(IDenoReplPresentationSink? currentSink = null)
        : IDenoReplPresentationRouter
    {
        private static readonly IDenoReplPresentationSink DefaultSink = new NoopPresentationSink();

        public ValueTask<IDenoReplPresentationSink> WaitForCallAsync(
            AgentSessionId sessionId,
            AgentToolCallId callId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(currentSink ?? DefaultSink);
        }

        public bool TryGetCurrentSink(AgentSessionId sessionId, [NotNullWhen(true)] out IDenoReplPresentationSink? sink)
        {
            sink = currentSink;
            return sink is not null;
        }
    }

    private sealed class NoopPresentationSink(Channel<string>? stderr = null) : IDenoReplPresentationSink
    {
        public ValueTask DisplayAsync(
            MimeBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<JupyterDisplayId> DisplayTrackedAsync(
            MimeBundle data,
            JupyterDisplayId displayId,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(displayId);
        }

        public ValueTask UpdateDisplayAsync(
            JupyterDisplayId displayId,
            MimeBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearOutputAsync(bool wait, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteStderrAsync(string text, CancellationToken cancellationToken)
        {
            stderr?.Writer.TryWrite(text);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishErrorAsync(
            string name,
            string value,
            IReadOnlyList<string> traceback,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public Task<string> RequestInputAsync(
            string prompt,
            bool password,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Empty);
        }
    }
}