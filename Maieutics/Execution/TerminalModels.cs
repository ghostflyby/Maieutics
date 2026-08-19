namespace Maieutics.Execution;

/// <summary>Fixed limits and defaults for the terminal tooling.</summary>
internal sealed class TerminalOptions
{
    internal const string SectionName = "Maieutics:Terminal";

    /// <summary>The program the lazy default interactive session starts with; other sessions name their own
    /// executable through <c>terminal_run</c>. The default resolves through PATH.</summary>
    public string Shell { get; set; } = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";

    /// <summary>The command-line arguments passed to the lazy default interactive session's program.</summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>The maximum number of terminal sessions one Agent session may own.</summary>
    public int MaxSessionsPerAgent { get; set; } = 4;

    /// <summary>The initial terminal width in characters.</summary>
    public int Columns { get; set; } = 120;

    /// <summary>The initial terminal height in characters.</summary>
    public int Rows { get; set; } = 30;

    /// <summary>The time allowed for a PTY child to start.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The bounded window a frame waits for the emulator to stop changing before returning.</summary>
    public TimeSpan SettleTimeout { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>The grace window between a graceful close request and a force kill.</summary>
    public TimeSpan GracefulExitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The default character budget for one screen frame.</summary>
    public int MaxSnapshotCharacters { get; set; } = 16 * 1024;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Shell);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSessionsPerAgent, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Rows, 1);
        ValidatePositive(StartupTimeout, nameof(StartupTimeout));
        ValidatePositive(SettleTimeout, nameof(SettleTimeout));
        ValidatePositive(GracefulExitTimeout, nameof(GracefulExitTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSnapshotCharacters, 1);
    }

    private static void ValidatePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(name, "The timeout must be positive.");
    }
}

/// <summary>One parsed input line awaiting execution against the session's terminal.</summary>
internal abstract record TerminalInputOperation(int Line);

/// <summary>A raw <c>t </c> line written as UTF-8 with no escape processing.</summary>
internal sealed record TerminalTextOperation(int Line, string Text) : TerminalInputOperation(Line);

/// <summary>A <c>k </c> line whose keys are encoded at write time against the live terminal mode.</summary>
internal sealed record TerminalKeysOperation(int Line, IReadOnlyList<TerminalKey> Keys) : TerminalInputOperation(Line);

/// <summary>The static parse of a <c>terminal_input</c> input batch.</summary>
internal sealed record TerminalInputBatch(IReadOnlyList<TerminalInputOperation> Operations, int LineCount);

/// <summary>Controls how a frame is produced for a snapshot-style call.</summary>
internal sealed record TerminalSnapshotRequest(bool? Full = null, int? MaxCharacters = null);

internal enum TerminalSessionKind
{
    /// <summary>An interactive shell with an undefined completion; the session survives until closed.</summary>
    Persistent,

    /// <summary>A single command run with a deadline; the session completes when the child exits.</summary>
    OneShot
}

internal enum TerminalSessionState
{
    Created,
    Starting,
    Idle,
    Busy,
    Completed,
    Closing,
    Closed,
    Faulted
}

internal sealed record TerminalInfo(
    string SessionId,
    int Generation,
    string State,
    string Cwd,
    bool IsDefault,
    string Kind,
    int Columns,
    int Rows,
    bool HasExited,
    int? ExitCode);

internal sealed record TerminalListResult(IReadOnlyList<TerminalInfo> Sessions);

internal sealed record TerminalCloseResult(string SessionId, bool Closed);

/// <summary>
///     The cursor as a deterministic row/column address plus a small content window so the model can
///     locate the insertion gap by sight instead of arithmetic. The window is unique within its row
///     when <see cref="Unambiguous"/> is true; otherwise the caller falls back to <see cref="Column"/>.
///     <see cref="Style"/> and <see cref="Blink"/> reflect the program's cursor shape (DECSCUSR): vim
///     uses a block cursor in normal mode and a bar cursor in insert mode.
/// </summary>
internal sealed record TerminalCursor(
    int Row,
    int Column,
    bool Visible,
    string Style,
    bool Blink,
    string Left,
    string Right,
    bool Unambiguous);

internal sealed record TerminalScreenRow(int Row, string Text);

/// <summary>One bounded view of the emulator screen: the changed rows, or every row when full.</summary>
internal sealed record TerminalFrame(
    long Version,
    int Columns,
    int Rows,
    TerminalCursor Cursor,
    bool AlternateBuffer,
    IReadOnlyList<TerminalScreenRow> Lines,
    bool Full,
    bool Truncated,
    int OmittedCharacters);

internal sealed record TerminalSnapshotResult(
    string SessionId,
    int Generation,
    string State,
    bool Full,
    int? ExitCode,
    TerminalFrame Frame);

/// <summary>The outcome of one one-shot command call. <c>Running</c> carries the session handle to poll;
/// <c>Completed</c> carries the exit code and the final frame.</summary>
internal sealed record TerminalRunResult(
    string SessionId,
    int Generation,
    string State,
    int? ExitCode,
    bool Settled,
    TerminalFrame Frame);

internal sealed record TerminalInputResult(
    string SessionId,
    int Generation,
    string State,
    int ExecutedLines,
    int? FailedLine,
    bool Settled,
    TerminalFrame Frame);

internal sealed record TerminalPasteResult(
    string SessionId,
    int Generation,
    string State,
    bool Bracketed,
    bool Settled,
    TerminalFrame Frame);

internal sealed record TerminalInterruptResult(
    string SessionId,
    int Generation,
    string State,
    bool Settled,
    TerminalFrame Frame);
