using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.Logging;

namespace Maieutics.Execution;

internal sealed class DenoReplSession : IAsyncDisposable
{
    internal const string IopubBarrierMarkerPrefix = "maieutics-repl-iopub-barrier-";
    internal const string IopubBarrierMediaType = "application/vnd.maieutics.repl-barrier+json";
    internal const string StdinReadinessProbeCodePrefix = "if (prompt('') !== ";
    internal const string StdinReadinessNoncePrefix = "maieutics-repl-stdin-readiness-";
    private const int LateOutputBufferCapacity = 256;
    private readonly ReplControlSessionRegistry controlRegistry;
    private readonly Dictionary<string, JupyterDisplayId> displayIds = new(StringComparer.Ordinal);

    private readonly TaskCompletionSource disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim executionGate = new(1, 1);
    private readonly IDenoReplSessionFactory factory;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ILogger<DenoReplSession> logger;
    private readonly DenoReplOptions options;
    private readonly IDenoReplPresentationRouter presentationRouter;
    private readonly Lock stateGate = new();
    private LateOutputCapture? activeLateOutputCapture;
    private int disposeState;
    private int generation = 1;
    private CancellationTokenSource? generationLifetime;
    private Task? generationLoop;
    private IJupyterKernelManager? manager;
    private int? registeredControlPid;
    private DenoReplSessionState state = DenoReplSessionState.Created;

    internal DenoReplSession(
        AgentSessionId ownerSessionId,
        string sessionId,
        bool isDefault,
        string workingDirectory,
        DenoReplOptions options,
        IDenoReplSessionFactory factory,
        IDenoReplPresentationRouter presentationRouter,
        ReplControlSessionRegistry controlRegistry,
        ILogger<DenoReplSession> logger)
    {
        OwnerSessionId = ownerSessionId;
        SessionId = sessionId;
        IsDefault = isDefault;
        WorkingDirectory = workingDirectory;
        this.options = options;
        this.factory = factory;
        this.presentationRouter = presentationRouter;
        this.controlRegistry = controlRegistry;
        this.logger = logger;
    }

    internal AgentSessionId OwnerSessionId { get; }

    internal string SessionId { get; }

    internal bool IsDefault { get; }

