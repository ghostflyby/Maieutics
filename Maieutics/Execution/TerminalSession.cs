using System.Text;
using Microsoft.Extensions.Logging;
using Maieutics.Agent;
using Maieutics.Permissions;
using Maieutics.Processes;

namespace Maieutics.Execution;

/// <summary>Owns one PTY child, the headless terminal emulator, and the output pump that feeds it.</summary>
internal sealed class TerminalSession : IAsyncDisposable
{
    /// <summary>The maximum characters of row text kept on each side of the cursor for the content window.</summary>
    private const int CursorContextWindow = 6;

    private readonly TaskCompletionSource disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim executionGate = new(1, 1);
    private readonly ITerminalProcessFactory factory;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ILogger<TerminalSession> logger;
    private readonly TerminalOptions options;
    private readonly string workingDirectory;
    private readonly TerminalSessionKind kind;
    private readonly string executable;
    private readonly IReadOnlyList<string> launchArguments;
    private readonly Lock signalGate = new();
    private readonly Lock stateGate = new();
    private readonly Lock terminalGate = new();
    private readonly XTerm.Terminal terminal;
    private readonly TerminalKeyEncoder keyEncoder;
    private readonly TaskCompletionSource exitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private bool cursorBlink;
    private XTerm.Common.CursorStyle cursorStyle = XTerm.Common.CursorStyle.Block;
    private int disposeState;
    private int? exitCode;
    private string[]? lastRowTexts;
    private TaskCompletionSource screenChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long screenVersion;
    private ITerminalProcess? process;
    private Task? pump;
    private CancellationTokenSource? pumpLifetime;
    private TerminalSessionState state = TerminalSessionState.Created;

    internal TerminalSession(
        AgentSessionId ownerSessionId,
        string sessionId,
        bool isDefault,
        string workingDirectory,
        TerminalSessionKind kind,
        string executable,
        IReadOnlyList<string> launchArguments,
        TerminalOptions options,
        ITerminalProcessFactory factory,
        ILogger<TerminalSession> logger)
    {
        OwnerSessionId = ownerSessionId;
        SessionId = sessionId;
        IsDefault = isDefault;
        this.workingDirectory = workingDirectory;
        this.kind = kind;
        this.executable = executable;
        this.launchArguments = launchArguments;
        this.options = options;
        this.factory = factory;
        this.logger = logger;
        terminal = new XTerm.Terminal(new XTerm.Options.TerminalOptions
        {
            Cols = options.Columns,
            Rows = options.Rows,
            Scrollback = 0
        });
        // The emulator has no cursor-style getter; it only raises CursorStyleChanged (DECSCUSR). The pump
        // raises it inside terminalGate while applying output, so the handlers need no further locking.
        terminal.CursorStyleChanged += OnCursorStyleChanged;
        keyEncoder = new TerminalKeyEncoder(terminal);
    }

    private void OnCursorStyleChanged(
        object? sender,
        XTerm.Events.TerminalEvents.CursorStyleChangedEventArgs args)
    {
        cursorStyle = args.Style;
        cursorBlink = args.Blink;
    }

    internal AgentSessionId OwnerSessionId { get; }

    internal string SessionId { get; }

    internal bool IsDefault { get; }

    internal TerminalSessionKind Kind => kind;

    /// <summary>The number of terminal screen writes the pump has applied; tests wait on it to observe
    /// emitted output without consuming frame state.</summary>
    internal long ScreenVersion => Volatile.Read(ref screenVersion);

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

