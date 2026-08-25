using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.Permissions;
using Maieutics.Plugins;
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
    DenoPermissionBroker broker,
    IReplPolicyRegistrar? replPolicyRegistrar = null,
    PluginHostManager? pluginHosts = null)
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

    private readonly PluginHostManager? pluginHosts = pluginHosts;

    private readonly IReplPolicyRegistrar? replPolicyRegistrar = replPolicyRegistrar;

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
        DenoReplProcess? process = null;
        try
        {
            // Pre-cache the effective REPL policy before the child starts (ADR 0020 decision 1):
            // the kernel is the permission authority and the host only enforces it. With the
            // broker env var active every REPL permission check reaches the broker, so a host-
            // derived REPL report for this session must register the true policy the moment it
            // arrives — the policy is computed here and re-uses the same esbuild-wasm resolution
            // the kernel-derived start below performs. A resolution failure only disables the
            // pre-cache for this session; the start itself is unchanged.
            var policy = await DenoReplPolicyCache.PrepareAsync(
                replPolicyRegistrar,
                options.Executable,
                modules.ModuleDirectory,
                workingDirectory,
                modules.ConfigFile,
                modules.LockFile,
                controlHost.ControlAddress,
                controlHost.WindowsPipeName,
                sessionId,
                logger,
                startup.Token).ConfigureAwait(false);

            // Host-derived path (ADR 0020 B5b): the plugin host is the spawner. The kernel decides
            // the entry, the complete child env, and the static permission shell; the host derives
            // the REPL process and reports the pid, which registers the broker policy and the
            // session identity before the eval channel connects. Any failure (derive rejected,
            // host not connected, no eval channel from the host-derived child) falls back to the
            // kernel-derived path below — the dual-track keeps sessions working while the
            // migration skeleton is in place.
            if (options.HostDerivedRepl && pluginHosts is not null &&
                await TryStartHostDerivedAsync(
                    policy,
                    sessionId,
                    generation,
                    startup.Token).ConfigureAwait(false) is { } hostGeneration)
            {
                return hostGeneration;
            }

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

            var connection = evalHost.WaitForConnectionAsync(sessionId, generation, startup.Token);
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
                credentialRegistry,
                replPolicyRegistrar);
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

    /// <summary>
    ///     Derives the REPL through the plugin host and, once the <c>host.repl.spawned</c> report
    ///     has been processed (pid registered with the session registry and permission broker),
    ///     waits for the REPL's eval channel. Returns null when the host path cannot be used so the
    ///     caller falls back to the kernel-derived path (derive rejected, host not connected, send
    ///     failure). Caller cancellation and the session startup timeout propagate: a host-derived
    ///     child that does not connect the eval channel within the startup budget (the migration
    ///     skeleton's <c>process_main.ts</c> serves the actor surface, not the WebSocket eval REPL
    ///     yet) is a startup failure — the host owns that process and will emit
    ///     <c>host.repl.exited</c>, so deriving a second kernel process for the same session would
    ///     be wrong.
    /// </summary>
    private async Task<IDenoReplGeneration?> TryStartHostDerivedAsync(
        EffectivePolicy? policy,
        string sessionId,
        int generation,
        CancellationToken cancellationToken)
    {
        var derive = BuildDerivePayload(policy, sessionId, generation);
        try
        {
            var outcome = await pluginHosts!.RequestReplDeriveAsync(derive, cancellationToken).ConfigureAwait(false);
            if (outcome.Failed)
            {
                logger.LogWarning(
                    "The plugin host could not derive a REPL for session '{SessionId}': {Message}. " +
                    "Falling back to a kernel-derived REPL.",
                    sessionId,
                    outcome.Message);
                return null;
            }

            var connection = evalHost.WaitForConnectionAsync(sessionId, generation, cancellationToken);
            var socket = await connection.ConfigureAwait(false);
            return new HostDerivedDenoReplGeneration(
                socket,
                sessionId,
                options.ShutdownTimeout,
                credentialRegistry,
                replPolicyRegistrar);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "The host-derived REPL start for session '{SessionId}' failed; falling back to a kernel-derived REPL.",
                sessionId);
            return null;
        }
    }

    /// <summary>Assembles the <c>host.repl.derive</c> instruction: the materialized process entry,
    /// the complete child env (without the broker path, which the host forwards itself — B5a), the
    /// effective policy rendered as a static permission shell, and reporting enabled.</summary>
    private HostReplDerivePayload BuildDerivePayload(
        EffectivePolicy? policy,
        string sessionId,
        int generation)
    {
        return new HostReplDerivePayload(
            sessionId,
            generation,
            modules.ProcessMainUrl,
            DenoReplEnvironment.Build(
                controlHost.ControlAddress,
                sessionId,
                generation,
                modules.ClientUrl,
                controlHost.WindowsPipeName),
            policy is null ? null : DenoPermissionRenderer.BuildHostReplPermissions(policy),
            Report: true);
    }

    private async Task StopFailedStartAsync(DenoReplProcess process, string sessionId)
    {
        sessionRegistry.Unregister(process.ProcessId);
        credentialRegistry.Remove(sessionId);
        DenoReplPolicyCache.Clear(replPolicyRegistrar, sessionId);
        await process.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class LocalDenoReplGeneration : IDenoReplGeneration
{
    private readonly ReplControlCredentialRegistry credentialRegistry;
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DenoReplProcess process;
    private readonly IReplPolicyRegistrar? replPolicyRegistrar;
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
        ReplControlCredentialRegistry credentialRegistry,
        IReplPolicyRegistrar? replPolicyRegistrar)
    {
        this.process = process;
        Connection = connection;
        this.sessionId = sessionId;
        this.shutdownTimeout = shutdownTimeout;
        this.sessionRegistry = sessionRegistry;
        this.credentialRegistry = credentialRegistry;
        this.replPolicyRegistrar = replPolicyRegistrar;
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
            // This generation's policy is spent: a restart re-caches its own policy on start, and
            // a closed session must not leave a stale policy behind (ADR 0020, B4 cache lifecycle).
            DenoReplPolicyCache.Clear(replPolicyRegistrar, sessionId);
        }

        if (!disposing && failure is not null) throw failure;
    }

    private async Task ObserveCompletionAsync()
    {
        var first = await Task.WhenAny(process.Completion, Connection.Completion).ConfigureAwait(false);
        await first.ConfigureAwait(false);
    }
}