    internal string WorkingDirectory { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref disposeState, 1, 0) != 0)
        {
            await disposalCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
        try
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lifetime.Dispose();
            lifecycleGate.Dispose();
            executionGate.Dispose();
        }

        if (failure is null)
            disposalCompletion.TrySetResult();
        else
            disposalCompletion.TrySetException(failure);

        await disposalCompletion.Task.ConfigureAwait(false);
    }

    internal DenoReplSessionResult GetSnapshot()
    {
        lock (stateGate)
        {
            return new DenoReplSessionResult(
                SessionId,
                generation,
                ToWireState(state),
                WorkingDirectory,
                IsDefault);
        }
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = GetState();
            if (current is DenoReplSessionState.Idle or DenoReplSessionState.Busy) return;

            if (current == DenoReplSessionState.Faulted) throw CreateFaultedException();

            SetState(DenoReplSessionState.Starting);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime.Token);
            startup.CancelAfter(options.StartupTimeout);
            try
            {
                manager = await factory.StartAsync(
                    WorkingDirectory,
                    SessionId,
                    startup.Token).ConfigureAwait(false);
                await StartControlChannelAsync(startup.Token).ConfigureAwait(false);
                await EnsureStdinReadyAsync(
                    manager.Client,
                    startup.Token).ConfigureAwait(false);
                StartGenerationLoop(manager.Client);
                SetState(DenoReplSessionState.Idle);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(DenoReplSessionState.Faulted);
                throw;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                SetState(DenoReplSessionState.Faulted);
                throw new OperationCanceledException(lifetime.Token);
            }
            catch (OperationCanceledException exception) when (startup.IsCancellationRequested)
            {
                SetState(DenoReplSessionState.Faulted);
                logger.LogWarning(exception, "Deno REPL session {SessionId} startup timed out.", SessionId);
                throw new AgentToolException(
                    "repl_start_failed",
                    $"Deno REPL session '{SessionId}' could not be started before its startup timeout.");
            }
            catch (Exception exception)
            {
                SetState(DenoReplSessionState.Faulted);
                logger.LogWarning(exception, "Could not start Deno REPL session {SessionId}.", SessionId);
                throw new AgentToolException(
                    "repl_start_failed",
                    $"Deno REPL session '{SessionId}' could not be started: {GetSafeMessage(exception)}");
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    internal async Task<DenoReplExecutionResult> ExecuteAsync(
        string code,
        AgentToolCallId callId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (GetState() == DenoReplSessionState.Faulted) throw CreateFaultedException();

            var currentManager = manager ?? throw CreateFaultedException();
            var currentGeneration = GetGeneration();
            SetState(DenoReplSessionState.Busy);

            var sink = await presentationRouter.WaitForCallAsync(
                OwnerSessionId,
                callId,
                cancellationToken).ConfigureAwait(false);
            var capture = new LateOutputCapture(
                IopubBarrierMarkerPrefix + Guid.NewGuid().ToString("N"),
                LateOutputBufferCapacity);
            SetActiveLateOutputCapture(capture);
            try
            {
                IJupyterExecution execution;
                try
                {
                    execution = await currentManager.Client.ExecuteAsync(
                        new JupyterExecuteRequest(code, AllowStdin: true),
                        cancellationToken).ConfigureAwait(false);
                    capture.SetPrimaryRequestId(execution.RequestId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    MarkFaulted();
                    logger.LogWarning(exception, "Could not begin Deno execution for session {SessionId}.", SessionId);
                    throw new AgentToolException(
                        "repl_faulted",
                        $"Deno REPL session '{SessionId}' could not begin execution: {GetSafeMessage(exception)}");
                }

                await using var executionLifetime = execution.ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(options.ExecutionTimeout);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.Token,
                    timeout.Token);
                var collector = new DenoReplExecutionCollector(
                    SessionId,
                    currentGeneration,
                    options,
                    sink,
                    GetOrCreateDisplayId,
                    GetDisplayId);
                var completion = CollectExecutionWithLateOutputsAsync(
                    currentManager.Client,
                    execution,
                    collector,
                    capture,
                    wait.Token);
                try
                {
                    return await completion.WaitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (wait.IsCancellationRequested)
                {
                    var escalated = await InterruptAndDrainAsync(currentManager, completion).ConfigureAwait(false);
                    if (escalated) MarkFaulted();

                    if (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken.IsCancellationRequested
                            ? cancellationToken
                            : lifetime.Token);

                    if (timeout.IsCancellationRequested)
                        throw new AgentToolException(
                            "repl_timeout",
                            $"Deno REPL session '{SessionId}' exceeded its execution timeout.");

                    throw;
                }
                catch (AgentToolException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    MarkFaulted();
                    logger.LogWarning(exception, "Deno execution failed for session {SessionId}.", SessionId);
                    throw new AgentToolException(
                        "repl_faulted",
                        $"Deno REPL session '{SessionId}' failed during execution: {GetSafeMessage(exception)}");
                }
            }
            finally
            {
                capture.Complete();
                ClearActiveLateOutputCapture(capture);
            }
        }
        finally
        {
            lock (stateGate)
            {
                if (state == DenoReplSessionState.Busy) state = DenoReplSessionState.Idle;
            }

            executionGate.Release();
        }
    }

    internal async Task<DenoReplSessionResult> RestartAsync(CancellationToken cancellationToken)
    {
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                SetState(DenoReplSessionState.Restarting);
                await StopGenerationLoopAsync().ConfigureAwait(false);
                lock (stateGate)
                {
                    displayIds.Clear();
                    generation = checked(generation + 1);
                }

                using var startup = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.Token);
                startup.CancelAfter(options.StartupTimeout);
                try
                {
                    if (manager is null)
                        manager = await factory.StartAsync(
                            WorkingDirectory,
                            SessionId,
                            startup.Token).ConfigureAwait(false);
                    else
                        await manager.RestartAsync(startup.Token).ConfigureAwait(false);

                    await StartControlChannelAsync(startup.Token).ConfigureAwait(false);
                    await EnsureStdinReadyAsync(
                        manager.Client,
                        startup.Token).ConfigureAwait(false);
                    StartGenerationLoop(manager.Client);
                    SetState(DenoReplSessionState.Idle);
                    return GetSnapshot();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    SetState(DenoReplSessionState.Faulted);
                    throw;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    SetState(DenoReplSessionState.Faulted);
                    throw new OperationCanceledException(lifetime.Token);
                }
                catch (OperationCanceledException exception) when (startup.IsCancellationRequested)
                {
                    SetState(DenoReplSessionState.Faulted);
                    logger.LogWarning(exception, "Deno REPL session {SessionId} restart timed out.", SessionId);
                    throw new AgentToolException(
                        "repl_restart_failed",
                        $"Deno REPL session '{SessionId}' could not be restarted before its startup timeout.");
                }
                catch (Exception exception)
                {
                    SetState(DenoReplSessionState.Faulted);
                    logger.LogWarning(exception, "Could not restart Deno REPL session {SessionId}.", SessionId);
                    throw new AgentToolException(
                        "repl_restart_failed",
                        $"Deno REPL session '{SessionId}' could not be restarted: {GetSafeMessage(exception)}");
                }
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
        finally
        {
            executionGate.Release();
        }
    }

    private async Task CloseAsync(CancellationToken cancellationToken)
    {
        await lifetime.CancelAsync();
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (GetState() == DenoReplSessionState.Closed) return;

                SetState(DenoReplSessionState.Closing);
                await StopGenerationLoopAsync().ConfigureAwait(false);
                var currentManager = manager;
                manager = null;
                if (currentManager is not null)
                    try
                    {
                        await currentManager.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await currentManager.DisposeAsync().ConfigureAwait(false);
                    }

                UnregisterControlSession();

                SetState(DenoReplSessionState.Closed);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
        finally
        {
            executionGate.Release();
        }
    }

    private async Task<DenoReplExecutionResult> CollectExecutionWithLateOutputsAsync(
        IJupyterClient client,
        IJupyterExecution execution,
        DenoReplExecutionCollector collector,
        LateOutputCapture capture,
        CancellationToken cancellationToken)
    {
        var primaryCompletion = await collector
            .ConsumeExecutionAsync(execution, cancellationToken)
            .ConfigureAwait(false);
        var lateOutputDrain = DrainLateOutputsAsync(collector, capture, cancellationToken);
        Exception? failure = null;
        try
        {
            await RunIopubBarrierAsync(
                client,
                capture,
                primaryCompletion.Reply.ExecutionCount,
                cancellationToken).ConfigureAwait(false);
            await lateOutputDrain.ConfigureAwait(false);
            return collector.CreateResult(primaryCompletion);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            capture.Complete();
            if (failure is not null)
                try
                {
                    await lateOutputDrain.ConfigureAwait(false);
                }
                catch
                {
                    // The primary failure owns this operation; the capture drain is still observed.
                }
        }
    }

    private static async Task DrainLateOutputsAsync(
        DenoReplExecutionCollector collector,
        LateOutputCapture capture,
        CancellationToken cancellationToken)
    {
        await foreach (var output in capture.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            if (capture.IsPrimaryRequest(output.RequestId))
                await collector.ObserveLateOutputAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunIopubBarrierAsync(
        IJupyterClient client,
        LateOutputCapture capture,
        int? primaryExecutionCount,
        CancellationToken cancellationToken)
    {
        var data = $"{{{JsonSerializer.Serialize(
            IopubBarrierMediaType,
            DenoReplJsonSerializerContext.Default.String)}:{JsonSerializer.Serialize(
            capture.Marker,
            DenoReplJsonSerializerContext.Default.String)}}}";
        var code = $"await Deno.jupyter.display({data}, {{ raw: true }});";
        await using var barrier = await client.ExecuteAsync(
            new JupyterExecuteRequest(
                code,
                true,
                false,
                AllowStdin: false),
            new JupyterExecutionOptions(true),
            cancellationToken).ConfigureAwait(false);
        capture.SetBarrierRequestId(barrier.RequestId);

        // The marker may also be retained in this execution stream, but only the matching
        // WatchEventsAsync observation can close the capture after earlier event-hub items drain.
        await foreach (var _ in barrier.Outputs.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
        }

        var completion = await barrier.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(completion.Reply.Status, "ok", StringComparison.Ordinal))
            throw new InvalidOperationException("The Deno IOPub barrier execution failed.");

        if (completion.Reply.ExecutionCount != primaryExecutionCount)
            throw new InvalidOperationException("The Deno IOPub barrier changed the user execution count.");
    }

    private async Task<bool> InterruptAndDrainAsync(
        IJupyterKernelManager currentManager,
        Task completion)
    {
        try
        {
            await currentManager.InterruptAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not interrupt Deno REPL session {SessionId}.", SessionId);
        }

        using var grace = new CancellationTokenSource(options.InterruptGracePeriod);
        try
        {
            await completion.WaitAsync(grace.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (grace.IsCancellationRequested)
        {
        }
        catch
        {
            return false;
        }

        try
        {
            await currentManager.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not terminate timed-out Deno REPL session {SessionId}.", SessionId);
        }

        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // Shutdown must terminate the client execution; this observes its terminal failure.
        }

        return true;
    }

    private void StartGenerationLoop(IJupyterClient client)
    {
        generationLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        generationLoop = WatchGenerationAsync(client, generationLifetime.Token);
    }

    private async Task StartControlChannelAsync(CancellationToken cancellationToken)
    {
        if (manager?.ProcessId is not { } processId) return;

        if (registeredControlPid is { } previous && previous != processId) controlRegistry.Unregister(previous);

        controlRegistry.Register(processId, SessionId);
        registeredControlPid = processId;
        await ReplControlBootstrap.RunAsync(
            manager.Client,
            options.StartupTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureStdinReadyAsync(
        IJupyterClient client,
        CancellationToken cancellationToken)
    {
        // Deno 2.9.x can complete prompt() with null before its stdin peer is ready. Each standard
        // execute/input/reply round trip drives this product-scoped readiness negotiation without delay polling.
        while (true)
        {
            var nonce = StdinReadinessNoncePrefix + Guid.NewGuid().ToString("N");
            var serializedNonce = JsonSerializer.Serialize(
                nonce,
                DenoReplJsonSerializerContext.Default.String);
            var code = StdinReadinessProbeCodePrefix + serializedNonce +
                       ") throw new Error('Deno stdin readiness nonce mismatch');";
            await using var probe = await client.ExecuteAsync(
                new JupyterExecuteRequest(
                    code,
                    true,
                    false,
                    AllowStdin: true),
                cancellationToken).ConfigureAwait(false);
            var inputSeen = false;
            await foreach (var output in probe.Outputs.WithCancellation(cancellationToken).ConfigureAwait(false))
                if (output is JupyterInputRequest input)
                {
                    inputSeen = true;
                    await probe.ReplyInputAsync(input, nonce, cancellationToken).ConfigureAwait(false);
                }

            var completion = await probe.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (inputSeen && string.Equals(completion.Reply.Status, "ok", StringComparison.Ordinal)) return;
        }
    }

    private void UnregisterControlSession()
    {
        if (registeredControlPid is { } processId)
        {
            controlRegistry.Unregister(processId);
            registeredControlPid = null;
        }
    }

    private async Task StopGenerationLoopAsync()
    {
        var cancellation = generationLifetime;
        var loop = generationLoop;
        generationLifetime = null;
        generationLoop = null;
        if (cancellation is null) return;

        await cancellation.CancelAsync();
        try
        {
            if (loop is not null) await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task WatchGenerationAsync(IJupyterClient client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var clientEvent in client.WatchEventsAsync(cancellationToken).ConfigureAwait(false))
                switch (clientEvent)
                {
                    case JupyterExecutionOutputObserved observed:
                        GetActiveLateOutputCapture()?.ObserveBarrierEvent(
                            observed.RequestId,
                            observed.Output);
                        break;
                    case JupyterLateOutput lateOutput:
                        var capture = GetActiveLateOutputCapture();
                        if (capture is not null &&
                            await capture.TryRouteAsync(lateOutput, cancellationToken).ConfigureAwait(false))
                            break;

                        if (!lateOutput.IncludedInExecution)
                            logger.LogDebug(
                                "Discarded late Deno output for request {RequestId} in session {SessionId} because no matching execution capture is active.",
                                lateOutput.RequestId.Value,
                                SessionId);

                        break;
                    case JupyterClientDisconnected disconnected when !cancellationToken.IsCancellationRequested:
                        MarkFaulted();
                        logger.LogWarning(
                            disconnected.Cause,
                            "Deno REPL session {SessionId} disconnected unexpectedly.",
                            SessionId);
                        return;
                }

            if (!cancellationToken.IsCancellationRequested)
            {
                MarkFaulted();
                logger.LogWarning(
                    "Deno REPL event loop ended unexpectedly for session {SessionId}.",
                    SessionId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MarkFaulted();
            logger.LogWarning(exception, "Deno REPL event loop failed for session {SessionId}.", SessionId);
        }
    }

    private void SetActiveLateOutputCapture(LateOutputCapture capture)
    {
        lock (stateGate)
        {
            if (activeLateOutputCapture is not null)
                throw new InvalidOperationException("A Deno late-output capture is already active.");

            activeLateOutputCapture = capture;
        }
    }

    private LateOutputCapture? GetActiveLateOutputCapture()
    {
        lock (stateGate)
        {
            return activeLateOutputCapture;
        }
    }

    private void ClearActiveLateOutputCapture(LateOutputCapture capture)
    {
        lock (stateGate)
        {
            if (ReferenceEquals(activeLateOutputCapture, capture)) activeLateOutputCapture = null;
        }
    }

    private JupyterDisplayId GetOrCreateDisplayId(JupyterDisplayId innerDisplayId)
    {
        lock (stateGate)
        {
            if (displayIds.TryGetValue(innerDisplayId.Value, out var existing)) return existing;

            var identity = Encoding.UTF8.GetBytes(
                $"{OwnerSessionId}:{SessionId}:{generation}:{innerDisplayId.Value}");
            var hash = Convert.ToHexString(SHA256.HashData(identity).AsSpan(0, 16)).ToLowerInvariant();
            var created = new JupyterDisplayId($"maieutics-repl-{hash}");
            displayIds.Add(innerDisplayId.Value, created);
            return created;
        }
    }

    private JupyterDisplayId? GetDisplayId(JupyterDisplayId innerDisplayId)
    {
        lock (stateGate)
        {
            return displayIds.GetValueOrDefault(innerDisplayId.Value);
        }
    }

    private int GetGeneration()
    {
        lock (stateGate)
        {
            return generation;
        }
    }

    private DenoReplSessionState GetState()
    {
        lock (stateGate)
        {
            return state;
        }
    }

    private void SetState(DenoReplSessionState value)
    {
        lock (stateGate)
        {
            state = value;
        }
    }

    private void MarkFaulted()
    {
        lock (stateGate)
        {
            if (state is not DenoReplSessionState.Closing and not DenoReplSessionState.Closed)
                state = DenoReplSessionState.Faulted;
        }
    }

    private AgentToolException CreateFaultedException()
    {
        return new AgentToolException(
            "repl_faulted",
            $"Deno REPL session '{SessionId}' is faulted and requires an explicit restart or close.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
    }

    private static string ToWireState(DenoReplSessionState value)
    {
        return value switch
        {
            DenoReplSessionState.Created => "created",
            DenoReplSessionState.Starting => "starting",
            DenoReplSessionState.Idle => "idle",
            DenoReplSessionState.Busy => "busy",
            DenoReplSessionState.Restarting => "restarting",
            DenoReplSessionState.Faulted => "faulted",
            DenoReplSessionState.Closing => "closing",
            DenoReplSessionState.Closed => "closed",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
    }

    private sealed class LateOutputCapture(string marker, int capacity)
    {
        private readonly Lock gate = new();

        private readonly Channel<JupyterOutput> outputs = Channel.CreateBounded<JupyterOutput>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        private JupyterMessageId? barrierRequestId;
        private JupyterMessageId? observedBarrierRequestId;
        private JupyterMessageId? primaryRequestId;

        internal string Marker { get; } = marker;

        internal void SetPrimaryRequestId(JupyterMessageId requestId)
        {
            lock (gate)
            {
                if (primaryRequestId is not null)
                    throw new InvalidOperationException("The Deno execution request ID was already assigned.");

                primaryRequestId = requestId;
            }
        }

        internal void SetBarrierRequestId(JupyterMessageId requestId)
        {
            Exception? failure = null;
            var completed = false;
            lock (gate)
            {
                if (barrierRequestId is not null)
                    throw new InvalidOperationException("The Deno IOPub barrier request ID was already assigned.");

                barrierRequestId = requestId;
                if (observedBarrierRequestId is { } observed)
                {
                    completed = observed.Equals(requestId);
                    if (!completed)
                        failure = new InvalidOperationException(
                            "The Deno IOPub barrier marker had an unexpected parent request ID.");
                }
            }

            if (failure is not null)
                outputs.Writer.TryComplete(failure);
            else if (completed)
                outputs.Writer.TryComplete();
        }

        internal bool IsPrimaryRequest(JupyterMessageId requestId)
        {
            lock (gate)
            {
                return primaryRequestId is { } primary && primary.Equals(requestId);
            }
        }

        internal IAsyncEnumerable<JupyterOutput> ReadAllAsync(CancellationToken cancellationToken)
        {
            return outputs.Reader.ReadAllAsync(cancellationToken);
        }

        internal void ObserveBarrierEvent(JupyterMessageId requestId, JupyterOutput output)
        {
            if (output is not JupyterDisplayOutput display || !IsBarrierMarker(display)) return;

            RecordBarrier(requestId);
        }

        internal async ValueTask<bool> TryRouteAsync(
            JupyterLateOutput lateOutput,
            CancellationToken cancellationToken)
        {
            var output = lateOutput.Output;
            var marker = output is JupyterDisplayOutput display && IsBarrierMarker(display);
            var write = false;
            bool handled;
            lock (gate)
            {
                var primaryMatch = primaryRequestId is { } primary && primary.Equals(lateOutput.RequestId);
                var barrierMatch = barrierRequestId is { } barrier && barrier.Equals(lateOutput.RequestId);
                if (marker)
                {
                    handled = true;
                }
                else if (lateOutput.IncludedInExecution)
                {
                    handled = primaryRequestId is null || primaryMatch || barrierMatch;
                }
                else if (output is not null && (primaryRequestId is null || primaryMatch))
                {
                    handled = true;
                    write = true;
                }
                else
                {
                    handled = barrierMatch;
                }
            }

            if (marker)
            {
                RecordBarrier(lateOutput.RequestId);
                return true;
            }

            if (!write || output is null) return handled;

            try
            {
                await outputs.Writer.WriteAsync(output, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
            }

            return true;
        }

        internal void Complete()
        {
            outputs.Writer.TryComplete();
        }

        private void RecordBarrier(JupyterMessageId requestId)
        {
            Exception? failure = null;
            var complete = false;
            lock (gate)
            {
                observedBarrierRequestId = requestId;
                if (barrierRequestId is { } expected)
                {
                    complete = expected.Equals(requestId);
                    if (!complete)
                        failure = new InvalidOperationException(
                            "The Deno IOPub barrier marker had an unexpected parent request ID.");
                }
            }

            if (failure is not null)
                outputs.Writer.TryComplete(failure);
            else if (complete)
                outputs.Writer.TryComplete();
        }

        private bool IsBarrierMarker(JupyterDisplayOutput display)
        {
            return display.Data.Data.TryGetValue(IopubBarrierMediaType, out var value) &&
                   value.ValueKind == JsonValueKind.String &&
                   string.Equals(value.GetString(), Marker, StringComparison.Ordinal);
        }
    }

    private static string GetSafeMessage(Exception exception)
    {
        return exception switch
        {
            FileNotFoundException => "The configured Deno executable was not found.",
            UnauthorizedAccessException => "The operating system denied access to the Deno executable or workspace.",
            _ => "The Deno kernel process or Jupyter connection failed."
        };
    }
}