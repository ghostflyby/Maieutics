using System.Text;
using Maieutics.Agent;
using Maieutics.Permissions;
using Maieutics.Processes;
using Microsoft.Extensions.Logging;
using XTerm;
using XTerm.Buffer;
using XTerm.Common;
using XTerm.Events;

namespace Maieutics.Execution;

/// <summary>Owns one PTY child, the headless terminal emulator, and the output pump that feeds it.</summary>
internal sealed class TerminalSession : IAsyncDisposable
{
    /// <summary>The maximum characters of row text kept on each side of the cursor for the content window.</summary>
    private const int CursorContextWindow = 6;

    private readonly TaskCompletionSource _disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly ITerminalProcessFactory _factory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger<TerminalSession> _logger;
    private readonly TerminalOptions _options;
    private readonly string _workingDirectory;
    private readonly TerminalSessionKind _kind;
    private readonly string _executable;
    private readonly IReadOnlyList<string> _launchArguments;
    private readonly EffectivePolicy _policy;
    private readonly Lock _signalGate = new();
    private readonly Lock _stateGate = new();
    private readonly Lock _terminalGate = new();
    private readonly Terminal _terminal;
    private readonly TerminalKeyEncoder _keyEncoder;
    private readonly TaskCompletionSource _exitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private bool _cursorBlink;
    private CursorStyle _cursorStyle = CursorStyle.Block;
    private int _disposeState;
    private int? _exitCode;
    private string[]? _lastRowTexts;
    private TaskCompletionSource _screenChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _screenVersion;
    private ITerminalProcess? _process;

    /// <summary>Ensures the child's exit is observed exactly once even though it can arrive
    /// through two paths: the reaper's <c>Exited</c> event and the post-spawn catch-up that
    /// replays an exit which fired before the handler was attached.</summary>
    private int _exitObserved;

    private Task? _pump;
    private CancellationTokenSource? _pumpLifetime;
    private TerminalSessionState _state = TerminalSessionState.Created;
    private TaskCompletionSource _stateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TerminalSession(
        string sessionId,
        bool isDefault,
        string workingDirectory,
        TerminalSessionKind kind,
        string executable,
        IReadOnlyList<string> launchArguments,
        EffectivePolicy policy,
        TerminalOptions options,
        ITerminalProcessFactory factory,
        ILogger<TerminalSession> logger)
    {
        SessionId = sessionId;
        IsDefault = isDefault;
        _workingDirectory = workingDirectory;
        _kind = kind;
        _executable = executable;
        _launchArguments = launchArguments;
        _policy = policy;
        _options = options;
        _factory = factory;
        _logger = logger;
        _terminal = new Terminal(new XTerm.Options.TerminalOptions
        {
            Cols = options.Columns,
            Rows = options.Rows,
            Scrollback = 0
        });
        // The emulator has no cursor-style getter; it only raises CursorStyleChanged (DECSCUSR). The pump
        // raises it inside terminalGate while applying output, so the handlers need no further locking.
        _terminal.CursorStyleChanged += OnCursorStyleChanged;
        _keyEncoder = new TerminalKeyEncoder(_terminal);
    }

    private void OnCursorStyleChanged(
        object? sender,
        TerminalEvents.CursorStyleChangedEventArgs args)
    {
        _cursorStyle = args.Style;
        _cursorBlink = args.Blink;
    }

    internal string SessionId { get; }

    internal bool IsDefault { get; }

    internal TerminalSessionKind Kind => _kind;

    /// <summary>The number of terminal screen writes the pump has applied; tests wait on it to observe
    /// emitted output without consuming frame state.</summary>
    internal long ScreenVersion => Volatile.Read(ref _screenVersion);

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
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
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