/// <summary>
///     An <see cref="IDenoReplGeneration"/> whose REPL process is owned by the plugin host (ADR
///     0020). The kernel derives the child through <c>host.repl.derive</c>; the host reports the
///     pid and owns the process lifecycle (it emits <c>host.repl.exited</c> to release the pid's
///     session identity and broker policy). The kernel therefore observes the generation through
///     the eval connection only: shutdown closes the channel, termination hard-closes it, the exit
///     code is unknown here (the host owns the process), and the session registry unregister is
///     the host's <c>exited</c> report, not this class.
/// </summary>
internal sealed class HostDerivedDenoReplGeneration : IDenoReplGeneration
{
    private readonly ReplControlCredentialRegistry credentialRegistry;
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ReplEvalWebSocketConnection connection;
    private readonly IReplPolicyRegistrar? replPolicyRegistrar;
    private readonly string sessionId;
    private readonly TimeSpan shutdownTimeout;
    private int disposeState;

    internal HostDerivedDenoReplGeneration(
        ReplEvalWebSocketConnection connection,
        string sessionId,
        TimeSpan shutdownTimeout,
        ReplControlCredentialRegistry credentialRegistry,
        IReplPolicyRegistrar? replPolicyRegistrar)
    {
        this.connection = connection;
        this.sessionId = sessionId;
        this.shutdownTimeout = shutdownTimeout;
        this.credentialRegistry = credentialRegistry ?? throw new ArgumentNullException(nameof(credentialRegistry));
        this.replPolicyRegistrar = replPolicyRegistrar;
        Completion = ObserveCompletionAsync();
    }

    public IDenoReplConnection Connection => connection;

    public Task Completion { get; }

    public int? ExitCode => null;

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
        return connection.DisposeAsync().AsTask();
    }

    private async Task StopCoreAsync(bool disposing, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(shutdownTimeout);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Exception? failure = null;
        try
        {
            await connection.ShutdownAsync(shutdown.Token).ConfigureAwait(false);
            await connection.Completion.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            credentialRegistry.Remove(sessionId);
            // This generation's policy is spent: a restart re-caches its own policy on start, and
            // a closed session must not leave a stale policy behind (ADR 0020, B4 cache lifecycle).
            // The host's host.repl.exited report releases the pid-scoped session identity and the
            // broker policy registration.
            DenoReplPolicyCache.Clear(replPolicyRegistrar, sessionId);
        }

        if (!disposing && failure is not null) throw failure;
    }

    private async Task ObserveCompletionAsync()
    {
        await connection.Completion.ConfigureAwait(false);
    }
}
