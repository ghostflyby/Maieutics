using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.Logging;

namespace Maieutics.Execution;

internal sealed class DenoReplSession : IAsyncDisposable
{
    private readonly Dictionary<string, JupyterDisplayId> displayIds = new(StringComparer.Ordinal);

    private readonly TaskCompletionSource disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim executionGate = new(1, 1);
    private readonly IDenoReplSessionFactory factory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly ILogger<DenoReplSession> logger;
    private readonly DenoReplOptions options;
    private readonly IDenoReplPresentationRouter presentationRouter;
    private readonly ReplControlSessionRegistry controlRegistry;
    private readonly object stateGate = new();
    private CancellationTokenSource? generationLifetime;
    private Task? generationLoop;
    private IJupyterKernelManager? manager;
    private int? registeredControlPid;
    private DenoReplSessionState state = DenoReplSessionState.Created;
    private int disposeState;
    private int generation = 1;
    private int latePresentationEventsRemaining;
    private int latePresentationTextBytesRemaining;

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
        latePresentationEventsRemaining = options.MaxPresentationEventsPerExecution;
        latePresentationTextBytesRemaining = options.MaxPresentationTextBytes;
    }

    internal AgentSessionId OwnerSessionId { get; }

    internal string SessionId { get; }

    internal bool IsDefault { get; }

    internal string WorkingDirectory { get; }

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
            if (current is DenoReplSessionState.Idle or DenoReplSessionState.Busy)
            {
                return;
            }

            if (current == DenoReplSessionState.Faulted)
            {
                throw CreateFaultedException();
            }

            SetState(DenoReplSessionState.Starting);
            try
            {
                manager = await factory.StartAsync(WorkingDirectory, cancellationToken).ConfigureAwait(false);
                await StartControlChannelAsync(cancellationToken).ConfigureAwait(false);
                StartGenerationLoop(manager.Client);
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
            if (GetState() == DenoReplSessionState.Faulted)
            {
                throw CreateFaultedException();
            }

            var currentManager = manager ?? throw CreateFaultedException();
            var currentGeneration = GetGeneration();
            SetState(DenoReplSessionState.Busy);
            Interlocked.Exchange(
                ref latePresentationEventsRemaining,
                options.MaxPresentationEventsPerExecution);
            Interlocked.Exchange(
                ref latePresentationTextBytesRemaining,
                options.MaxPresentationTextBytes);

            var sink = await presentationRouter.WaitForCallAsync(
                OwnerSessionId,
                callId,
                cancellationToken).ConfigureAwait(false);
            IJupyterExecution execution;
            try
            {
                execution = await currentManager.Client.ExecuteAsync(
                    new JupyterExecuteRequest(code, AllowStdin: true),
                    cancellationToken).ConfigureAwait(false);
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
            var completion = collector.ConsumeAsync(execution, wait.Token);
            try
            {
                return await completion.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested)
            {
                var escalated = await InterruptAndDrainAsync(currentManager, completion).ConfigureAwait(false);
                if (escalated)
                {
                    MarkFaulted();
                }

                if (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken.IsCancellationRequested
                        ? cancellationToken
                        : lifetime.Token);
                }

                if (timeout.IsCancellationRequested)
                {
                    throw new AgentToolException(
                        "repl_timeout",
                        $"Deno REPL session '{SessionId}' exceeded its execution timeout.");
                }

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
                if (state == DenoReplSessionState.Busy)
                {
                    state = DenoReplSessionState.Idle;
                }
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

                try
                {
                    if (manager is null)
                    {
                        manager = await factory.StartAsync(WorkingDirectory, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await manager.RestartAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await StartControlChannelAsync(cancellationToken).ConfigureAwait(false);
                    StartGenerationLoop(manager.Client);
                    SetState(DenoReplSessionState.Idle);
                    return GetSnapshot();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    SetState(DenoReplSessionState.Faulted);
                    throw;
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

    internal async Task CloseAsync(CancellationToken cancellationToken)
    {
        await lifetime.CancelAsync();
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (GetState() == DenoReplSessionState.Closed)
                {
                    return;
                }

                SetState(DenoReplSessionState.Closing);
                await StopGenerationLoopAsync().ConfigureAwait(false);
                var currentManager = manager;
                manager = null;
                if (currentManager is not null)
                {
                    try
                    {
                        await currentManager.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await currentManager.DisposeAsync().ConfigureAwait(false);
                    }
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
        {
            disposalCompletion.TrySetResult();
        }
        else
        {
            disposalCompletion.TrySetException(failure);
        }

        await disposalCompletion.Task.ConfigureAwait(false);
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
        if (manager?.ProcessId is not { } processId)
        {
            return;
        }

        if (registeredControlPid is { } previous && previous != processId)
        {
            controlRegistry.Unregister(previous);
        }

        controlRegistry.Register(processId, SessionId);
        registeredControlPid = processId;
        await ReplControlBootstrap.RunAsync(
            manager.Client,
            options.StartupTimeout,
            cancellationToken).ConfigureAwait(false);
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
        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync();
        try
        {
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }
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
            {
                switch (clientEvent)
                {
                    case JupyterLateOutput { Output: { } output }:
                        await RouteLateOutputAsync(output, cancellationToken).ConfigureAwait(false);
                        break;
                    case JupyterClientDisconnected disconnected when !cancellationToken.IsCancellationRequested:
                        MarkFaulted();
                        logger.LogWarning(
                            disconnected.Cause,
                            "Deno REPL session {SessionId} disconnected unexpectedly.",
                            SessionId);
                        return;
                }
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

    private async ValueTask RouteLateOutputAsync(JupyterOutput output, CancellationToken cancellationToken)
    {
        if (!presentationRouter.TryGetCurrentSink(OwnerSessionId, out var sink))
        {
            logger.LogDebug(
                "Discarded late Deno output {OutputType} for session {SessionId} because no Agent run sink is active.",
                output.GetType().Name,
                SessionId);
            return;
        }

        switch (output)
        {
            case JupyterDisplayOutput display when CanPresentLateBundle(display.Data, display.Metadata):
                if (display.DisplayId is { } innerDisplayId)
                {
                    await sink.DisplayTrackedAsync(
                        display.Data,
                        GetOrCreateDisplayId(innerDisplayId),
                        display.Metadata,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await sink.DisplayAsync(display.Data, display.Metadata, cancellationToken).ConfigureAwait(false);
                }

                break;
            case JupyterDisplayUpdateOutput update when CanPresentLateBundle(update.Data, update.Metadata):
                if (GetDisplayId(update.DisplayId) is { } updateDisplayId)
                {
                    await sink.UpdateDisplayAsync(
                        updateDisplayId,
                        update.Data,
                        update.Metadata,
                        cancellationToken).ConfigureAwait(false);
                }

                break;
            case JupyterClearOutput clear when TryReserveLatePresentationEvent():
                await sink.ClearOutputAsync(clear.Wait, cancellationToken).ConfigureAwait(false);
                break;
            case JupyterStderr stderr when TryReserveLatePresentationEvent():
                var text = TakeLatePresentationText(stderr.Text);
                if (text.Length > 0)
                {
                    await sink.WriteStderrAsync(text, cancellationToken).ConfigureAwait(false);
                }

                break;
            case JupyterExecutionError error when TryReserveLatePresentationEvent():
                var value = TakeLatePresentationText(error.Value);
                var traceback = error.Traceback
                    .Select(TakeLatePresentationText)
                    .Where(static line => line.Length > 0)
                    .ToArray();
                await sink.PublishErrorAsync(
                    error.Name,
                    value,
                    traceback,
                    cancellationToken).ConfigureAwait(false);
                break;
            case JupyterMalformedOutput malformed:
                logger.LogDebug(
                    "Discarded malformed late Deno output {MessageType} ({ErrorCode}) for session {SessionId}.",
                    malformed.MessageType,
                    malformed.ErrorCode,
                    SessionId);
                break;
            default:
                logger.LogDebug(
                    "Discarded model-only late Deno output {OutputType} for session {SessionId}.",
                    output.GetType().Name,
                    SessionId);
                break;
        }
    }

    private bool CanPresentLateBundle(
        MimeBundle bundle,
        IReadOnlyDictionary<string, JsonElement> metadata) =>
        TryReserveLatePresentationEvent() &&
        DenoReplExecutionCollector.CountJsonBytes(bundle.Data) +
        DenoReplExecutionCollector.CountJsonBytes(metadata) <= options.MaxPresentationBundleBytes;

    private bool TryReserveLatePresentationEvent() =>
        Interlocked.Decrement(ref latePresentationEventsRemaining) >= 0;

    private string TakeLatePresentationText(string text)
    {
        while (true)
        {
            var remaining = Volatile.Read(ref latePresentationTextBytesRemaining);
            if (remaining <= 0)
            {
                return string.Empty;
            }

            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes <= remaining)
            {
                if (Interlocked.CompareExchange(
                        ref latePresentationTextBytesRemaining,
                        remaining - bytes,
                        remaining) == remaining)
                {
                    return text;
                }

                continue;
            }

            if (Interlocked.CompareExchange(ref latePresentationTextBytesRemaining, 0, remaining) == remaining)
            {
                var selected = new StringBuilder();
                var selectedBytes = 0;
                foreach (var rune in text.EnumerateRunes())
                {
                    if (selectedBytes + rune.Utf8SequenceLength > remaining)
                    {
                        break;
                    }

                    selected.Append(rune.ToString());
                    selectedBytes += rune.Utf8SequenceLength;
                }

                return selected.ToString();
            }
        }
    }

    private JupyterDisplayId GetOrCreateDisplayId(JupyterDisplayId innerDisplayId)
    {
        lock (stateGate)
        {
            if (displayIds.TryGetValue(innerDisplayId.Value, out var existing))
            {
                return existing;
            }

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
            {
                state = DenoReplSessionState.Faulted;
            }
        }
    }

    private AgentToolException CreateFaultedException() => new(
        "repl_faulted",
        $"Deno REPL session '{SessionId}' is faulted and requires an explicit restart or close.");

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);

    private static string ToWireState(DenoReplSessionState value) => value switch
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

    private static string GetSafeMessage(Exception exception) => exception switch
    {
        FileNotFoundException => "The configured Deno executable was not found.",
        UnauthorizedAccessException => "The operating system denied access to the Deno executable or workspace.",
        _ => "The Deno kernel process or Jupyter connection failed."
    };
}