    internal TerminalInfo GetSnapshot()
    {
        lock (_stateGate)
        {
            return new TerminalInfo(
                SessionId,
                GetWireStateLocked(),
                ToWireKind(_kind),
                _exitCode);
        }
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = GetState();
            if (current is TerminalSessionState.Idle or TerminalSessionState.Busy) return;

            if (current == TerminalSessionState.Faulted) throw CreateFaultedException();
            if (current == TerminalSessionState.Completed)
            {
                // A one-shot session is a one-run object: its single-run bookkeeping
                // (exitObserved/exitCode/exitCompletion) is spent, so a restart would spawn
                // a second child whose exit can never be recorded. Reject instead of
                // corrupting the session — the model's remedy is to start a new session.
                throw new AgentToolException(
                    "terminal_session_completed",
                    $"The one-shot terminal session '{SessionId}' has already completed and cannot be restarted.");
            }

            SetState(TerminalSessionState.Starting);
            try
            {
                EnsureExecutableAllowed();
                var environment = ProcessEnvironment.Capture(_policy);
                // The spawn itself is a synchronous fork/exec; the start budget bounds how long
                // StartAsync (and with it the lifecycle gate) can stall on a pathological PTY
                // allocation. Exited is attached inside the task — before the awaiting caller
                // resumes — so a child that dies immediately cannot fire into the void; a spawn
                // that finishes after its budget is disposed, not leaked.
                var spawnTask = Task.Factory.StartNew(
                    () =>
                    {
                        var spawned = _factory.Start(
                            _executable,
                            _launchArguments,
                            _workingDirectory,
                            environment,
                            _options.Columns,
                            _options.Rows);
                        spawned.GracefulExitTimeout = _options.GracefulExitTimeout;
                        spawned.Exited += OnProcessExited;
                        return spawned;
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                ITerminalProcess started;
                try
                {
                    started = await spawnTask
                        .WaitAsync(_options.StartupTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _ = ReclaimLateSpawnAsync(spawnTask);
                    throw new AgentToolException(
                        "terminal_start_failed",
                        $"The terminal session '{SessionId}' exceeded its " +
                        $"{_options.StartupTimeout.TotalSeconds:0.###}s start budget.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Caller cancellation must surface as cancellation, not as a start failure
                    // (invariant 9); the not-yet-adopted spawn is reclaimed in case it lands late.
                    _ = ReclaimLateSpawnAsync(spawnTask);
                    throw;
                }

                lock (_terminalGate)
                {
                    _terminal.Reset();
                    _terminal.Resize(_options.Columns, _options.Rows);
                    _screenVersion = 0;
                    _cursorStyle = CursorStyle.Block;
                    _cursorBlink = false;
                }

                Volatile.Write(ref _process, started);
                // The reaper may have raised Exited before the in-task handler attach; the
                // published exit fields are the durable record, so replay that observation once.
                if (started.ExitCode is not null || started.TerminationSignal is not null)
                    OnProcessExited(started);

                _lastRowTexts = null;
                StartPump(started);
                // The child may have exited while the spawn awaited: a one-shot is then already
                // completed and a persistent session already faulted; only a live child turns idle.
                SetStateIdleIfStarting();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(TerminalSessionState.Faulted);
                throw;
            }
            catch (AgentToolException)
            {
                SetState(TerminalSessionState.Faulted);
                throw;
            }
            catch (Exception exception)
            {
                SetState(TerminalSessionState.Faulted);
                _logger.LogWarning(exception, "Could not start terminal session {SessionId}.", SessionId);
                throw new AgentToolException(
                    "terminal_start_failed",
                    $"The terminal session '{SessionId}' could not be started: {GetSafeMessage(exception)}");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Disposes a spawn that completes after its start budget elapsed, so the late
    /// PTY child is reaped instead of leaking. A spawn that faults instead has no process to
    /// reclaim; the fault is observed here so it cannot vanish.</summary>
    private async Task ReclaimLateSpawnAsync(Task<ITerminalProcess> spawnTask)
    {
        ITerminalProcess? late;
        try
        {
            late = await spawnTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "The terminal spawn for {SessionId} failed after its start budget had already elapsed.",
                SessionId);
            return;
        }

        await late.DisposeAsync().ConfigureAwait(false);
    }

    internal async Task<TerminalInputResult> ExecuteInputAsync(
        TerminalInputBatch batch,
        TerminalSnapshotRequest snapshotRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (GetState() == TerminalSessionState.Faulted) throw CreateFaultedException();

            var current = _process ?? throw CreateFaultedException();
            SetState(TerminalSessionState.Busy);
            var executedLines = 0;
            int? failedLine = null;
            try
            {
                foreach (var operation in batch.Operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = EncodeOperationLocked(operation);
                    try
                    {
                        // The write links the session lifetime so a pty that stops draining its input queue
                        // cannot hold executionGate forever and block close/dispose; the pty write has no
                        // internal timeout and blocks for capacity.
                        using var write = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            _lifetime.Token);
                        await current.BaseStream.WriteAsync(bytes, write.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (IsInputFailure(exception))
                    {
                        failedLine = operation.Line;
                        _logger.LogWarning(
                            exception,
                            "Terminal session {SessionId} rejected input on line {Line}.",
                            SessionId,
                            operation.Line);
                        break;
                    }

                    executedLines++;
                }
            }
            finally
            {
                // Only busy executions return to idle; a fault observed mid-execution stays faulted.
                lock (_stateGate)
                {
                    if (_state == TerminalSessionState.Busy)
                    {
                        _state = TerminalSessionState.Idle;
                        _stateChanged.TrySetResult();
                    }
                }
            }

            var settled = await WaitForSettleAsync(cancellationToken).ConfigureAwait(false);
            var frame = CaptureFrame(snapshotRequest);
            return new TerminalInputResult(
                executedLines,
                failedLine,
                settled,
                frame);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    internal async Task<TerminalPasteResult> PasteAsync(
        string text,
        TerminalSnapshotRequest snapshotRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (GetState() == TerminalSessionState.Faulted) throw CreateFaultedException();

            if (ContainsForbiddenPasteControl(text))
                throw new AgentToolException(
                    "terminal_invalid_paste",
                    "Pasted text cannot contain an escape byte or control characters other than tab and newlines.");

            var current = _process ?? throw CreateFaultedException();
            SetState(TerminalSessionState.Busy);
            bool bracketed;
            try
            {
                lock (_terminalGate)
                {
                    bracketed = _terminal.BracketedPasteMode;
                }

                var payload = bracketed ? "\x1b[200~" + text + "\x1b[201~" : text;
                var bytes = Encoding.UTF8.GetBytes(payload);
                // See ExecuteInputAsync: the write links the session lifetime so a child that stops
                // draining its input queue cannot hold executionGate forever and block close/dispose.
                using var write = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token);
                await current.BaseStream.WriteAsync(bytes, write.Token).ConfigureAwait(false);
            }
            finally
            {
                // Only busy executions return to idle; a fault observed mid-execution stays faulted.
                lock (_stateGate)
                {
                    if (_state == TerminalSessionState.Busy)
                    {
                        _state = TerminalSessionState.Idle;
                        _stateChanged.TrySetResult();
                    }
                }
            }

            var settled = await WaitForSettleAsync(cancellationToken).ConfigureAwait(false);
            var frame = CaptureFrame(snapshotRequest);
            return new TerminalPasteResult(
                bracketed,
                settled,
                frame);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    internal TerminalSnapshotResult Snapshot(TerminalSnapshotRequest snapshotRequest)
    {
        ArgumentNullException.ThrowIfNull(snapshotRequest);
        ThrowIfDisposed();
        var frame = CaptureFrame(snapshotRequest);
        lock (_stateGate)
        {
            return new TerminalSnapshotResult(
                _exitCode,
                frame);
        }
    }

    /// <summary>Runs a one-shot command with a deadline. Returns <c>completed</c> with the exit code and final
    /// frame when the child exits in time, <c>running</c> with the session as a pollable handle on timeout, and
    /// throws OperationCanceledException (after terminating the child) when the caller cancels.</summary>
    internal async Task<TerminalRunResult> RunOnceAsync(
        TimeSpan timeout,
        TerminalSnapshotRequest snapshotRequest,
        CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (GetState() == TerminalSessionState.Faulted) throw CreateFaultedException();

            // The pump owns the pty reads; the pty library's own wait drains the stream with a sync read,
            // which races the pump's pending async read, so completion is event-driven via the reaper's
            // Exited signal instead.
            try
            {
                await _exitCompletion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new TerminalRunResult(
                    SessionId,
                    GetWireState(),
                    null,
                    false,
                    CaptureFrame(snapshotRequest));
            }

            // The child is reaped; let the pump drain the remaining output before capturing the final frame.
            await WaitForSettleAsync(cancellationToken).ConfigureAwait(false);
            int? completedExitCode;
            lock (_stateGate)
            {
                completedExitCode = _exitCode;
            }

            return new TerminalRunResult(
                SessionId,
                GetWireState(),
                completedExitCode,
                true,
                CaptureFrame(snapshotRequest));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller (or the run) cancelled: terminate the child before surfacing so no orphan is left.
            var current = _process;
            if (current is not null)
                try
                {
                    await current.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not terminate cancelled one-shot terminal session {SessionId}.",
                        SessionId);
                }

            throw;
        }
        finally
        {
            _executionGate.Release();
        }
    }

    /// <summary>Waits until the session's child process exits. Resolve-once semantics: the
    /// returned task completes even when the wait starts after the exit was observed. This is
    /// the completion signal behind the task plane's wait contract (ADR 0030 decision 5).</summary>
    internal Task WaitExitedAsync(CancellationToken cancellationToken)
    {
        return _exitCompletion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Waits until the output pump has applied at least <paramref name="minimum"/>
    /// screen writes. Signal-driven: the wait wakes on each screen-change notification and
    /// re-checks the version under the same lock that swaps the notification source, so no
    /// wakeup can be lost; a notification without a version bump (the child-exit path notifies
    /// without applying output) merely re-arms the wait.</summary>
    internal async Task WaitForScreenVersionAsync(long minimum, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task signal;
            lock (_signalGate)
            {
                if (Volatile.Read(ref _screenVersion) >= minimum) return;

                signal = _screenChanged.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<TerminalInterruptResult> InterruptAsync(
        TerminalSnapshotRequest snapshotRequest,
        CancellationToken cancellationToken)
    {
        var batch = new TerminalInputBatch(
            [new TerminalKeysOperation(1, [new TerminalKey(null, new TerminalCharKey('c', TerminalKeyModifiers.Control))])],
            1);
        var result = await ExecuteInputAsync(batch, snapshotRequest, cancellationToken).ConfigureAwait(false);
        return new TerminalInterruptResult(
            result.Settled,
            result.Frame);
    }

    private async Task CloseAsync(CancellationToken cancellationToken)
    {
        await _lifetime.CancelAsync();
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (GetState() == TerminalSessionState.Closed) return;

                SetState(TerminalSessionState.Closing);
                await StopPumpAsync().ConfigureAwait(false);
                var current = _process;
                _process = null;
                if (current is not null)
                    try
                    {
                        // Disposal runs the graceful ladder: graceful close, grace window, force kill, reaper wait.
                        await current.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(
                            exception,
                            "Could not dispose terminal session {SessionId} cleanly.",
                            SessionId);
                    }

                // Clearing the process field above detaches the reaper's Exited event, so this path
                // must settle the one-shot's exit signal itself: a waiter armed on the session's exit
                // (the task plane's wait, ADR 0030 decision 5) has to observe the close instead of
                // blocking to its own timeout, and an already-terminal session's signal is untouched.
                if (_kind == TerminalSessionKind.OneShot)
                    _exitCompletion.TrySetResult();

                SetState(TerminalSessionState.Closed);
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

    private void OnProcessExited(ITerminalProcess exited)
    {
        // Fired on the pty reaper thread; must not block. The reaper event — not stream EOF — is
        // the reliable child-exit signal: the pty read may stay open while a background job still
        // holds the terminal even though the shell itself exited. A persistent session faults
        // because its completion is undefined and the shell is gone; a one-shot command completes
        // by definition. A Unix signal termination has no exit code; the signal is logged and the
        // snapshot reports completion with a null exit code rather than a synthetic one.
        // Exits are ignored for any process the session did not adopt (a spawn that lost its
        // start budget races and is disposed unowned), and observed exactly once across the
        // event path and the post-spawn catch-up replay.
        if (!ReferenceEquals(exited, Volatile.Read(ref _process))) return;
        if (Interlocked.Exchange(ref _exitObserved, 1) != 0) return;

        lock (_stateGate)
        {
            // The state guard suppresses the state transition only, never the exit signal below:
            // a close in flight owns the terminal state, but an exit observed while it is in
            // flight is still a real child exit, and a waiter armed on the one-shot's task
            // resource must not be left to its own timeout (ADR 0030 decision 5).
            if (_state is TerminalSessionState.Closing or TerminalSessionState.Closed)
            {
                if (_kind == TerminalSessionKind.OneShot)
                    _exitCompletion.TrySetResult();

                return;
            }

            if (_kind == TerminalSessionKind.OneShot)
            {
                if (_state != TerminalSessionState.Completed)
                {
                    _state = TerminalSessionState.Completed;
                    _exitCode = exited.ExitCode;
                }
            }
            else
            {
                _state = TerminalSessionState.Faulted;
            }

            _stateChanged.TrySetResult();
        }

        if (exited.TerminationSignal is { } signal)
            _logger.LogWarning(
                "Terminal session {SessionId} child was terminated by signal {Signal}.",
                SessionId,
                signal);

        if (_kind == TerminalSessionKind.OneShot)
            _exitCompletion.TrySetResult();

        NotifyScreenChanged();
        if (_kind == TerminalSessionKind.Persistent)
            _logger.LogWarning("Terminal session {SessionId} child exited unexpectedly.", SessionId);
    }

    private byte[] EncodeOperationLocked(TerminalInputOperation operation)
    {
        // Key encoding reads terminal mode flags (application cursor keys, keypad) that the pump writes
        // under terminalGate; encode under the same lock so the byte sequence always reflects one screen state.
        lock (_terminalGate)
        {
            return operation switch
            {
                TerminalTextOperation text => Encoding.UTF8.GetBytes(text.Text),
                TerminalKeysOperation keys => _keyEncoder.Encode(keys.Keys),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };
        }
    }

    private void StartPump(ITerminalProcess started)
    {
        _pumpLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pump = PumpAsync(started, _pumpLifetime.Token);
    }

    private async Task PumpAsync(ITerminalProcess started, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[8_192];
            var chars = new char[8_192];
            var decoder = _utf8.GetDecoder();
            while (true)
            {
                var count = await started.BaseStream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0) break;

                lock (_terminalGate)
                {
                    var charCount = decoder.GetChars(buffer, 0, count, chars, 0);
                    _terminal.Write(new string(chars, 0, charCount));
                    _screenVersion++;
                }

                NotifyScreenChanged();
            }

            if (Volatile.Read(ref _disposeState) == 0 &&
                GetState() is not (TerminalSessionState.Closing or TerminalSessionState.Closed))
            {
                if (_kind == TerminalSessionKind.Persistent)
                {
                    // The pty stream ended without a graceful close: the child is gone.
                    MarkFaulted();
                    _logger.LogWarning("Terminal session {SessionId} output ended unexpectedly.", SessionId);
                }

                // For a one-shot session the Exited handler already recorded the completion.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MarkFaulted();
            _logger.LogWarning(exception, "Terminal session {SessionId} output pump failed.", SessionId);
        }
    }

    private async Task<bool> WaitForSettleAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.SettleTimeout;
        while (true)
        {
            var version = Volatile.Read(ref _screenVersion);
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;

            try
            {
                await WaitForScreenChangeAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The screen may have changed in the instant the wait timed out; re-check before settling.
                if (Volatile.Read(ref _screenVersion) == version) return true;
            }
        }
    }

    private Task WaitForScreenChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task signal;
        lock (_signalGate)
        {
            signal = _screenChanged.Task;
        }

        return signal.WaitAsync(timeout, cancellationToken);
    }

    private void NotifyScreenChanged()
    {
        lock (_signalGate)
        {
            _screenChanged.TrySetResult();
            _screenChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private TerminalFrame CaptureFrame(TerminalSnapshotRequest request)
    {
        var maximumCharacters = request.MaxCharacters ?? _options.MaxSnapshotCharacters;
        lock (_terminalGate)
        {
            var buffer = _terminal.Buffer;
            var columns = _terminal.Cols;
            var rows = _terminal.Rows;
            var baseline = _lastRowTexts ?? new string[rows];
            var full = request.Full is true || _lastRowTexts is null;
            var cursorVisible = buffer.Y >= buffer.YDisp && _terminal.CursorVisible;
            var cursorRow = cursorVisible ? buffer.Y - buffer.YDisp : (int?)null;
            var rowsChanged = new List<TerminalScreenRow>();
            var characterCount = 0;
            var truncated = false;
            var omitted = 0;
            // Rows not delivered in a truncated frame keep their previous baseline so the next diff is stable;
            // rows that are delivered (fully or truncated) update the baseline to exactly what the frame carried.
            var currentRows = (string[])baseline.Clone();
            for (var row = 0; row < rows; row++)
            {
                var line = buffer.GetLine(buffer.YDisp + row);
                var text = line is null
                    ? string.Empty
                    : line.TranslateToString(trimRight: true, 0, Math.Min(columns, line.Length));

                if (truncated)
                {
                    // The budget is exhausted: only the cursor row is still delivered, truncated to the
                    // remaining budget (possibly empty), so the frame stays bounded and anchored.
                    if (row != cursorRow) continue;
                    var remaining = Math.Max(0, maximumCharacters - characterCount);
                    text = text[..Math.Min(text.Length, remaining)];
                }
                else if (characterCount + text.Length > maximumCharacters)
                {
                    truncated = true;
                    var remaining = Math.Max(0, maximumCharacters - characterCount);
                    text = text[..Math.Min(text.Length, remaining)];
                    omitted = EstimateOmittedCharacters(columns, row, rows);
                }

                currentRows[row] = text;
                characterCount += text.Length;
                // A diff frame always carries the cursor row so the cursor's content window stays anchored.
                if (full || row == cursorRow || !string.Equals(text, baseline[row], StringComparison.Ordinal))
                    rowsChanged.Add(new TerminalScreenRow(row, text));
            }

            _lastRowTexts = currentRows;

            var version = Volatile.Read(ref _screenVersion);
            return new TerminalFrame(
                version,
                columns,
                rows,
                CaptureCursorLocked(buffer),
                _terminal.IsAlternateBufferActive,
                rowsChanged,
                full,
                truncated,
                omitted);
        }
    }

    private TerminalCursor CaptureCursorLocked(TerminalBuffer buffer)
    {
        if (buffer.Y < buffer.YDisp || !_terminal.CursorVisible)
            return new TerminalCursor(0, 0, false, ToCursorStyleName(_cursorStyle), _cursorBlink, "", "", false);

        var row = buffer.Y - buffer.YDisp;
        var (left, right, unambiguous) = BuildCursorWindowLocked(buffer, row, buffer.X);
        return new TerminalCursor(row, buffer.X, true, ToCursorStyleName(_cursorStyle), _cursorBlink, left, right, unambiguous);
    }

    /// <summary>Builds the minimal left/right content window that uniquely locates the cursor gap within its row.
    /// Left and right grow together by total budget so the returned window is the shortest unambiguous reference;
    /// a row too repetitive to disambiguate falls back to the maximum windows with <c>Unambiguous=false</c>.</summary>
    private (string Left, string Right, bool Unambiguous) BuildCursorWindowLocked(
        TerminalBuffer buffer,
        int row,
        int column)
    {
        if (buffer.GetLine(buffer.YDisp + row) is not { } line) return ("", "", false);

        var contentLength = line.GetTrimmedLength();
        var rowText = line.TranslateToString(trimRight: true, 0, Math.Min(buffer.Cols, contentLength));
        var maxLeft = Math.Min(CursorContextWindow, column);
        var maxRight = Math.Min(
            CursorContextWindow,
            Math.Max(0, Math.Min(buffer.Cols, contentLength) - column));
        var atLineStart = column == 0;
        var atLineEnd = column >= contentLength;

        for (var budget = 1; budget <= maxLeft + maxRight; budget++)
            for (var leftLength = 0; leftLength <= Math.Min(budget, maxLeft); leftLength++)
            {
                var rightLength = budget - leftLength;
                if (rightLength < 0 || rightLength > maxRight) continue;

                // The window is the "L | R" gap: one side may be empty only when the cursor is at that
                // row edge. Otherwise a one-sided window makes an empty side ambiguous with a line end.
                if ((leftLength == 0 && rightLength == 0) ||
                    (leftLength == 0 && !atLineStart) ||
                    (rightLength == 0 && !atLineEnd))
                    continue;

                var left = ReadCells(line, column - leftLength, leftLength);
                var right = ReadCells(line, column, rightLength);
                if (CountBoundaryOccurrences(rowText, left, right) == 1)
                    return (left, right, true);
            }

        return (ReadCells(line, column - maxLeft, maxLeft), ReadCells(line, column, maxRight), false);
    }

    /// <summary>Reads <paramref name="count"/> cells starting at <paramref name="start"/>, keeping wide-character
    /// continuations (empty content cells) as part of the window.</summary>
    private static string ReadCells(BufferLine line, int start, int count)
    {
        var builder = new StringBuilder();
        for (var index = start; index < start + count; index++)
            builder.Append(line[index].Content);

        return builder.ToString();
    }

    /// <summary>Counts the row positions where <paramref name="left"/> ends exactly as <paramref name="right"/> begins.
    /// This is the boundary count, not the count of the concatenated window: for a periodic row such as
    /// <c>aaaaaaaaaa</c> the window may appear once while several internal gaps still match both sides.</summary>
    private static int CountBoundaryOccurrences(string text, string left, string right)
    {
        var count = 0;
        for (var boundary = 0; boundary <= text.Length; boundary++)
        {
            var leftOk = left.Length == 0 ||
                         (boundary >= left.Length &&
                          text.AsSpan(boundary - left.Length, left.Length).SequenceEqual(left));
            if (!leftOk) continue;

            var rightOk = right.Length == 0 ||
                          (boundary + right.Length <= text.Length &&
                           text.AsSpan(boundary, right.Length).SequenceEqual(right));
            if (rightOk) count++;
        }

        return count;
    }

    private static string ToCursorStyleName(CursorStyle style)
    {
        return style switch
        {
            CursorStyle.Bar => "bar",
            CursorStyle.Underline => "underline",
            _ => "block"
        };
    }

    private static int EstimateOmittedCharacters(int columns, int fromRow, int totalRows)
    {
        // The remaining visible rows each hold at most Columns characters; the budget already spent
        // is unknown here, so the estimate is the capacity of the rows the frame cut.
        return Math.Max(0, totalRows - fromRow - 1) * columns;
    }

    private async Task StopPumpAsync()
    {
        var cancellation = _pumpLifetime;
        var loop = _pump;
        _pumpLifetime = null;
        _pump = null;
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

    private TerminalSessionState GetState()
    {
        lock (_stateGate)
        {
            return _state;
        }
    }

    /// <summary>Internal test observability: completes on the next state transition, so a
    /// test can await a transition instead of polling <see cref="GetSnapshot"/>. A completed
    /// source is handed back once and a fresh one installed in the same locked section; a
    /// reader that races the transition therefore observes that edge instead of waiting for
    /// a second transition that may never happen.</summary>
    internal Task StateChanged
    {
        get
        {
            lock (_stateGate)
            {
                var changed = _stateChanged.Task;
                if (changed.IsCompleted)
                    _stateChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return changed;
            }
        }
    }

    private void SetState(TerminalSessionState value)
    {
        lock (_stateGate)
        {
            _state = value;
            _stateChanged.TrySetResult();
        }
    }

    /// <summary>Moves a starting session to idle without regressing a terminal state the
    /// reaper may have already recorded while the spawn was still awaiting (a one-shot child
    /// that completed, or a persistent child that faulted, during startup).</summary>
    private void SetStateIdleIfStarting()
    {
        lock (_stateGate)
        {
            if (_state != TerminalSessionState.Starting) return;
            _state = TerminalSessionState.Idle;
            _stateChanged.TrySetResult();
        }
    }

    private void MarkFaulted()
    {
        lock (_stateGate)
        {
            if (_state is TerminalSessionState.Closing or TerminalSessionState.Closed) return;
            _state = TerminalSessionState.Faulted;
            _stateChanged.TrySetResult();
        }
    }

    private AgentToolException CreateFaultedException()
    {
        return new AgentToolException(
            "terminal_faulted",
            $"The terminal session '{SessionId}' is faulted and requires an explicit restart or close.");
    }

    /// <summary>Rejects the configured executable when the effective policy restricts <c>run</c>.
    /// The default policy (and any policy without explicit run rules) grants <c>run</c> fully, so
    /// nothing changes by default (ADR 0018 §7, decision 2); the check engages only when a layer
    /// contributes an explicit run allowlist or deny. The verdict comes from the shared
    /// general-start gate (Maieutics.Processes), so terminal and MCP server children judge
    /// alike.</summary>
    private void EnsureExecutableAllowed()
    {
        if (RunGrant.Allows(_policy, _executable))
            return;

        throw new AgentToolException(
            "terminal_start_failed",
            $"The executable '{_executable}' is not permitted by the effective policy.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    private static bool IsInputFailure(Exception exception)
    {
        return exception is IOException or ObjectDisposedException;
    }

    private static bool ContainsForbiddenPasteControl(string text)
    {
        foreach (var character in text)
            if (character == '\x1b' || (character < 0x20 && character is not ('\t' or '\n' or '\r')) || character == 0x7f)
                return true;

        return false;
    }

    private static string GetSafeMessage(Exception exception)
    {
        return exception switch
        {
            FileNotFoundException => "The configured shell executable was not found.",
            UnauthorizedAccessException => "The operating system denied access to the shell executable or workspace.",
            _ => "The terminal process could not be started."
        };
    }

    private string GetWireState()
    {
        lock (_stateGate)
        {
            return GetWireStateLocked();
        }
    }

    /// <summary>The wire state of the session. A one-shot command reports <c>running</c> while its child is
    /// alive (the internal idle state is an implementation detail); completed and terminal states pass through.</summary>
    private string GetWireStateLocked()
    {
        if (_kind == TerminalSessionKind.OneShot && _state == TerminalSessionState.Idle)
            return "running";

        return ToWireState(_state);
    }

    private static string ToWireState(TerminalSessionState value)
    {
        return value switch
        {
            TerminalSessionState.Created => "created",
            TerminalSessionState.Starting => "starting",
            TerminalSessionState.Idle => "idle",
            TerminalSessionState.Busy => "busy",
            TerminalSessionState.Completed => "completed",
            TerminalSessionState.Closing => "closing",
            TerminalSessionState.Closed => "closed",
            TerminalSessionState.Faulted => "faulted",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
    }

    private static string ToWireKind(TerminalSessionKind value)
    {
        return value switch
        {
            TerminalSessionKind.Persistent => "persistent",
            TerminalSessionKind.OneShot => "oneshot",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
    }
}
