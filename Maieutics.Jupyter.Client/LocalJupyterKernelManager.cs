using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public interface IJupyterKernelManager : IAsyncDisposable
{
    IJupyterClient Client { get; }

    /// <summary>Gets the operating system process id of the local child kernel, or null when the kernel is not local.</summary>
    int? ProcessId { get; }

    Task InterruptAsync(CancellationToken cancellationToken = default);

    Task RestartAsync(CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);

    Task TerminateAsync(CancellationToken cancellationToken = default);
}

public sealed record LocalJupyterKernelManagerOptions
{
    /// <summary>Gets the child kernel working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets whether the child starts without the host process environment.</summary>
    public bool ClearInheritedEnvironment { get; init; }

    /// <summary>Gets explicit environment values applied before kernelspec values.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public string? RuntimeDirectory { get; init; }
}

/// <summary>Represents a local Jupyter kernel that failed before becoming ready.</summary>
public sealed class JupyterKernelStartupException : Exception
{
    internal JupyterKernelStartupException(
        string displayName,
        int? exitCode,
        bool timedOut,
        string standardError,
        Exception? innerException = null)
        : base(CreateMessage(displayName, exitCode, timedOut, standardError), innerException)
    {
        DisplayName = displayName;
        ExitCode = exitCode;
        TimedOut = timedOut;
        StandardError = standardError;
    }

    /// <summary>Gets the kernelspec display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the child exit code, or null when the child was still running at failure time.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets whether the configured startup timeout elapsed.</summary>
    public bool TimedOut { get; }

    /// <summary>Gets bounded and redacted child standard-error diagnostics.</summary>
    public string StandardError { get; }

    private static string CreateMessage(string displayName, int? exitCode, bool timedOut, string standardError)
    {
        var reason = timedOut
            ? "did not become ready before the startup timeout"
            : exitCode is { } code
                ? $"exited with code {code} before becoming ready"
                : "failed before becoming ready";
        return standardError.Length == 0
            ? $"Jupyter kernel '{displayName}' {reason}."
            : $"Jupyter kernel '{displayName}' {reason}. Standard error: {standardError}";
    }
}

public sealed partial class LocalJupyterKernelManager : IJupyterKernelManager
{
    private const int SigInt = 2;
    private readonly JupyterKernelSpec kernelSpec;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly LocalJupyterKernelManagerOptions options;
    private JupyterClient? client;
    private string? connectionFile;
    private int disposeState;
    private KernelProcess? process;

    private LocalJupyterKernelManager(JupyterKernelSpec kernelSpec, LocalJupyterKernelManagerOptions options)
    {
        this.kernelSpec = kernelSpec;
        this.options = options;
    }

    public IJupyterClient Client => client
                                    ?? throw new InvalidOperationException("The Jupyter kernel is not running.");

