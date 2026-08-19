using Maieutics.Control;
using Maieutics.DenoExecution;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoRepl;

internal interface IDenoReplSessionFactory
{
    Task<IDenoReplGeneration> StartAsync(
        string workingDirectory,
        string sessionId,
        int generation,
        CancellationToken cancellationToken);
}

internal interface IDenoReplGeneration : IAsyncDisposable
{
    IDenoReplConnection Connection { get; }

    Task Completion { get; }

    int? ExitCode { get; }

    Task ShutdownAsync(CancellationToken cancellationToken);

    Task TerminateAsync();
}

internal sealed class LocalDenoReplSessionFactory(
    DenoReplOptions options,
    ReplControlHost controlHost,
    DenoReplModule modules,
    ReplEvalWebSocketHost evalHost,
    ReplControlSessionRegistry sessionRegistry,
    ReplControlCredentialRegistry credentialRegistry,
    ILogger<DenoReplProcess> logger,
    DenoPermissionBroker broker)
    : IDenoReplSessionFactory
{
    private readonly ReplControlCredentialRegistry credentialRegistry =
        credentialRegistry ?? throw new ArgumentNullException(nameof(credentialRegistry));

    private readonly ReplControlHost controlHost =
        controlHost ?? throw new ArgumentNullException(nameof(controlHost));

    private readonly ReplEvalWebSocketHost evalHost =
        evalHost ?? throw new ArgumentNullException(nameof(evalHost));

    private readonly ILogger<DenoReplProcess> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly DenoReplModule modules = modules ?? throw new ArgumentNullException(nameof(modules));

    private readonly DenoReplOptions options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly ReplControlSessionRegistry sessionRegistry =
        sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));

    public async Task<IDenoReplGeneration> StartAsync(
        string workingDirectory,
        string sessionId,
        int generation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);

        using var timeout = new CancellationTokenSource(options.StartupTimeout);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var connection = evalHost.WaitForConnectionAsync(sessionId, generation, startup.Token);
        DenoReplProcess? process = null;
        try
        {
            process = await DenoReplProcess.StartAsync(
                new DenoReplProcessOptions(
                    options.Executable,
                    modules.MainUrl,
                    modules.ConfigFile,
                    modules.LockFile,
                    modules.ModuleDirectory,
                    workingDirectory,
                    controlHost.ControlAddress,
                    sessionId,
                    generation,
                    modules.ClientUrl,
                    controlHost.WindowsPipeName,
                    broker,
                    options.AutoInstallModuleGraph),
                logger,
                startup.Token).ConfigureAwait(false);
            sessionRegistry.Register(process.ProcessId, sessionId);

            var connected = connection;
            var completed = process.Completion;
            var first = await Task.WhenAny(connected, completed).WaitAsync(startup.Token).ConfigureAwait(false);
            if (ReferenceEquals(first, completed))
            {
                await completed.ConfigureAwait(false);
                var error = await process.StandardError.ConfigureAwait(false);
                var detail = string.IsNullOrWhiteSpace(error)
                    ? string.Empty
                    : $" stderr: {error.Trim()}";
                throw new InvalidOperationException(
                    $"The Deno REPL process exited with code {process.ExitCode} before its eval channel connected.{detail}");
            }

            var socket = await connected.ConfigureAwait(false);
            return new LocalDenoReplGeneration(
                process,
                socket,
                sessionId,
                options.ShutdownTimeout,
                sessionRegistry,
                credentialRegistry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (process is not null) await StopFailedStartAsync(process, sessionId).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (process is not null) await StopFailedStartAsync(process, sessionId).ConfigureAwait(false);
            throw new TimeoutException("The Deno REPL eval channel did not become ready before its startup timeout.");
        }
        catch
        {
            if (process is not null) await StopFailedStartAsync(process, sessionId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopFailedStartAsync(DenoReplProcess process, string sessionId)
    {
        sessionRegistry.Unregister(process.ProcessId);
        credentialRegistry.Remove(sessionId);
        await process.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class LocalDenoReplGeneration : IDenoReplGeneration
{
    private readonly ReplControlCredentialRegistry credentialRegistry;
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DenoReplProcess process;
    private readonly string sessionId;
    private readonly ReplControlSessionRegistry sessionRegistry;
    private readonly TimeSpan shutdownTimeout;
    private int disposeState;

    internal LocalDenoReplGeneration(
        DenoReplProcess process,
        ReplEvalWebSocketConnection connection,
        string sessionId,
        TimeSpan shutdownTimeout,
        ReplControlSessionRegistry sessionRegistry,
        ReplControlCredentialRegistry credentialRegistry)
    {
        this.process = process;
        Connection = connection;
        this.sessionId = sessionId;
        this.shutdownTimeout = shutdownTimeout;
        this.sessionRegistry = sessionRegistry;
        this.credentialRegistry = credentialRegistry;
        Completion = ObserveCompletionAsync();
    }

    public IDenoReplConnection Connection { get; }

    public Task Completion { get; }

    public int? ExitCode => process.ExitCode;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) == 0)
        {
            try
            {
                await StopCoreAsync(true, CancellationToken.None).ConfigureAwait(false);
                disposal.TrySetResult();
            }
            catch (Exception exception)
            {
                disposal.TrySetException(exception);
            }
        }

        await disposal.Task.ConfigureAwait(false);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        return StopCoreAsync(false, cancellationToken);
    }

    public Task TerminateAsync()
    {
        return process.StopAsync();
    }

    private async Task StopCoreAsync(bool disposing, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(shutdownTimeout);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Exception? failure = null;
        try
        {
            await Connection.ShutdownAsync(shutdown.Token).ConfigureAwait(false);
            await process.Completion.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            await process.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            sessionRegistry.Unregister(process.ProcessId);
            credentialRegistry.Remove(sessionId);
        }

        if (!disposing && failure is not null) throw failure;
    }

    private async Task ObserveCompletionAsync()
    {
        var first = await Task.WhenAny(process.Completion, Connection.Completion).ConfigureAwait(false);
        await first.ConfigureAwait(false);
    }
}
