using Ghostflyby.Pty;

namespace Maieutics.Execution;

/// <summary>The PTY child surface the terminal session needs. Product code owns this boundary so tests
/// can substitute an in-memory process and the concrete PtyProcess wrapper stays small. The surface is
/// exactly what the session consumes: the byte stream, the reaper exit signal and code, and disposal
/// (which runs the graceful-close ladder inside the PTY library).</summary>
internal interface ITerminalProcess : IAsyncDisposable
{
    /// <summary>The master side of the pseudo-terminal: reads yield child output, writes deliver input.</summary>
    Stream BaseStream { get; }

    /// <summary>The child's exit code once reaped; null while it is still running.</summary>
    int? ExitCode { get; }

    /// <summary>The grace window between a graceful close request and a force kill during disposal.</summary>
    TimeSpan GracefulExitTimeout { get; set; }

    /// <summary>Raised on the reaper thread when the child is reaped; handlers must not block.</summary>
    event Action<int, ITerminalProcess>? Exited;
}

internal sealed class LocalTerminalProcess : ITerminalProcess
{
    private readonly PtyProcess process;

    internal LocalTerminalProcess(PtyProcess process)
    {
        this.process = process;
        process.Exited += (code, _) => Exited?.Invoke(code, this);
    }

    public Stream BaseStream => process.BaseStream;

    public int? ExitCode => process.ExitCode;

    public TimeSpan GracefulExitTimeout
    {
        get => process.GracefulExitTimeout;
        set => process.GracefulExitTimeout = value;
    }

    public event Action<int, ITerminalProcess>? Exited;

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