    public int? ProcessId => process?.Id;

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (string.Equals(kernelSpec.InterruptMode, "message", StringComparison.OrdinalIgnoreCase))
            {
                await RequireClient().InterruptAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            SendInterruptSignal(RequireProcess());
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync(true, false, cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync(false, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync(false, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0) return;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(false, false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
            lifecycleGate.Dispose();
        }
    }

    public static async Task<LocalJupyterKernelManager> StartAsync(
        JupyterKernelSpec kernelSpec,
        LocalJupyterKernelManagerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var manager = new LocalJupyterKernelManager(kernelSpec, options ?? new LocalJupyterKernelManagerOptions());
        try
        {
            await manager.StartCoreAsync(cancellationToken).ConfigureAwait(false);
            return manager;
        }
        catch
        {
            await manager.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        var connectionInfo = JupyterConnectionInfo.CreateLocalTcp();
        var runtimeDirectory = options.RuntimeDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(runtimeDirectory);
        connectionFile = Path.Combine(runtimeDirectory, $"maieutics-kernel-{Guid.NewGuid():N}.json");
        await connectionInfo.WriteFileAsync(connectionFile, cancellationToken).ConfigureAwait(false);

        process = StartProcess(connectionFile);
        client = await JupyterClient.ConnectAsync(connectionInfo, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(options.StartupTimeout);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var readiness = client.WaitForReadyAsync(startup.Token);
        var first = await Task.WhenAny(readiness, process.Exit).ConfigureAwait(false);
        if (ReferenceEquals(first, process.Exit))
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await startup.CancelAsync().ConfigureAwait(false);
            await ObserveStartupReadinessAsync(readiness, startup.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw process.CreateStartupException(kernelSpec.DisplayName);
        }

        try
        {
            await readiness.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw process.CreateStartupException(kernelSpec.DisplayName, true, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (process.HasExited) await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            throw process.CreateStartupException(kernelSpec.DisplayName, innerException: exception);
        }

        if (process.HasExited)
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw process.CreateStartupException(kernelSpec.DisplayName);
        }
    }

    private async Task StopCoreAsync(
        bool restart,
        bool force,
        CancellationToken cancellationToken)
    {
        var currentClient = client;
        var currentProcess = process;
        client = null;
        process = null;
        Exception? failure = null;
        using var timeout = new CancellationTokenSource(options.ShutdownTimeout);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            if (!force && currentClient is not null && currentProcess is { HasExited: false })
                await currentClient.ShutdownAsync(restart, shutdown.Token).ConfigureAwait(false);

            if (!force && currentProcess is { HasExited: false })
                await currentProcess.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (currentProcess is not null)
                try
                {
                    if (!currentProcess.HasExited) currentProcess.Kill(true);

                    await currentProcess.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (currentProcess.HasExited)
                {
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

            if (currentClient is not null)
                try
                {
                    await currentClient.DisposeAsync().AsTask().WaitAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

            currentProcess?.Dispose();
            try
            {
                DeleteConnectionFile();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is OperationCanceledException && timeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
            return;

        if (failure is not null && Volatile.Read(ref disposeState) == 0) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private KernelProcess StartProcess(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = kernelSpec.Argv[0],
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = options.WorkingDirectory ?? string.Empty
        };

        if (options.ClearInheritedEnvironment) startInfo.Environment.Clear();

        foreach (var environment in options.Environment) startInfo.Environment[environment.Key] = environment.Value;

        foreach (var argument in kernelSpec.Argv.Skip(1))
            startInfo.ArgumentList.Add(argument.Replace("{connection_file}", path, StringComparison.Ordinal));

        foreach (var environment in kernelSpec.Environment) startInfo.Environment[environment.Key] = environment.Value;

        return KernelProcess.Start(
            startInfo,
            kernelSpec.DisplayName,
            [
                path,
                .. startInfo.Environment.Values.OfType<string>(),
                .. startInfo.ArgumentList
            ]);
    }

    private static void SendInterruptSignal(Process process)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Signal-based Jupyter kernel interrupt is not supported on Windows.");

        if (Kill(process.Id, SigInt) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private JupyterClient RequireClient()
    {
        return client
               ?? throw new InvalidOperationException(
                   "The Jupyter kernel is not running.");
    }

    private Process RequireProcess()
    {
        return process?.Value
               ?? throw new InvalidOperationException("The Jupyter kernel is not running.");
    }

    private static async Task ObserveStartupReadinessAsync(Task readiness, CancellationToken cancellationToken)
    {
        try
        {
            await readiness.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Process exit is the terminal startup cause; this observes a concurrent transport failure.
        }
    }

    private void DeleteConnectionFile()
    {
        if (connectionFile is not null && File.Exists(connectionFile)) File.Delete(connectionFile);

        connectionFile = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
    }

    private sealed class KernelProcess : IDisposable
    {
        private const int MaxDiagnosticCharacters = 8 * 1024;
        private const int MaxDiagnosticLineCharacters = 1024;
        private readonly StringBuilder diagnostics = new();
        private readonly TaskCompletionSource<int> exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock gate = new();
        private readonly string[] redactions;

        private KernelProcess(ProcessStartInfo startInfo, IEnumerable<string> redactions)
        {
            Value = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            Value.Exited += OnExited;
            Value.OutputDataReceived += IgnoreOutput;
            Value.ErrorDataReceived += OnErrorDataReceived;
            this.redactions = redactions
                .Where(static value => value.Length >= 8)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .OrderByDescending(static value => value.Length)
                .ToArray();
        }

        internal Process Value { get; }

        internal int Id => Value.Id;

        internal bool HasExited => Value.HasExited;

        internal Task<int> Exit => exit.Task;

        public void Dispose()
        {
            Value.Exited -= OnExited;
            Value.OutputDataReceived -= IgnoreOutput;
            Value.ErrorDataReceived -= OnErrorDataReceived;
            Value.Dispose();
        }

        internal static KernelProcess Start(
            ProcessStartInfo startInfo,
            string displayName,
            IEnumerable<string> redactions)
        {
            var owner = new KernelProcess(startInfo, redactions);
            try
            {
                if (!owner.Value.Start())
                    throw new InvalidOperationException($"Could not start Jupyter kernel '{displayName}'.");

                owner.Value.BeginOutputReadLine();
                owner.Value.BeginErrorReadLine();
                return owner;
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        internal void Kill(bool entireProcessTree)
        {
            Value.Kill(entireProcessTree);
        }

        internal async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await Value.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            exit.TrySetResult(Value.ExitCode);
            await exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal JupyterKernelStartupException CreateStartupException(
            string displayName,
            bool timedOut = false,
            Exception? innerException = null)
        {
            int? exitCode = HasExited ? Value.ExitCode : null;
            string standardError;
            lock (gate)
            {
                standardError = diagnostics.ToString();
            }

            return new JupyterKernelStartupException(
                displayName,
                exitCode,
                timedOut,
                standardError,
                innerException);
        }

        private static void IgnoreOutput(object sender, DataReceivedEventArgs args)
        {
        }

        private void OnExited(object? sender, EventArgs args)
        {
            try
            {
                exit.TrySetResult(Value.ExitCode);
            }
            catch (InvalidOperationException)
            {
                exit.TrySetResult(-1);
            }
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is not { Length: > 0 } line) return;

            foreach (var redaction in redactions)
                line = line.Replace(
                    redaction,
                    "[redacted]",
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            var cleaned = string.Concat(line.Select(static character => char.IsControl(character) ? '?' : character));
            if (cleaned.Length > MaxDiagnosticLineCharacters) cleaned = cleaned[..MaxDiagnosticLineCharacters];

            lock (gate)
            {
                if (diagnostics.Length >= MaxDiagnosticCharacters) return;

                if (diagnostics.Length > 0) diagnostics.Append(' ');
                var remaining = MaxDiagnosticCharacters - diagnostics.Length;
                diagnostics.Append(cleaned.AsSpan(0, Math.Min(cleaned.Length, remaining)));
            }
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
