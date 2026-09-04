using Maieutics.Agent;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoRepl;

internal sealed class DenoReplSession : IAsyncDisposable
{
    private readonly TaskCompletionSource disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, ReplDisplayId> displayIds =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim executionGate = new(1, 1);
    private readonly IDenoReplSessionFactory factory;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ILogger<DenoReplSession> logger;
    private readonly DenoReplOptions options;
    private readonly IDenoReplPresentationRouter presentationRouter;
    /// <summary>Session-lifetime display rate limiter, shared by every execution so the sliding
    /// budget accumulates across turns (aligned with jupyter_server's global iopub rate limit).
    /// A restart intentionally keeps the window: it is not a new frontend, and the window prunes
    /// stale samples within <see cref="DenoReplOptions.DisplayRateLimitWindow"/>.</summary>
    private readonly ReplOutputRateLimiter rateLimiter;
    private readonly Lock stateGate = new();
    private int disposeState;
    private int generation = 1;
    private CancellationTokenSource? generationLifetime;
    private Task? generationMonitor;
    private IDenoReplGeneration? runtime;
    private DenoReplSessionState state = DenoReplSessionState.Created;

    internal DenoReplSession(
        AgentSessionId ownerSessionId,
        string sessionId,
        bool isDefault,
        string workingDirectory,
        DenoReplOptions options,
        IDenoReplSessionFactory factory,
        IDenoReplPresentationRouter presentationRouter,
        ILogger<DenoReplSession> logger)
    {
        OwnerSessionId = ownerSessionId;
        SessionId = sessionId;
        IsDefault = isDefault;
        WorkingDirectory = workingDirectory;
        this.options = options;
        this.factory = factory;
        this.presentationRouter = presentationRouter;
        this.logger = logger;
        rateLimiter = new ReplOutputRateLimiter(options);
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
            await lifetime.CancelAsync().ConfigureAwait(false);
            await CloseAsync().ConfigureAwait(false);
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
            try
            {
                var started = await factory.StartAsync(
                    WorkingDirectory,
                    SessionId,
                    GetGeneration(),
                    cancellationToken).ConfigureAwait(false);
                runtime = started;
                StartGenerationMonitor(started);
                SetState(DenoReplSessionState.Idle);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(DenoReplSessionState.Faulted);
                throw;
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
            var activeRuntime = runtime ?? throw CreateFaultedException();
            SetState(DenoReplSessionState.Busy);
            var sink = await presentationRouter.WaitForCallAsync(
                OwnerSessionId,
                callId,
                cancellationToken).ConfigureAwait(false);
            var execution = await activeRuntime.Connection.ExecuteAsync(code, cancellationToken).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(options.ExecutionTimeout);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime.Token,
                timeout.Token);
            var outputEvents = await ResolveOutputEventsAsync(wait.Token).ConfigureAwait(false);
            var collector = new DenoReplExecutionCollector(
                SessionId,
                GetGeneration(),
                options,
                sink,
                displayIds,
                execution.ExecutionId,
                rateLimiter);
            var completion = collector.ConsumeAsync(activeRuntime.Connection, execution, outputEvents, wait.Token);
            try
            {
                var result = await completion.WaitAsync(wait.Token).ConfigureAwait(false);
                if (await execution.Completion.ConfigureAwait(false) is ReplEvalErrorTerminal { Fatal: true })
                    MarkFaulted();
                return result;
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested)
            {
                var escalated = await CancelAndDrainAsync(activeRuntime, execution, completion).ConfigureAwait(false);
                if (escalated) MarkFaulted();
                if (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
                    throw new OperationCanceledException(
                        cancellationToken.IsCancellationRequested ? cancellationToken : lifetime.Token);
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
                await StopGenerationMonitorAsync().ConfigureAwait(false);
                if (runtime is { } previous)
                {
                    runtime = null;
                    await previous.DisposeAsync().ConfigureAwait(false);
                }
                lock (stateGate)
                {
                    displayIds.Clear();
                    generation = checked(generation + 1);
                }
                var started = await factory.StartAsync(
                    WorkingDirectory,
                    SessionId,
                    GetGeneration(),
                    cancellationToken).ConfigureAwait(false);
                runtime = started;
                StartGenerationMonitor(started);
                SetState(DenoReplSessionState.Idle);
                return GetSnapshot();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkFaulted();
                throw;
            }
            catch (Exception exception)
            {
                MarkFaulted();
                logger.LogWarning(exception, "Could not restart Deno REPL session {SessionId}.", SessionId);
                throw new AgentToolException(
                    "repl_restart_failed",
                    $"Deno REPL session '{SessionId}' could not be restarted: {GetSafeMessage(exception)}");
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

    private async Task CloseAsync()
    {
        await executionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (GetState() == DenoReplSessionState.Closed) return;
                SetState(DenoReplSessionState.Closing);
                await StopGenerationMonitorAsync().ConfigureAwait(false);
                if (runtime is { } activeRuntime)
                {
                    runtime = null;
                    await activeRuntime.DisposeAsync().ConfigureAwait(false);
                }
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

    /// <summary>
    ///     Resolves the output events for the current execution. The generation owns the output
    ///     connection (it awaits the dedicated binary output endpoint attaching during startup);
    ///     the session-lifetime stream is consumed per execution. When the generation does not
    ///     expose an output stream (isolated unit harnesses) an empty stream is returned, which
    ///     ends immediately, so the collector degrades to the eval control plane only.
    /// </summary>
    private async Task<IAsyncEnumerable<ReplOutputFrame>> ResolveOutputEventsAsync(
        CancellationToken cancellationToken)
    {
        var activeRuntime = runtime ?? throw CreateFaultedException();
        var output = activeRuntime.OutputEvents;
        if (output is null) return EmptyOutputEvents();
        return await output.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An output stream that ends immediately, used when no output endpoint is wired.</summary>
    private static async IAsyncEnumerable<ReplOutputFrame> EmptyOutputEvents()
    {
        yield break;
    }

    private async Task<bool> CancelAndDrainAsync(
        IDenoReplGeneration activeRuntime,
        ReplEvalExecution execution,
        Task completion)
    {
        try
        {
            var cancellation = activeRuntime.Connection.CancelAsync(execution.ExecutionId, CancellationToken.None);
            await Task.WhenAll(cancellation, completion)
                .WaitAsync(options.InterruptGracePeriod, CancellationToken.None)
                .ConfigureAwait(false);
            return false;
        }
        catch (TimeoutException)
        {
            await activeRuntime.TerminateAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Deno REPL session {SessionId} did not drain after cancellation.", SessionId);
            await activeRuntime.TerminateAsync().ConfigureAwait(false);
            return true;
        }
    }

    private void StartGenerationMonitor(IDenoReplGeneration value)
    {
        generationLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        generationMonitor = ObserveGenerationAsync(value, generationLifetime.Token);
    }

    private async Task ObserveGenerationAsync(IDenoReplGeneration value, CancellationToken cancellationToken)
    {
        try
        {
            await value.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                MarkFaulted();
                logger.LogWarning(
                    "Deno REPL session {SessionId} process exited unexpectedly with code {ExitCode}.",
                    SessionId,
                    value.ExitCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                MarkFaulted();
                logger.LogWarning(exception, "Deno REPL session {SessionId} channel failed.", SessionId);
            }
        }
    }

    private async Task StopGenerationMonitorAsync()
    {
        var cancellation = generationLifetime;
        var monitor = generationMonitor;
        generationLifetime = null;
        generationMonitor = null;
        if (cancellation is null) return;
        await cancellation.CancelAsync().ConfigureAwait(false);
        if (monitor is not null) await monitor.ConfigureAwait(false);
        cancellation.Dispose();
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
            $"Deno REPL session '{SessionId}' is faulted. Restart it before executing more code.");
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

    private static string GetSafeMessage(Exception exception)
    {
        return exception switch
        {
            TimeoutException => "the operation timed out",
            IOException => "the REPL process connection failed",
            _ => exception.Message
        };
    }
}
