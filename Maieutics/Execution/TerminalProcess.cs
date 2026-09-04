using Ghostflyby.Pty;

namespace Maieutics.Execution;

/// <summary>The PTY child surface the terminal session needs. Product code owns this boundary so tests
/// can substitute an in-memory process and the concrete PtyProcess wrapper stays small. The surface is
/// exactly what the session consumes: the byte stream, the reaper exit signal with its terminal result
/// (exit code or Unix termination signal), and disposal (which runs the graceful-close ladder inside the
/// PTY library).</summary>
internal interface ITerminalProcess : IAsyncDisposable
{
    /// <summary>The master side of the pseudo-terminal: reads yield child output, writes deliver input.</summary>
    Stream BaseStream { get; }

    /// <summary>The child's exit code after a normal exit; null while it is running or when a Unix signal
    /// terminated it (see <see cref="TerminationSignal"/>).</summary>
    int? ExitCode { get; }

    /// <summary>The Unix signal that terminated the child; null while it is running, after a normal exit,
    /// and on Windows. Exactly one of this and <see cref="ExitCode"/> is non-null once reaped.</summary>
    int? TerminationSignal { get; }

    /// <summary>The grace window between a graceful close request and a force kill during disposal.</summary>
    TimeSpan GracefulExitTimeout { get; set; }

    /// <summary>Raised on the reaper thread when the child is reaped; the terminal result is already
    /// published on the argument. Handlers must not block.</summary>
    event Action<ITerminalProcess>? Exited;
}

internal sealed class LocalTerminalProcess : ITerminalProcess
{
    private readonly PtyProcess process;

    internal LocalTerminalProcess(PtyProcess process)
    {
        this.process = process;
        process.Exited += _ => Exited?.Invoke(this);
    }

    public Stream BaseStream => process.BaseStream;

    public int? ExitCode => process.ExitCode;

    public int? TerminationSignal => process.TerminationSignal;

    public TimeSpan GracefulExitTimeout
    {
        get => process.GracefulExitTimeout;
        set => process.GracefulExitTimeout = value;
    }

    public event Action<ITerminalProcess>? Exited;

    public ValueTask DisposeAsync()
    {
        return process.DisposeAsync();
    }
}

/// <summary>Starts PTY children from the configured shell with an allowlisted environment.</summary>
internal interface ITerminalProcessFactory
{
    /// <summary>Starts a new PTY child. The child inherits no environment beyond <paramref name="environment"/>.</summary>
    ITerminalProcess Start(
        string shell,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        int columns,
        int rows);
}

internal sealed class LocalTerminalProcessFactory : ITerminalProcessFactory
{
    public ITerminalProcess Start(
        string shell,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        int columns,
        int rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shell);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        var info = new PtyStartInfo(shell)
        {
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Column = columns,
            Row = rows,
            Environment = environment,
            InheritParentEnvironment = false
        };
        return new LocalTerminalProcess(PtyProcess.Start(info));
    }
}
