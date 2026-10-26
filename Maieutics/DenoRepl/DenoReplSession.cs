using Maieutics.Agent;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoRepl;

internal sealed class DenoReplSession : IAsyncDisposable
{
    private readonly TaskCompletionSource _disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Dictionary<string, ReplDisplayId> _displayIds =
        new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly IDenoReplSessionFactory _factory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger<DenoReplSession> _logger;
    private readonly DenoReplOptions _options;
    private readonly IDenoReplPresentationRouter _presentationRouter;
    private readonly IReplDisplayObjectStore? _displayObjectStore;

    /// <summary>Session-lifetime display rate limiter, shared by every execution so the sliding
    /// budget accumulates across turns (aligned with jupyter_server's global iopub rate limit).
    /// A restart intentionally keeps the window: it is not a new frontend, and the window prunes
    /// stale samples within <see cref="DenoReplOptions.DisplayRateLimitWindow"/>.</summary>
    private readonly ReplOutputRateLimiter _rateLimiter;

    private readonly Lock _stateGate = new();
    private int _disposeState;
    private int _generation = 1;
    private CancellationTokenSource? _generationLifetime;
    private Task? _generationMonitor;
    private IDenoReplGeneration? _runtime;
    private DenoReplSessionState _state = DenoReplSessionState.Created;

    internal DenoReplSession(
        AgentSessionId ownerSessionId,
        string sessionId,
        bool isDefault,
        string workingDirectory,
        DenoReplOptions options,
        IDenoReplSessionFactory factory,
        IDenoReplPresentationRouter presentationRouter,
        ILogger<DenoReplSession> logger,
        IReplDisplayObjectStore? displayObjectStore = null)
    {
        OwnerSessionId = ownerSessionId;
        SessionId = sessionId;
        IsDefault = isDefault;
        WorkingDirectory = workingDirectory;
        _options = options;
        _factory = factory;
        _presentationRouter = presentationRouter;
        _logger = logger;
        _displayObjectStore = displayObjectStore;
        _rateLimiter = new ReplOutputRateLimiter(options);
    }

    internal AgentSessionId OwnerSessionId { get; }

    internal string SessionId { get; }

    internal bool IsDefault { get; }

    internal string WorkingDirectory { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _disposalCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _lifetime.Dispose();
            _lifecycleGate.Dispose();
            _executionGate.Dispose();
        }

