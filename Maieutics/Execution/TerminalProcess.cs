using Ghostflyby.Pty;

namespace Maieutics.Execution;

/// <summary>The PTY child surface the terminal session needs. Product code owns this boundary so tests
/// can substitute an in-memory process and the concrete PtyProcess wrapper stays small.</summary>
internal interface ITerminalProcess : IAsyncDisposable
{
    /// <summary>The master side of the pseudo-terminal: reads yield child output, writes deliver input.</summary>
    Stream BaseStream { get; }

    /// <summary>The operating-system process identifier.</summary>
    int Pid { get; }

    /// <summary>Whether the child has been reaped.</summary>
    bool HasExited { get; }

    /// <summary>The grace window between a graceful close request and a force kill during disposal.</summary>
    TimeSpan GracefulExitTimeout { get; set; }

    /// <summary>Raised on the reaper thread when the child is reaped; handlers must not block.</summary>
    event Action<int, ITerminalProcess>? Exited;

    /// <summary>Requests a graceful close (SIGHUP / CTRL_CLOSE_EVENT); the child decides how to handle it.</summary>
    void RequestClose();

    /// <summary>Force-terminates the child without cleanup.</summary>
    void Kill();

    /// <summary>Resizes the terminal; the child re-lays out immediately (SIGWINCH / ConPTY).</summary>
    void Resize(int columns, int rows);

    /// <summary>Waits for the child to be reaped, returning whether it exited within the timeout.</summary>
    Task<bool> WaitForExitAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);
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

    public int Pid => process.Pid;

    public bool HasExited => process.HasExited;

    public TimeSpan GracefulExitTimeout
    {
        get => process.GracefulExitTimeout;
        set => process.GracefulExitTimeout = value;
    }

    public event Action<int, ITerminalProcess>? Exited;

    public void RequestClose()
    {
        process.RequestClose();
    }

    public void Kill()
    {
        process.Kill();
    }

    public void Resize(int columns, int rows)
    {
        process.Resize(columns, rows);
    }

    public Task<bool> WaitForExitAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return process.WaitForExitAsync(timeout, cancellationToken);
    }

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

/// <summary>The allowlisted environment variables a terminal child may inherit, mirroring the Deno REPL
/// policy. Provider credentials never cross into a shell child.</summary>
internal static class TerminalEnvironment
{
    internal const string TermName = "xterm-256color";

    private static readonly string[] AllowedEnvironmentNames =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",
        "LOCALAPPDATA",
        "APPDATA",
        "TMPDIR",
        "TMP",
        "TEMP",
        "LANG",
        "LC_ALL",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "SYSTEMROOT",
        "WINDIR",
        "COMSPEC",
        "PATHEXT",
        "TERM"
    ];

    internal static IReadOnlyDictionary<string, string?> Capture()
    {
        var result = new Dictionary<string, string?>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in AllowedEnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) result[name] = value;
        }

        result["TERM"] = TermName;
        return result;
    }
}
