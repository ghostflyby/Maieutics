using Maieutics.Agent;
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
        AgentSessionId ownerSessionId,
        int generation,
        CancellationToken cancellationToken);
}

internal interface IDenoReplGeneration : IAsyncDisposable
{
    IDenoReplConnection Connection { get; }

    /// <summary>Resolves the generation's output frame stream once the binary output endpoint
    /// attaches during startup. Null when the generation has no output connection (isolated unit
    /// harnesses).</summary>
    Task<IAsyncEnumerable<ReplOutputFrame>>? OutputEvents { get; }

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
    ReplOutputWebSocketHost outputHost,
    ReplControlSessionRegistry sessionRegistry,
    ReplControlCredentialRegistry credentialRegistry,
    ILogger<DenoReplProcess> logger,
    DenoPermissionBroker broker,
    PermissionPolicyAcquirer acquirer,
    PluginHostManager? pluginHosts = null,
    IReplPolicyRegistrar? replPolicyRegistrar = null,
    ReplOwnerPolicyCache? ownerPolicyCache = null,
    string? processSandboxMode = null)
    : IDenoReplSessionFactory
{
    private readonly ReplControlCredentialRegistry _credentialRegistry =
        credentialRegistry ?? throw new ArgumentNullException(nameof(credentialRegistry));

    private readonly ReplControlHost _controlHost =
        controlHost ?? throw new ArgumentNullException(nameof(controlHost));

    private readonly ReplEvalWebSocketHost _evalHost =
        evalHost ?? throw new ArgumentNullException(nameof(evalHost));

    private readonly ILogger<DenoReplProcess> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly DenoReplModule _modules = modules ?? throw new ArgumentNullException(nameof(modules));

    private readonly DenoReplOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly ReplOutputWebSocketHost _outputHost =
        outputHost ?? throw new ArgumentNullException(nameof(outputHost));

    private readonly PluginHostManager? _pluginHosts = pluginHosts;

    private readonly IReplPolicyRegistrar? _replPolicyRegistrar = replPolicyRegistrar;

    private readonly ReplOwnerPolicyCache? _ownerPolicyCache = ownerPolicyCache;

    private readonly string? _processSandboxMode = processSandboxMode;

    private readonly ReplControlSessionRegistry _sessionRegistry =
        sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));

    public async Task<IDenoReplGeneration> StartAsync(
        string workingDirectory,
        string sessionId,
        AgentSessionId ownerSessionId,
        int generation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);

        using var timeout = new CancellationTokenSource(_options.StartupTimeout);
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
                _replPolicyRegistrar,
                _options.Executable,
                _modules.ModuleDirectory,
                workingDirectory,
                _modules.ConfigFile,
                _modules.LockFile,
                _controlHost.ControlAddress,
                _controlHost.WindowsPipeName,
                sessionId,
                ownerSessionId,
                acquirer,
                _logger,
                startup.Token).ConfigureAwait(false);
            // The owner-keyed mirror of the same snapshot: the IDE workspace bridge judges a
            // script call against exactly the policy the broker holds for this session's REPL.
            if (policy is not null)
                _ownerPolicyCache?.Cache(ownerSessionId.Value.ToString("N"), policy);

            // Host-derived path (ADR 0020 B5b): the plugin host is the spawner. The kernel decides
            // the entry, the complete child env, and the static permission shell; the host derives
            // the REPL process and reports the pid, which registers the broker policy and the
            // session identity before the eval channel connects. Any failure (derive rejected,
            // host not connected, no eval channel from the host-derived child) falls back to the
            // kernel-derived path below — the dual-track keeps sessions working while the
            // migration skeleton is in place.
            if (_options.HostDerivedRepl && _pluginHosts is not null &&
                await TryStartHostDerivedAsync(
                    policy,
                    sessionId,
                    generation,
                    workingDirectory,
                    startup.Token).ConfigureAwait(false) is { } hostGeneration)
            {
                return hostGeneration;
            }

            process = await DenoReplProcess.StartAsync(
                new DenoReplProcessOptions(
                    _options.Executable,
                    _modules.MainUrl,
                    _modules.ConfigFile,
                    _modules.LockFile,
                    _modules.ModuleDirectory,
                    workingDirectory,
                    _controlHost.ControlAddress,
                    sessionId,
                    generation,
                    _modules.ClientUrl,
                    _controlHost.WindowsPipeName,
                    broker,
                    _options.AutoInstallModuleGraph,
                    _processSandboxMode),
                _logger,
                acquirer,
                ownerSessionId,
                startup.Token).ConfigureAwait(false);
            _sessionRegistry.Register(process.ProcessId, sessionId);
            // The owner mapping backs the script tools' session-scoped gates (ADR 0042
            // decision 12): the hello identity is the REPL-local session id; the gates
            // key on the agent session the frontend announces against.
            _sessionRegistry.RegisterOwner(process.ProcessId, ownerSessionId.Value.ToString("N"));

            var connection = _evalHost.WaitForConnectionAsync(sessionId, generation, startup.Token);
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
                _outputHost,
                sessionId,
                generation,
                _options.ShutdownTimeout,
                _sessionRegistry,
                _credentialRegistry,
                _replPolicyRegistrar);
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
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var derive = BuildDerivePayload(policy, sessionId, generation, workingDirectory);
        try
        {
            var outcome = await _pluginHosts!.RequestReplDeriveAsync(derive, cancellationToken).ConfigureAwait(false);
            if (outcome.Failed)
            {
                _logger.LogWarning(
                    "The plugin host could not derive a REPL for session '{SessionId}': {Message}. " +
                    "Falling back to a kernel-derived REPL.",
                    sessionId,
                    outcome.Message);
                return null;
            }

            var connection = _evalHost.WaitForConnectionAsync(sessionId, generation, cancellationToken);
            var socket = await connection.ConfigureAwait(false);
            return new HostDerivedDenoReplGeneration(
                socket,
                _outputHost,
                sessionId,
                generation,
                _options.ShutdownTimeout,
                _credentialRegistry,
                _replPolicyRegistrar);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
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
        int generation,
        string workingDirectory)
    {
        return new HostReplDerivePayload(
            sessionId,
            generation,
            _modules.ProcessMainUrl,
            DenoReplEnvironment.Build(
                _controlHost.ControlAddress,
                sessionId,
                generation,
                _modules.ClientUrl,
                _controlHost.WindowsPipeName,
                workspaceRoot: workingDirectory,
                processSandboxMode: _processSandboxMode),
            policy is null ? null : DenoPermissionRenderer.BuildHostReplPermissions(policy),
            Report: true);
    }

    private async Task StopFailedStartAsync(DenoReplProcess process, string sessionId)
    {
        _sessionRegistry.Unregister(process.ProcessId);
        _credentialRegistry.Remove(sessionId);
        DenoReplPolicyCache.Clear(_replPolicyRegistrar, sessionId);
        await process.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class LocalDenoReplGeneration : IDenoReplGeneration
{
    private readonly ReplControlCredentialRegistry _credentialRegistry;
    private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DenoReplProcess _process;
    private readonly IReplPolicyRegistrar? _replPolicyRegistrar;
    private readonly string _sessionId;
    private readonly ReplControlSessionRegistry _sessionRegistry;
    private readonly TimeSpan _shutdownTimeout;
    private int _disposeState;

    internal LocalDenoReplGeneration(
        DenoReplProcess process,
        ReplEvalWebSocketConnection connection,
        ReplOutputWebSocketHost outputHost,
        string sessionId,
        int generation,
        TimeSpan shutdownTimeout,
        ReplControlSessionRegistry sessionRegistry,
        ReplControlCredentialRegistry credentialRegistry,
        IReplPolicyRegistrar? replPolicyRegistrar)
    {
        _process = process;
        Connection = connection;
        _sessionId = sessionId;
        _shutdownTimeout = shutdownTimeout;
        _sessionRegistry = sessionRegistry;
        _credentialRegistry = credentialRegistry;
        _replPolicyRegistrar = replPolicyRegistrar;
        Completion = ObserveCompletionAsync();
        OutputEvents = ReplOutputEvents.MapAsync(outputHost, sessionId, generation);
    }

    public IDenoReplConnection Connection { get; }

    /// <summary>The output frame stream for this generation's process. It is a session-lifetime
    /// connection: the wait resolves once the output WebSocket attaches during startup, and the
    /// stream stays open (consumed per execution) until the process ends.</summary>
    public Task<IAsyncEnumerable<ReplOutputFrame>> OutputEvents { get; }

    public Task Completion { get; }

    public int? ExitCode => _process.ExitCode;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            try
            {
                await StopCoreAsync(true, CancellationToken.None).ConfigureAwait(false);
                _disposal.TrySetResult();
            }
            catch (Exception exception)
            {
                _disposal.TrySetException(exception);
            }
        }

        await _disposal.Task.ConfigureAwait(false);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        return StopCoreAsync(false, cancellationToken);
    }

    public Task TerminateAsync()
    {
        return _process.StopAsync();
    }

    private async Task StopCoreAsync(bool disposing, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_shutdownTimeout);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Exception? failure = null;
        try
        {
            await Connection.ShutdownAsync(shutdown.Token).ConfigureAwait(false);
            await _process.Completion.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            await _process.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessionRegistry.Unregister(_process.ProcessId);
            _credentialRegistry.Remove(_sessionId);
            // This generation's policy is spent: a restart re-caches its own policy on start, and
            // a closed session must not leave a stale policy behind (ADR 0020, B4 cache lifecycle).
            DenoReplPolicyCache.Clear(_replPolicyRegistrar, _sessionId);
        }

        if (!disposing && failure is not null) throw failure;
    }

    private async Task ObserveCompletionAsync()
    {
        var first = await Task.WhenAny(_process.Completion, Connection.Completion).ConfigureAwait(false);
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
    private readonly ReplControlCredentialRegistry _credentialRegistry;
    private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ReplEvalWebSocketConnection _connection;
    private readonly IReplPolicyRegistrar? _replPolicyRegistrar;
    private readonly string _sessionId;
    private readonly TimeSpan _shutdownTimeout;
    private int _disposeState;

    internal HostDerivedDenoReplGeneration(
        ReplEvalWebSocketConnection connection,
        ReplOutputWebSocketHost outputHost,
        string sessionId,
        int generation,
        TimeSpan shutdownTimeout,
        ReplControlCredentialRegistry credentialRegistry,
        IReplPolicyRegistrar? replPolicyRegistrar)
    {
        _connection = connection;
        _sessionId = sessionId;
        _shutdownTimeout = shutdownTimeout;
        _credentialRegistry = credentialRegistry ?? throw new ArgumentNullException(nameof(credentialRegistry));
        _replPolicyRegistrar = replPolicyRegistrar;
        Completion = ObserveCompletionAsync();
        OutputEvents = ReplOutputEvents.MapAsync(outputHost, sessionId, generation);
    }

    public IDenoReplConnection Connection => _connection;

    /// <summary>The output frame stream for this generation's process. The host-derived child
    /// opens the output endpoint as part of its startup, so the wait resolves once the output
    /// WebSocket attaches.</summary>
    public Task<IAsyncEnumerable<ReplOutputFrame>> OutputEvents { get; }

    public Task Completion { get; }

    public int? ExitCode => null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            try
            {
                await StopCoreAsync(true, CancellationToken.None).ConfigureAwait(false);
                _disposal.TrySetResult();
            }
            catch (Exception exception)
            {
                _disposal.TrySetException(exception);
            }
        }

        await _disposal.Task.ConfigureAwait(false);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        return StopCoreAsync(false, cancellationToken);
    }

    public Task TerminateAsync()
    {
        return _connection.DisposeAsync().AsTask();
    }

    private async Task StopCoreAsync(bool disposing, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_shutdownTimeout);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Exception? failure = null;
        try
        {
            await _connection.ShutdownAsync(shutdown.Token).ConfigureAwait(false);
            await _connection.Completion.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _credentialRegistry.Remove(_sessionId);
            // This generation's policy is spent: a restart re-caches its own policy on start, and
            // a closed session must not leave a stale policy behind (ADR 0020, B4 cache lifecycle).
            // The host's host.repl.exited report releases the pid-scoped session identity and the
            // broker policy registration.
            DenoReplPolicyCache.Clear(_replPolicyRegistrar, _sessionId);
        }

        if (!disposing && failure is not null) throw failure;
    }

    private async Task ObserveCompletionAsync()
    {
        await _connection.Completion.ConfigureAwait(false);
    }
}

/// <summary>Maps the output connection wait into an output frame stream task. The wait itself
/// faults when the output endpoint never attaches (startup failure), surfacing on the first
/// execution through the session's <c>ResolveOutputEventsAsync</c>.</summary>
internal static class ReplOutputEvents
{
    internal static async Task<IAsyncEnumerable<ReplOutputFrame>> MapAsync(
        ReplOutputWebSocketHost outputHost,
        string sessionId,
        int generation)
    {
        ArgumentNullException.ThrowIfNull(outputHost);
        var connection = await outputHost.WaitForConnectionAsync(sessionId, generation).ConfigureAwait(false);
        return connection.Events;
    }
}