        if (failure is null)
            _disposalCompletion.TrySetResult();
        else
            _disposalCompletion.TrySetException(failure);
        await _disposalCompletion.Task.ConfigureAwait(false);
    }

    internal DenoReplSessionResult GetSnapshot()
    {
        lock (_stateGate)
        {
            return new DenoReplSessionResult(
                SessionId,
                _generation,
                ToWireState(_state),
                WorkingDirectory,
                IsDefault);
        }
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = GetState();
            if (current is DenoReplSessionState.Idle or DenoReplSessionState.Busy) return;
            if (current == DenoReplSessionState.Faulted) throw CreateFaultedException();
            SetState(DenoReplSessionState.Starting);
            try
            {
                var started = await _factory.StartAsync(
                    WorkingDirectory,
                    SessionId,
                    OwnerSessionId,
                    GetGeneration(),
                    cancellationToken).ConfigureAwait(false);
                _runtime = started;
                StartGenerationMonitor(started);
                SetStateIdleIfInTransition(DenoReplSessionState.Starting);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(DenoReplSessionState.Faulted);
                throw;
            }
            catch (Exception exception)
            {
                SetState(DenoReplSessionState.Faulted);
                _logger.LogWarning(exception, "Could not start Deno REPL session {SessionId}.", SessionId);
                throw new AgentToolException(
                    "repl_start_failed",
                    $"Deno REPL session '{SessionId}' could not be started: {GetSafeMessage(exception)}");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async Task<DenoReplExecutionResult> ExecuteAsync(
        string code,
        AgentToolCallId callId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (GetState() == DenoReplSessionState.Faulted) throw CreateFaultedException();
            var activeRuntime = _runtime ?? throw CreateFaultedException();
            SetState(DenoReplSessionState.Busy);
            var sink = await _presentationRouter.WaitForCallAsync(
                OwnerSessionId,
                callId,
                cancellationToken).ConfigureAwait(false);
            var execution = await activeRuntime.Connection.ExecuteAsync(code, cancellationToken).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(_options.ExecutionTimeout);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token,
                timeout.Token);
            var outputEvents = await ResolveOutputEventsAsync(wait.Token).ConfigureAwait(false);
            var collector = new DenoReplExecutionCollector(
                SessionId,
                GetGeneration(),
                _options,
                sink,
                _displayIds,
                execution.ExecutionId,
                _rateLimiter,
                _displayObjectStore,
                logger: _logger);
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
                if (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
                    throw new OperationCanceledException(
                        cancellationToken.IsCancellationRequested ? cancellationToken : _lifetime.Token);
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
                _logger.LogWarning(exception, "Deno execution failed for session {SessionId}.", SessionId);
                throw new AgentToolException(
                    "repl_faulted",
                    $"Deno REPL session '{SessionId}' failed during execution: {GetSafeMessage(exception)}");
            }
        }
        finally
        {
            lock (_stateGate)
            {
                if (_state == DenoReplSessionState.Busy) _state = DenoReplSessionState.Idle;
            }
            _executionGate.Release();
        }
    }

    internal async Task<DenoReplSessionResult> RestartAsync(CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                SetState(DenoReplSessionState.Restarting);
                await StopGenerationMonitorAsync().ConfigureAwait(false);
                if (_runtime is { } previous)
                {
                    _runtime = null;
                    await previous.DisposeAsync().ConfigureAwait(false);
                }
                lock (_stateGate)
                {
                    _displayIds.Clear();
                    _generation = checked(_generation + 1);
                }
                var started = await _factory.StartAsync(
                    WorkingDirectory,
                    SessionId,
                    OwnerSessionId,
                    GetGeneration(),
                    cancellationToken).ConfigureAwait(false);
                _runtime = started;
                StartGenerationMonitor(started);
                SetStateIdleIfInTransition(DenoReplSessionState.Restarting);
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
                _logger.LogWarning(exception, "Could not restart Deno REPL session {SessionId}.", SessionId);
                throw new AgentToolException(
                    "repl_restart_failed",
                    $"Deno REPL session '{SessionId}' could not be restarted: {GetSafeMessage(exception)}");
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task CloseAsync()
    {
        await _executionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (GetState() == DenoReplSessionState.Closed) return;
                SetState(DenoReplSessionState.Closing);
                await StopGenerationMonitorAsync().ConfigureAwait(false);
                if (_runtime is { } activeRuntime)
                {
                    _runtime = null;
                    await activeRuntime.DisposeAsync().ConfigureAwait(false);
                }
                SetState(DenoReplSessionState.Closed);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _executionGate.Release();
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
        var activeRuntime = _runtime ?? throw CreateFaultedException();
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
                .WaitAsync(_options.InterruptGracePeriod, CancellationToken.None)
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
            _logger.LogDebug(exception, "Deno REPL session {SessionId} did not drain after cancellation.", SessionId);
            await activeRuntime.TerminateAsync().ConfigureAwait(false);
            return true;
        }
    }

    private void StartGenerationMonitor(IDenoReplGeneration value)
    {
        _generationLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _generationMonitor = ObserveGenerationAsync(value, _generationLifetime.Token);
    }

    private async Task ObserveGenerationAsync(IDenoReplGeneration value, CancellationToken cancellationToken)
    {
        try
        {
            await value.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                MarkFaulted();
                _logger.LogWarning(
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
                _logger.LogWarning(exception, "Deno REPL session {SessionId} channel failed.", SessionId);
            }
        }
    }

    private async Task StopGenerationMonitorAsync()
    {
        var cancellation = _generationLifetime;
        var monitor = _generationMonitor;
        _generationLifetime = null;
        _generationMonitor = null;
        if (cancellation is null) return;
        await cancellation.CancelAsync().ConfigureAwait(false);
        if (monitor is not null) await monitor.ConfigureAwait(false);
        cancellation.Dispose();
    }

    private int GetGeneration()
    {
        lock (_stateGate)
        {
            return _generation;
        }
    }

    private DenoReplSessionState GetState()
    {
        lock (_stateGate)
        {
            return _state;
        }
    }

    /// <summary>Moves a session out of the caller's own transient state (Starting for a
    /// start, Restarting for a restart) into idle without erasing a terminal state the
    /// generation monitor may have already recorded synchronously — a child that died during
    /// the startup handshake reports Faulted before the startup code completes.</summary>
    private void SetStateIdleIfInTransition(DenoReplSessionState expected)
    {
        lock (_stateGate)
        {
            if (_state == expected) _state = DenoReplSessionState.Idle;
        }
    }

    private void SetState(DenoReplSessionState value)
    {
        lock (_stateGate)
        {
            _state = value;
        }
    }

    private void MarkFaulted()
    {
        lock (_stateGate)
        {
            if (_state is not DenoReplSessionState.Closing and not DenoReplSessionState.Closed)
                _state = DenoReplSessionState.Faulted;
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
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
