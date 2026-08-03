using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
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

public sealed class LocalJupyterKernelManager : IJupyterKernelManager
{
    private const int SigInt = 2;
    private readonly JupyterKernelSpec kernelSpec;
    private readonly LocalJupyterKernelManagerOptions options;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private Process? process;
    private JupyterClient? client;
    private string? connectionFile;
    private int disposeState;

    private LocalJupyterKernelManager(JupyterKernelSpec kernelSpec, LocalJupyterKernelManagerOptions options)
    {
        this.kernelSpec = kernelSpec;
        this.options = options;
    }

    public IJupyterClient Client => client
                                    ?? throw new InvalidOperationException("The Jupyter kernel is not running.");

    public int? ProcessId => process?.Id;

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
            await StopCoreAsync(restart: true, force: false, cancellationToken).ConfigureAwait(false);
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
            await StopCoreAsync(restart: false, force: false, cancellationToken).ConfigureAwait(false);
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
            await StopCoreAsync(restart: false, force: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(restart: false, force: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
            lifecycleGate.Dispose();
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
        await client.WaitForReadyAsync(startup.Token).ConfigureAwait(false);
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
            {
                await currentClient.ShutdownAsync(restart, shutdown.Token).ConfigureAwait(false);
            }

            if (!force && currentProcess is { HasExited: false })
            {
                await currentProcess.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (currentProcess is not null)
            {
                try
                {
                    if (!currentProcess.HasExited)
                    {
                        currentProcess.Kill(entireProcessTree: true);
                    }

                    await currentProcess.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (currentProcess.HasExited)
                {
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            if (currentClient is not null)
            {
                try
                {
                    await currentClient.DisposeAsync().AsTask().WaitAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
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
        {
            return;
        }

        if (failure is not null && Volatile.Read(ref disposeState) == 0)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private Process StartProcess(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = kernelSpec.Argv[0],
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = options.WorkingDirectory ?? string.Empty
        };

        if (options.ClearInheritedEnvironment)
        {
            startInfo.Environment.Clear();
        }

        foreach (var environment in options.Environment)
        {
            startInfo.Environment[environment.Key] = environment.Value;
        }

        foreach (var argument in kernelSpec.Argv.Skip(1))
        {
            startInfo.ArgumentList.Add(argument.Replace("{connection_file}", path, StringComparison.Ordinal));
        }

        foreach (var environment in kernelSpec.Environment)
        {
            startInfo.Environment[environment.Key] = environment.Value;
        }

        var startedProcess = Process.Start(startInfo)
                             ?? throw new InvalidOperationException(
                                 $"Could not start Jupyter kernel '{kernelSpec.DisplayName}'.");
        startedProcess.BeginOutputReadLine();
        startedProcess.BeginErrorReadLine();
        return startedProcess;
    }

    private static void SendInterruptSignal(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Signal-based Jupyter kernel interrupt is not supported on Windows.");
        }

        if (kill(process.Id, SigInt) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private JupyterClient RequireClient() => client
                                             ?? throw new InvalidOperationException(
                                                 "The Jupyter kernel is not running.");

    private Process RequireProcess() => process
                                        ?? throw new InvalidOperationException("The Jupyter kernel is not running.");

    private void DeleteConnectionFile()
    {
        if (connectionFile is not null && File.Exists(connectionFile))
        {
            File.Delete(connectionFile);
        }

        connectionFile = null;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