    internal TerminalInfo GetSnapshot()
    {
        lock (stateGate)
        {
            return new TerminalInfo(
                SessionId,
                GetWireStateLocked(),
                ToWireKind(kind),
                exitCode);
        }
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = GetState();
            if (current is TerminalSessionState.Idle or TerminalSessionState.Busy) return;

            if (current == TerminalSessionState.Faulted) throw CreateFaultedException();

            SetState(TerminalSessionState.Starting);
            try
            {
                var environment = ProcessEnvironment.Capture(EffectivePolicy.Default);
                var started = factory.Start(
                    executable,
                    launchArguments,
                    workingDirectory,
                    environment,
                    options.Columns,
                    options.Rows);
                started.GracefulExitTimeout = options.GracefulExitTimeout;
                lock (terminalGate)
                {
                    terminal.Reset();
                    terminal.Resize(options.Columns, options.Rows);
                    screenVersion = 0;
                    cursorStyle = XTerm.Common.CursorStyle.Block;
                    cursorBlink = false;
                }

                lastRowTexts = null;
                process = started;
                // The pty read may stay open while a background job still holds the terminal even though the
                // shell itself exited; the reaper event is the reliable child-exit signal and marks the session
                // faulted so later input fails loudly instead of writing into the void.
                started.Exited += OnProcessExited;
                StartPump(started);
                SetState(TerminalSessionState.Idle);
            }
            catch (AgentToolException)
            {
                SetState(TerminalSessionState.Faulted);
                throw;
            }
            catch (Exception exception)
            {
                SetState(TerminalSessionState.Faulted);
                logger.LogWarning(exception, "Could not start terminal session {SessionId}.", SessionId);
                throw new AgentToolException(
                    "terminal_start_failed",
                    $"The terminal session '{SessionId}' could not be started: {GetSafeMessage(exception)}");
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    internal async Task<TerminalInputResult> ExecuteInputAsync(
        TerminalInputBatch batch,
        TerminalSnapshotRequest snapshotRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (GetState() == TerminalSessionState.Faulted) throw CreateFaultedException();

            var current = process ?? throw CreateFaultedException();
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
                            lifetime.Token);
                        await current.BaseStream.WriteAsync(bytes, write.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (IsInputFailure(exception))
                    {
                        failedLine = operation.Line;
                        logger.LogWarning(
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
                lock (stateGate)
                {
                    if (state == TerminalSessionState.Busy) state = TerminalSessionState.Idle;
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
            executionGate.Release();
        }
    }

    internal async Task<TerminalPasteResult> PasteAsync(
        string text,
        TerminalSnapshotRequest snapshotRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (GetState() == TerminalSessionState.Faulted) throw CreateFaultedException();

            if (ContainsForbiddenPasteControl(text))
                throw new AgentToolException(
                    "terminal_invalid_paste",
                    "Pasted text cannot contain an escape byte or control characters other than tab and newlines.");

            var current = process ?? throw CreateFaultedException();
            SetState(TerminalSessionState.Busy);
            var bracketed = false;
            try
            {
                lock (terminalGate)
                {
                    bracketed = terminal.BracketedPasteMode;
                }

                var payload = bracketed ? "\x1b[200~" + text + "\x1b[201~" : text;
                var bytes = Encoding.UTF8.GetBytes(payload);
                // See ExecuteInputAsync: the write links the session lifetime so a child that stops
                // draining its input queue cannot hold executionGate forever and block close/dispose.
                using var write = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.Token);
                await current.BaseStream.WriteAsync(bytes, write.Token).ConfigureAwait(false);
            }
            finally
            {
                // Only busy executions return to idle; a fault observed mid-execution stays faulted.
                lock (stateGate)
                {
                    if (state == TerminalSessionState.Busy) state = TerminalSessionState.Idle;
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
            executionGate.Release();
        }
    }

    internal TerminalSnapshotResult Snapshot(TerminalSnapshotRequest snapshotRequest)
    {
        ArgumentNullException.ThrowIfNull(snapshotRequest);
        ThrowIfDisposed();
        var frame = CaptureFrame(snapshotRequest);
        lock (stateGate)
        {
            return new TerminalSnapshotResult(
                exitCode,
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
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (GetState() == TerminalSessionState.Faulted) throw CreateFaultedException();

            // The pump owns the pty reads; the pty library's own wait drains the stream with a sync read,
            // which races the pump's pending async read, so completion is event-driven via the reaper's
            // Exited signal instead.
            try
            {
                await exitCompletion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
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
            lock (stateGate)
            {
                completedExitCode = exitCode;
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
            var current = process;
            if (current is not null)
                try
                {
                    await current.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Could not terminate cancelled one-shot terminal session {SessionId}.",
                        SessionId);
                }

            throw;
        }
        finally
        {
            executionGate.Release();
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
        await lifetime.CancelAsync();
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (GetState() == TerminalSessionState.Closed) return;

                SetState(TerminalSessionState.Closing);
                await StopPumpAsync().ConfigureAwait(false);
                var current = process;
                process = null;
                if (current is not null)
                    try
                    {
                        // Disposal runs the graceful ladder: graceful close, grace window, force kill, reaper wait.
                        await current.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Could not dispose terminal session {SessionId} cleanly.",
                            SessionId);
                    }

                SetState(TerminalSessionState.Closed);
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

    private void OnProcessExited(int exitCode, ITerminalProcess exited)
    {
        // Fired on the pty reaper thread; must not block. A persistent session faults because its
        // completion is undefined and the shell is gone; a one-shot command completes by definition.
        lock (stateGate)
        {
            if (state is TerminalSessionState.Closing or TerminalSessionState.Closed) return;
            if (kind == TerminalSessionKind.OneShot)
            {
                if (state != TerminalSessionState.Completed)
                {
                    state = TerminalSessionState.Completed;
                    this.exitCode = exited.ExitCode ?? exitCode;
                }
            }
            else
            {
                state = TerminalSessionState.Faulted;
            }
        }

        if (kind == TerminalSessionKind.OneShot)
            exitCompletion.TrySetResult();

        NotifyScreenChanged();
        if (kind == TerminalSessionKind.Persistent)
            logger.LogWarning("Terminal session {SessionId} child exited unexpectedly.", SessionId);
    }

    private byte[] EncodeOperationLocked(TerminalInputOperation operation)
    {
        // Key encoding reads terminal mode flags (application cursor keys, keypad) that the pump writes
        // under terminalGate; encode under the same lock so the byte sequence always reflects one screen state.
        lock (terminalGate)
        {
            return operation switch
            {
                TerminalTextOperation text => Encoding.UTF8.GetBytes(text.Text),
                TerminalKeysOperation keys => keyEncoder.Encode(keys.Keys),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };
        }
    }

    private void StartPump(ITerminalProcess started)
    {
        pumpLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pump = PumpAsync(started, pumpLifetime.Token);
    }

    private async Task PumpAsync(ITerminalProcess started, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[8_192];
            var chars = new char[8_192];
            var decoder = utf8.GetDecoder();
            while (true)
            {
                var count = await started.BaseStream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0) break;

                lock (terminalGate)
                {
                    var charCount = decoder.GetChars(buffer, 0, count, chars, 0);
                    terminal.Write(new string(chars, 0, charCount));
                    screenVersion++;
                }

                NotifyScreenChanged();
            }

            if (Volatile.Read(ref disposeState) == 0 &&
                GetState() is not (TerminalSessionState.Closing or TerminalSessionState.Closed))
            {
                if (kind == TerminalSessionKind.Persistent)
                {
                    // The pty stream ended without a graceful close: the child is gone.
                    MarkFaulted();
                    logger.LogWarning("Terminal session {SessionId} output ended unexpectedly.", SessionId);
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
            logger.LogWarning(exception, "Terminal session {SessionId} output pump failed.", SessionId);
        }
    }

    private async Task<bool> WaitForSettleAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + options.SettleTimeout;
        while (true)
        {
            var version = Volatile.Read(ref screenVersion);
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;

            try
            {
                await WaitForScreenChangeAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The screen may have changed in the instant the wait timed out; re-check before settling.
                if (Volatile.Read(ref screenVersion) == version) return true;
            }
        }
    }

    private Task WaitForScreenChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task signal;
        lock (signalGate)
        {
            signal = screenChanged.Task;
        }

        return signal.WaitAsync(timeout, cancellationToken);
    }

    private void NotifyScreenChanged()
    {
        lock (signalGate)
        {
            screenChanged.TrySetResult();
            screenChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private TerminalFrame CaptureFrame(TerminalSnapshotRequest request)
    {
        var maximumCharacters = request.MaxCharacters ?? options.MaxSnapshotCharacters;
        lock (terminalGate)
        {
            var buffer = terminal.Buffer;
            var columns = terminal.Cols;
            var rows = terminal.Rows;
            var baseline = lastRowTexts ?? new string[rows];
            var full = request.Full is true || lastRowTexts is null;
            var cursorVisible = buffer.Y >= buffer.YDisp && terminal.CursorVisible;
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

            lastRowTexts = currentRows;

            var version = Volatile.Read(ref screenVersion);
            return new TerminalFrame(
                version,
                columns,
                rows,
                CaptureCursorLocked(buffer),
                terminal.IsAlternateBufferActive,
                rowsChanged,
                full,
                truncated,
                omitted);
        }
    }

    private TerminalCursor CaptureCursorLocked(XTerm.Buffer.TerminalBuffer buffer)
    {
        if (buffer.Y < buffer.YDisp || !terminal.CursorVisible)
            return new TerminalCursor(0, 0, false, ToCursorStyleName(cursorStyle), cursorBlink, "", "", false);

        var row = buffer.Y - buffer.YDisp;
        var (left, right, unambiguous) = BuildCursorWindowLocked(buffer, row, buffer.X);
        return new TerminalCursor(row, buffer.X, true, ToCursorStyleName(cursorStyle), cursorBlink, left, right, unambiguous);
    }

    /// <summary>Builds the minimal left/right content window that uniquely locates the cursor gap within its row.
    /// Left and right grow together by total budget so the returned window is the shortest unambiguous reference;
    /// a row too repetitive to disambiguate falls back to the maximum windows with <c>Unambiguous=false</c>.</summary>
    private (string Left, string Right, bool Unambiguous) BuildCursorWindowLocked(
        XTerm.Buffer.TerminalBuffer buffer,
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
    private static string ReadCells(XTerm.Buffer.BufferLine line, int start, int count)
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

    private static string ToCursorStyleName(XTerm.Common.CursorStyle style)
    {
        return style switch
        {
            XTerm.Common.CursorStyle.Bar => "bar",
            XTerm.Common.CursorStyle.Underline => "underline",
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
        var cancellation = pumpLifetime;
        var loop = pump;
        pumpLifetime = null;
        pump = null;
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
        lock (stateGate)
        {
            return state;
        }
    }

    private void SetState(TerminalSessionState value)
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
            if (state is not (TerminalSessionState.Closing or TerminalSessionState.Closed))
                state = TerminalSessionState.Faulted;
        }
    }

    private AgentToolException CreateFaultedException()
    {
        return new AgentToolException(
            "terminal_faulted",
            $"The terminal session '{SessionId}' is faulted and requires an explicit restart or close.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
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
        lock (stateGate)
        {
            return GetWireStateLocked();
        }
    }

    /// <summary>The wire state of the session. A one-shot command reports <c>running</c> while its child is
    /// alive (the internal idle state is an implementation detail); completed and terminal states pass through.</summary>
    private string GetWireStateLocked()
    {
        if (kind == TerminalSessionKind.OneShot && state == TerminalSessionState.Idle)
            return "running";

        return ToWireState(state);
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
