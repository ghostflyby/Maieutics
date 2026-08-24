using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Mcp;
using Maieutics.Permissions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins;

internal sealed record PluginRegistration(string PluginId, string ExportName, string ExtensionPoint);

/// <summary>Per-plugin lifecycle state reported by the host (backward compatible new field).</summary>
internal sealed record PluginState(string PluginId, string ExportName, string Specifier, string State, string? Failure);

internal sealed record PluginHostStatus(
    PluginHostState State,
    int PluginCount,
    int RegistrationCount,
    bool HostProcessRequired,
    bool ControlConnected);

internal enum PluginHostState
{
    NotStarted,
    Starting,
    Ready,
    Stopping,
    Stopped,
    Canceled,
    Failed,
    Exited
}

internal readonly record struct ExtensionCallOutcome(
    bool IsError,
    JsonElement? Value,
    string Code,
    string Message)
{
    public static ExtensionCallOutcome Result(JsonElement? value)
    {
        return new ExtensionCallOutcome(false, value, string.Empty, string.Empty);
    }

    public static ExtensionCallOutcome Error(string code, string message)
    {
        return new ExtensionCallOutcome(true, null, code, message);
    }
}

/// <summary>
///     Owns plugin discovery, the plugin host process, its control-channel WebSocket connection, and
///     extension point invocation routing. REPL connections stay in <see cref="ReplControlHost" />;
///     host connections are attached here so the kernel can call into plugin workers without a
///     reverse dependency. The generic host starts and stops this same instance.
/// </summary>
internal sealed class PluginHostManager(
    string pluginsRoot,
    string socketPath,
    DenoReplOptions denoOptions,
    PluginHostModule modules,
    ReplControlSessionRegistry sessionRegistry,
    ILogger<PluginHostManager> logger,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider,
    DenoPermissionBroker? broker = null)
    : IHostedService, IAsyncDisposable, IReplPolicyRegistrar
{
    private const int EnvelopeVersion = 1;
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PluginReloadDebounce = TimeSpan.FromMilliseconds(500);

    private readonly DenoReplOptions denoOptions = denoOptions ?? throw new ArgumentNullException(nameof(denoOptions));
    private readonly List<PluginDescriptor> descriptors = [];
    private readonly Lock gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock lifecycleGate = new();

    private readonly TaskCompletionSource readiness =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ILogger<PluginHostManager> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ILoggerFactory loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    private readonly PluginHostModule modules = modules ?? throw new ArgumentNullException(nameof(modules));

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ExtensionCallOutcome>> pending =
        new(StringComparer.Ordinal);

    private readonly List<PluginRegistration> registrations = [];
    private readonly List<PluginState> states = [];
    private FileSystemWatcher? pluginWatcher;
    private CancellationTokenSource? watcherDebounce;

    /// <summary>
    ///     Publishes the latest registry snapshot produced by the plugin host so tests can wait for a
    ///     registration without polling. Completed when the manager is disposed. Bounded and
    ///     drop-oldest so an unconsumed production stream retains only the newest snapshot.
    /// </summary>
    internal readonly Channel<PluginRegistration[]> RegistryChanges = Channel.CreateBounded<PluginRegistration[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly ReplControlSessionRegistry sessionRegistry =
        sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));

    private readonly DenoPermissionBroker? broker = broker;

    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private string? configPath;

    /// <summary>
    ///     Effective REPL policies keyed by session id, registered by the kernel before the host
    ///     derives a REPL for that session (ADR 0020 decision 1). The permission broker resolves
    ///     every Deno permission check the REPL child makes against the policy registered for its
    ///     pid; the host is only the enforcement point, never the authority. The kernel pre-caches
    ///     the policy at session start (<see cref="DenoReplPolicyCache.PrepareAsync"/>), so a
    ///     spawned report registers the real policy; a session with no cached policy degrades to
    ///     <see cref="EffectivePolicy.Default"/> explicitly with a warning — the fallback is a
    ///     deliberate choice, never a silent one.
    /// </summary>
    private readonly ConcurrentDictionary<string, EffectivePolicy> replPolicies = new(StringComparer.Ordinal);

    private PluginMcpCoordinator? dynamicMcpCoordinator;
    private PluginHostProcess? process;
    private Task processExitObservation = Task.CompletedTask;
    private IReadOnlySet<string> reservedToolNames = new HashSet<string>(StringComparer.Ordinal);
    private Task? starting;
    private Task? stopping;
    private WebSocket? Socket { get; set; }

    private string HostId { get; } = $"host-{Guid.NewGuid():N}"[..12];

    public ValueTask DisposeAsync()
    {
        return new ValueTask(EnsureStoppedAsync());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(stopping is not null, this);
            return starting ??= StartCoreAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop must run to completion even if the caller's token cancels: an
        // aborted stop would leave the host process and watcher behind, and
        // Host.StopAsync treats a thrown TaskCanceledException as a fatal
        // hosted-service failure. The cancellation only bounds the wait here;
        // EnsureStoppedAsync itself is cancellation-free.
        try
        {
            await EnsureStoppedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await EnsureStoppedAsync().ConfigureAwait(false);
        }
    }

    internal Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        return readiness.Task.WaitAsync(cancellationToken);
    }

    internal PluginHostStatus GetStatus()
    {
        Task? startup;
        Task? shutdown;
        PluginHostProcess? hostProcess;
        lock (lifecycleGate)
        {
            startup = starting;
            shutdown = stopping;
            hostProcess = process;
        }

        int pluginCount;
        int registrationCount;
        bool controlConnected;
        lock (gate)
        {
            pluginCount = descriptors.Count;
            registrationCount = registrations.Count;
            controlConnected = Socket?.State == WebSocketState.Open;
        }

        var hostProcessRequired = hostProcess is not null || pluginCount > 0;
        var state = startup switch
        {
            _ when readiness.Task.IsFaulted => PluginHostState.Failed,
            { IsFaulted: true } => PluginHostState.Failed,
            { IsCanceled: true } => PluginHostState.Canceled,
            _ when shutdown is { IsCompleted: true } => PluginHostState.Stopped,
            _ when shutdown is not null => PluginHostState.Stopping,
            null => PluginHostState.NotStarted,
            _ when !readiness.Task.IsCompletedSuccessfully => PluginHostState.Starting,
            _ when hostProcess?.Completion.IsCompleted == true => PluginHostState.Exited,
            _ => PluginHostState.Ready
        };
        return new PluginHostStatus(
            state,
            pluginCount,
            registrationCount,
            hostProcessRequired,
            controlConnected);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(stopping is not null, this);
                Start();
                readiness.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            readiness.TrySetCanceled(cancellationToken);
            await CleanupFailedStartupAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            readiness.TrySetException(exception);
            await CleanupFailedStartupAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CleanupFailedStartupAsync()
    {
        try
        {
            await EnsureStoppedAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Plugin host cleanup failed after startup did not complete.");
        }
    }

    public void SetReservedToolNames(IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        lock (gate)
        {
            reservedToolNames = names.ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///     Caches the effective REPL policy the kernel computed for a session, keyed by session
    ///     id. A host-derived REPL report (<c>host.repl.spawned</c>) then registers this policy
    ///     with the permission broker for the REPL's pid, preserving the kernel as the permission
    ///     authority (ADR 0020 decision 1). The kernel pre-caches the policy at session start via
    ///     <see cref="DenoReplPolicyCache"/>, which calls this method through
    ///     <see cref="IReplPolicyRegistrar"/>.
    /// </summary>
    internal void RegisterReplPolicy(string sessionId, EffectivePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(policy);
        replPolicies[sessionId] = policy;
        logger.LogDebug("Cached the effective REPL policy for session '{SessionId}'.", sessionId);
    }

    /// <summary>Removes a session's cached REPL policy, so a closed or restarted session's old
    /// policy cannot leak into the next generation (the next session start re-caches).</summary>
    internal void UnregisterReplPolicy(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (replPolicies.TryRemove(sessionId, out _))
            logger.LogDebug("Removed the cached REPL policy for session '{SessionId}'.", sessionId);
    }

    void IReplPolicyRegistrar.RegisterReplPolicy(string sessionId, EffectivePolicy policy)
    {
        RegisterReplPolicy(sessionId, policy);
    }

    void IReplPolicyRegistrar.UnregisterReplPolicy(string sessionId)
    {
        UnregisterReplPolicy(sessionId);
    }

    public IReadOnlyList<PluginRegistration> GetRegistrations(string extensionPoint)
    {
        lock (gate)
        {
            return registrations
                .Where(registration => registration.ExtensionPoint == extensionPoint)
                .ToArray();
        }
    }

    private void Start()
    {
        EnsurePluginsRoot();
        PluginGraphResult graph;
        lock (gate)
        {
            descriptors.Clear();
            var scanned = ScanPlugins();
            graph = PluginDependencyGraph.Validate(scanned);
            foreach (var exclusion in graph.Exclusions)
            {
                logger.LogWarning(
                    "Plugin '{PluginId}' is excluded: {Reason} — {Detail}.",
                    exclusion.PluginId,
                    exclusion.Reason,
                    exclusion.Detail);
            }
            descriptors.AddRange(graph.Enabled);
        }

        // The plugin root always exists (an empty deno project skeleton) and the
        // host always starts, even with zero plugins: built-in functionality is
        // planned to ship as plugins, so the host is resident and a plugin added
        // later takes effect without a kernel restart.
        configPath = WriteConfigFile(descriptors);
        process = PluginHostProcess.Start(
            new PluginHostProcessOptions(
                denoOptions.Executable,
                modules.HostUrl,
                socketPath,
                configPath,
                HostId,
                modules.SdkUrl,
                modules.WorkerEntryUrl,
                modules.ConfigFile,
                broker),
            logger);
        sessionRegistry.RegisterPluginHost(process.ProcessId, HostId);
        processExitObservation = ObserveExitAsync(process, configPath);
        StartDynamicMcpCoordinator();
        StartPluginWatcher();
    }

    /// <summary>Creates the plugins root on first start as an empty deno project skeleton:
    /// a <c>deno.json</c> (package identity + SDK import mapping) and a <c>maieutics.json</c>
    /// with no entrypoints. The directory is idempotent — an existing user project is never
    /// overwritten.</summary>
    private void EnsurePluginsRoot()
    {
        if (Directory.Exists(pluginsRoot)) return;

        Directory.CreateDirectory(pluginsRoot);
        var denoJson = Path.Combine(pluginsRoot, "deno.json");
        if (!File.Exists(denoJson))
            File.WriteAllText(
                denoJson,
                """
                {
                  "name": "@maieutics/plugins",
                  "version": "0.1.0",
                  "imports": {
                    "@maieutics/plugin-sdk": "jsr:@maieutics/plugin-sdk@^0.1"
                  }
                }
                """);
        var manifestPath = Path.Combine(pluginsRoot, "maieutics.json");
        if (!File.Exists(manifestPath))
            File.WriteAllText(
                manifestPath,
                """
                {
                  "isolation": "auto",
                  "entrypoints": {}
                }
                """);
        logger.LogInformation(
            "Created the plugin directory '{PluginsRoot}' with an empty deno project skeleton.",
            pluginsRoot);
    }

    private void StartDynamicMcpCoordinator()
    {
        var coordinator = new PluginMcpCoordinator(
            DiscoverDynamicMcpAsync,
            CreateDynamicMcpGenerationAsync,
            logger);
        coordinator.Start();
        dynamicMcpCoordinator = coordinator;
    }

    /// <summary>Watches the plugins root for manifest/permission/source changes and reloads the
    /// owning plugin's worker in-process with its latest config (the config file is re-resolved
    /// per reload, so a permission change applies without restarting the host process). Changes
    /// are debounced; only the most recent change triggers one reload.</summary>
    private void StartPluginWatcher()
    {
        // The plugins root always exists (EnsurePluginsRoot ran in Start), so the
        // watcher always runs; a plugin added later is picked up by a reload.
        var watcher = new FileSystemWatcher(pluginsRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                           NotifyFilters.DirectoryName | NotifyFilters.Size
        };
        watcher.Changed += OnPluginFileChanged;
        watcher.Created += OnPluginFileChanged;
        watcher.Deleted += OnPluginFileChanged;
        watcher.Renamed += OnPluginFileChanged;
        watcher.EnableRaisingEvents = true;
        pluginWatcher = watcher;
        logger.LogInformation("Watching '{PluginsRoot}' for plugin changes.", pluginsRoot);
    }

    private void StopPluginWatcher()
    {
        lock (gate)
        {
            watcherDebounce?.Cancel();
            watcherDebounce = null;
            pluginWatcher?.Dispose();
            pluginWatcher = null;
        }
    }

    private void OnPluginFileChanged(object sender, FileSystemEventArgs args)
    {
        // Debounce: a burst of file writes (e.g. deno fmt, git checkout) collapses into one
        // reload. The most recent change wins; earlier pending reloads are superseded.
        CancellationTokenSource debounce;
        lock (gate)
        {
            watcherDebounce?.Cancel();
            watcherDebounce = debounce = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        }

        _ = Task.Run(async () =>
        {
            if (lifetime.IsCancellationRequested) return;

            try
            {
                await Task.Delay(PluginReloadDebounce, debounce.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await ReloadChangedPluginAsync(args.FullPath).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or OperationCanceledException
                    or WebSocketException or ObjectDisposedException)
            {
                // A deleted plugin directory, an unreadable manifest, a host socket that closed
                // mid-send, or a shutdown-during-reload must not crash the watcher; the next
                // change (or the next startup) re-resolves.
                logger.LogDebug(
                    exception,
                    "Plugin reload for '{Path}' did not complete.",
                    args.FullPath);
            }
        });
    }

    /// <summary>Locates the plugin owning a changed path and ships its fresh config to the host
    /// via <c>plugin.reload</c>, so the host rebuilds that worker (and its dependents) with the
    /// latest permissions. Config/dependency changes re-resolve the descriptor; pure source edits
    /// reload with the same config so new module text is picked up.</summary>
    private async Task ReloadChangedPluginAsync(string changedPath)
    {
        // Find the most specific owning plugin: the longest root directory that
        // contains the changed path, with a path-segment boundary so a plugin
        // named "foo" never claims "foo-bar/...". Without the longest-match
        // rule the plugins-root descriptor (whose RootDirectory equals the
        // plugins root itself) would swallow every nested-plugin event.
        PluginDescriptor? owner = null;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        lock (gate)
        {
            foreach (var descriptor in descriptors)
            {
                if (!IsWithin(changedPath, descriptor.RootDirectory, comparison)) continue;
                if (owner is null ||
                    descriptor.RootDirectory.Length > owner.RootDirectory.Length)
                {
                    owner = descriptor;
                }
            }
        }

        if (owner is null) return;

        // Re-resolve the descriptor from disk so manifest/permission changes apply.
        PluginHostConfigPlugin? replacement = null;
        if (PluginManifest.TryLoad(owner.RootDirectory, out var reloaded, out _))
        {
            if (RequiresProcessIsolation(reloaded))
            {
                // A plugin that newly declares run/ffi (or switches to process
                // isolation) cannot take effect through an in-process worker
                // reload — that would require a host-process restart. Keep the
                // previous grants and surface the gap instead of silently
                // reloading with stale permissions.
                logger.LogWarning(
                    "Plugin '{PluginId}' now requires process isolation (run/ffi grants or isolation=process); " +
                    "an in-process reload cannot apply it. Restart the host process to pick up the change.",
                    owner.Id);
            }
            else
            {
                var config = BuildConfig([reloaded]);
                replacement = config.Plugins.FirstOrDefault();
            }
        }

        foreach (var worker in owner.Workers)
            await SendReloadAsync(owner.Id, worker.ExportName, replacement).ConfigureAwait(false);
    }

    private static bool IsWithin(string path, string root, StringComparison comparison)
    {
        if (path.Equals(root, comparison)) return true;
        return path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private async Task SendReloadAsync(string pluginId, string exportName, PluginHostConfigPlugin? replacement)
    {
        WebSocket? socket;
        lock (gate)
        {
            socket = Socket;
        }

        if (socket is not { State: WebSocketState.Open })
        {
            logger.LogWarning(
                "Plugin reload for '{PluginId}/{ExportName}' skipped: host not connected.",
                pluginId,
                exportName);
            return;
        }

        var payload = new PluginReloadPayload(pluginId, exportName, replacement);
        await PushAsync(
            socket,
            new ReplEnvelope(
                EnvelopeVersion,
                ReplMessageType.PluginReload,
                Guid.NewGuid().ToString("N"),
                JsonSerializer.SerializeToElement(payload, PluginHostJsonContext.Default.PluginReloadPayload)),
            lifetime.Token).ConfigureAwait(false);
        logger.LogInformation(
            "Plugin reload requested for '{PluginId}/{ExportName}'.",
            pluginId,
            exportName);
    }

    public async Task<ExtensionCallOutcome> InvokeExtensionPointAsync(
        string pluginId,
        string exportName,
        string extensionPoint,
        JsonElement? request,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ExtensionCallOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pending[correlationId] = tcs;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(InvokeTimeout);
        try
        {
            await using var registration = timeout.Token.Register(
                static state => (state as TaskCompletionSource<ExtensionCallOutcome>)?.TrySetCanceled(),
                tcs);
            var payload = new ExtensionInvokePayload(pluginId, exportName, extensionPoint, request);
            await PushAsync(
                Socket ?? throw new InvalidOperationException("The plugin host is not connected."),
                new ReplEnvelope(
                    EnvelopeVersion,
                    ReplMessageType.ExtensionInvoke,
                    correlationId,
                    JsonSerializer.SerializeToElement(payload, ReplControlJsonContext.Default.ExtensionInvokePayload)),
                timeout.Token).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(correlationId, out _);
        }
    }

    /// <summary>Acquires leases for plugin-discovered MCP servers that are ready.</summary>
    public async Task<IReadOnlyList<McpServerGeneration.McpServerLease>> AcquireDynamicMcpLeasesAsync(
        CancellationToken cancellationToken)
    {
        await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        return dynamicMcpCoordinator?.AcquireLeases() ?? [];
    }

    /// <summary>Runs the receiving loop for a plugin host WebSocket attached by the control host.</summary>
    public async Task AttachHostAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            Socket = socket;
        }

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var text = await ReplControlMessageReader
                    .ReadAsync(socket, cancellationToken)
                    .ConfigureAwait(false);
                if (text is null) break;

                HandleHostMessage(text);
            }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(socket, Socket)) Socket = null;
            }

            FailPending("The plugin host connection closed.");
        }
    }

    private Task EnsureStoppedAsync()
    {
        lock (lifecycleGate)
        {
            return stopping ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await Task.Yield();
        readiness.TrySetCanceled();
        await lifetime.CancelAsync().ConfigureAwait(false);
        FailPending("The plugin host is stopping.");
        if (dynamicMcpCoordinator is { } coordinator) await coordinator.DisposeAsync().ConfigureAwait(false);
        RegistryChanges.Writer.TryComplete();
        StopPluginWatcher();

        if (process is not null)
        {
            await process.StopAsync().ConfigureAwait(false);
            await processExitObservation.ConfigureAwait(false);
        }
        else if (configPath is not null)
            try
            {
                File.Delete(configPath);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup must not mask shutdown.
            }

        lifetime.Dispose();
    }

    private async Task ObserveExitAsync(PluginHostProcess pluginProcess, string path)
    {
        try
        {
            await pluginProcess.Completion.ConfigureAwait(false);
            logger.LogWarning(
                "Plugin host exited with code {ExitCode} (pid {ProcessId}).",
                pluginProcess.ExitCode ?? -1,
                pluginProcess.ProcessId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Plugin host exit observation failed for pid {ProcessId}.",
                pluginProcess.ProcessId);
        }
        finally
        {
            sessionRegistry.UnregisterPluginHost(pluginProcess.ProcessId);
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private List<PluginDescriptor> ScanPlugins()
    {
        // The plugins root always exists (EnsurePluginsRoot ran in Start): it is
        // itself the plugin project. Its maieutics.json declares its own
        // entrypoints (empty in the skeleton), and its deno.json imports declare
        // the installed plugins (local file/workspace packages whose directory
        // carries a maieutics.json). jsr:/npm: imports are resolved by the Deno
        // toolchain at install time, not by the kernel.
        var result = new List<PluginDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in ScanProject(pluginsRoot))
        {
            if (seen.Add(descriptor.Id))
            {
                result.Add(descriptor);
                logger.LogInformation(
                    "Discovered Maieutics plugin '{PluginName}' with {WorkerCount} worker entrypoint(s).",
                    descriptor.Name,
                    descriptor.Workers.Count);
            }
        }
        return result;
    }

    /// <summary>Scans one plugin project directory: itself, plus its imports-declared local plugins.</summary>
    private List<PluginDescriptor> ScanProject(string projectDirectory)
    {
        var result = new List<PluginDescriptor>();
        if (PluginManifest.TryLoad(projectDirectory, out var self, out _) &&
            !RequiresProcessIsolation(self))
            result.Add(self);

        foreach (var importTarget in PluginManifest.ReadLocalImportTargets(projectDirectory))
        {
            var packageDirectory = Path.GetDirectoryName(importTarget);
            if (packageDirectory is null || !Directory.Exists(packageDirectory)) continue;
            if (PluginManifest.TryLoad(packageDirectory, out var plugin, out _) &&
                !RequiresProcessIsolation(plugin))
                result.Add(plugin);
        }
        return result;
    }

    private static bool RequiresProcessIsolation(PluginDescriptor descriptor)
    {
        return string.Equals(descriptor.Isolation, "process", StringComparison.OrdinalIgnoreCase) ||
               descriptor.Permissions.Run.AllowAll || descriptor.Permissions.Run.Values.Count > 0 ||
               descriptor.Permissions.Ffi.AllowAll || descriptor.Permissions.Ffi.Values.Count > 0;
    }

    private static string WriteConfigFile(IReadOnlyList<PluginDescriptor> plugins)
    {
        var config = BuildConfig(plugins);
        var json = JsonSerializer.Serialize(config, PluginHostJsonContext.Default.PluginHostConfigFile);
        var path = Path.Combine(Path.GetTempPath(), $"mc-plugins-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Builds the host config for one plugin set. A plugin's config carries the full
    /// replacement surface (permissions, workers, dependencies) so a reload can ship a single
    /// plugin's new config to the host without rewriting the shared file.</summary>
    private static PluginHostConfigFile BuildConfig(IReadOnlyList<PluginDescriptor> plugins)
    {
        return new PluginHostConfigFile(
            plugins.Select(descriptor => new PluginHostConfigPlugin(
                descriptor.Id,
                descriptor.RootDirectory,
                [
                    .. descriptor.Workers
                        .Select(worker => new PluginHostConfigWorker(
                            worker.ExportName,
                            worker.EntryUrl,
                            SpecifierOf(descriptor, worker.ExportName)))
                ],
                ToConfigPermissions(descriptor.Permissions),
                descriptor.Dependencies.ToArray())).ToArray());
    }

    /// <summary>Canonical interop specifier of one worker entrypoint: `&lt;name&gt;/&lt;entrypoint&gt;`.</summary>
    private static string SpecifierOf(PluginDescriptor descriptor, string exportName)
    {
        return $"{descriptor.Name}/{exportName}";
    }

    private static PluginHostConfigPermissions ToConfigPermissions(PluginPermissionGrants permissions)
    {
        return new PluginHostConfigPermissions(
            ToGrant(permissions.Env),
            ToGrant(permissions.Net),
            ToGrant(permissions.Read),
            ToGrant(permissions.Write),
            ToGrant(permissions.Run),
            ToGrant(permissions.Ffi),
            ToGrant(permissions.Sys),
            ToGrant(permissions.Import));
    }

    private static JsonElement ToGrant(PluginPermissionGrant grant)
    {
        return grant.AllowAll
            ? JsonSerializer.SerializeToElement(true, PluginHostJsonContext.Default.Boolean)
            : JsonSerializer.SerializeToElement([.. grant.Values], PluginHostJsonContext.Default.StringArray);
    }

    internal void HandleHostMessage(string text)
    {
        ReplEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(text, ReplControlJsonContext.Default.ReplEnvelope)
                       ?? throw new JsonException("The envelope is null.");
        }
        catch (JsonException)
        {
            return;
        }

        switch (envelope.Type)
        {
            case ReplMessageType.ExtensionResult:
            case ReplMessageType.ExtensionError:
                CompletePending(envelope);
                break;
            case ReplMessageType.ExtensionRegistry:
                UpdateRegistry(ParsePayload<ExtensionRegistryPayload>(envelope));
                break;
            case ReplMessageType.HostReplSpawned:
                RegisterHostRepl(ParsePayload<HostReplSpawnedPayload>(envelope));
                break;
            case ReplMessageType.HostReplExited:
                UnregisterHostRepl(ParsePayload<HostReplExitedPayload>(envelope));
                break;
        }
    }

    /// <summary>
    ///     Registers a REPL process the plugin host derived (ADR 0020): the pid becomes a
    ///     session-owned control-channel identity and a broker-scoped permission subject, the
    ///     same registration effect a kernel-derived REPL has at spawn. The host connection is
    ///     already authenticated as a trusted host (the <c>control.hello</c> host handshake), and
    ///     like every other host message this report carries no host id.
    /// </summary>
    private void RegisterHostRepl(HostReplSpawnedPayload? payload)
    {
        if (payload is null ||
            payload.Pid <= 0 ||
            string.IsNullOrWhiteSpace(payload.SessionId))
        {
            logger.LogWarning(
                "Ignored a malformed host REPL spawned report (session '{SessionId}', pid {Pid}).",
                payload?.SessionId,
                payload?.Pid);
            return;
        }

        if (replPolicies.TryGetValue(payload.SessionId, out var policy))
            logger.LogDebug(
                "Host-derived REPL for session '{SessionId}' generation {Generation} registered with pid {Pid}.",
                payload.SessionId,
                payload.Generation,
                payload.Pid);
        else
        {
            // Explicit downgrade: no kernel path cached a policy for this session (the esbuild-
            // wasm resolution failed at session start, or a non-kernel session was derived).
            // Registering the empty default policy denies every REPL permission request by
            // default — a deliberate, surfaced fallback, never a silent one. The kernel must
            // compute and pre-cache the policy before the host derives the REPL (ADR 0020).
            policy = EffectivePolicy.Default;
            logger.LogWarning(
                "No effective REPL policy is cached for session '{SessionId}'; " +
                "registered the host-derived REPL pid {Pid} with the default policy. " +
                "The kernel must compute and pre-cache the REPL policy before the host derives it (ADR 0020).",
                payload.SessionId,
                payload.Pid);
        }

        sessionRegistry.Register(payload.Pid, payload.SessionId);
        broker?.RegisterPolicy(payload.Pid, policy);
        logger.LogInformation(
            "Plugin host reported a REPL process for session '{SessionId}' generation {Generation} with pid {Pid}.",
            payload.SessionId,
            payload.Generation,
            payload.Pid);
    }

    /// <summary>Releases the pid-scoped identity and broker policy of a host-derived REPL that
    /// exited (ADR 0020). Idempotent: the pid may already have been unregistered by another
    /// path.</summary>
    private void UnregisterHostRepl(HostReplExitedPayload? payload)
    {
        if (payload is null ||
            payload.Pid <= 0 ||
            string.IsNullOrWhiteSpace(payload.SessionId))
        {
            logger.LogWarning(
                "Ignored a malformed host REPL exited report (session '{SessionId}', pid {Pid}).",
                payload?.SessionId,
                payload?.Pid);
            return;
        }

        sessionRegistry.Unregister(payload.Pid);
        broker?.UnregisterProcess(payload.Pid);
        logger.LogDebug(
            "Plugin host reported a REPL process exit for session '{SessionId}' generation {Generation} " +
            "with pid {Pid}{Failure}.",
            payload.SessionId,
            payload.Generation,
            payload.Pid,
            payload.Failure is { } failure ? $" ({failure})" : string.Empty);
    }

    private void CompletePending(ReplEnvelope envelope)
    {
        if (envelope.CorrelationId is not { } correlationId ||
            !pending.TryRemove(correlationId, out var completion))
            return;

        if (envelope.Type == ReplMessageType.ExtensionResult)
        {
            var payload = ParsePayload<ExtensionResultPayload>(envelope);
            completion.TrySetResult(ExtensionCallOutcome.Result(payload?.Value));
            return;
        }

        var error = ParsePayload<ExtensionErrorPayload>(envelope);
        completion.TrySetResult(
            ExtensionCallOutcome.Error(error?.Code ?? "extension_failed", error?.Message ?? "the extension failed"));
    }

    private void UpdateRegistry(ExtensionRegistryPayload? payload)
    {
        if (payload is null) return;

        PluginRegistration[] snapshot;
        PluginRegistration[] registrySnapshot;
        lock (gate)
        {
            registrations.Clear();
            foreach (var plugin in payload.Plugins)
                foreach (var extensionPoint in plugin.ExtensionPoints)
                    registrations.Add(new PluginRegistration(plugin.PluginId, plugin.ExportName, extensionPoint));

            states.Clear();
            foreach (var state in payload.States ?? [])
                states.Add(new PluginState(state.PluginId, state.ExportName, state.Specifier, state.State, state.Failure));

            logger.LogInformation(
                "Plugin host registered {Count} extension point(s) across {PluginCount} plugin(s).",
                registrations.Count,
                payload.Plugins.Count);

            snapshot = registrations
                .Where(static registration => registration.ExtensionPoint == ReplExtensionPointName.McpDiscover)
                .ToArray();
            registrySnapshot = registrations.ToArray();
        }

        RegistryChanges.Writer.TryWrite(registrySnapshot);
        dynamicMcpCoordinator?.PublishRegistry(snapshot);
    }

    private async Task<PluginMcpDiscoveryResult> DiscoverDynamicMcpAsync(
        PluginRegistration registration,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.SerializeToElement(
            new DiscoverContextPayload("registry_update"),
            ReplControlJsonContext.Default.DiscoverContextPayload);
        var outcome = await InvokeExtensionPointAsync(
                registration.PluginId,
                registration.ExportName,
                ReplExtensionPointName.McpDiscover,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (outcome.IsError) return PluginMcpDiscoveryResult.Failed(outcome.Code);
        if (outcome.Value is not { ValueKind: JsonValueKind.Array } array)
            return PluginMcpDiscoveryResult.Failed("invalid_discovery_result");

        var definitions = new List<McpServerDefinition>();
        foreach (var item in array.EnumerateArray())
        {
            if (!TryToMcpDefinition(registration.PluginId, item, out var definition))
                return PluginMcpDiscoveryResult.Failed("invalid_server_definition");

            definitions.Add(definition);
        }

        return PluginMcpDiscoveryResult.Success(definitions);
    }

    private Task<McpServerGeneration> CreateDynamicMcpGenerationAsync(
        McpServerDefinition definition,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<string> reservedNames;
        lock (gate)
        {
            reservedNames = reservedToolNames;
        }

        return McpServerGeneration.CreateAsync(
            definition,
            loggerFactory,
            timeProvider,
            cancellationToken,
            null,
            reservedNames);
    }

    private static bool TryToMcpDefinition(
        string pluginId,
        JsonElement discovery,
        [NotNullWhen(true)] out McpServerDefinition? definition)
    {
        definition = null;
        if (discovery.ValueKind != JsonValueKind.Object ||
            !discovery.TryGetProperty("module", out var module) ||
            module.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(module.GetString()) ||
            !discovery.TryGetProperty("transport", out var transport) ||
            transport.ValueKind != JsonValueKind.Object)
            return false;

        McpTransportDefinition payload;
        try
        {
            payload = transport.Deserialize(McpJsonContext.Default.McpTransportDefinition)
                      ?? throw new JsonException("The transport payload is null.");
        }
        catch (JsonException)
        {
            return false;
        }

        var id = $"plugin:{pluginId}::{module.GetString()}";
        switch (payload)
        {
            case StdioMcpTransportDefinition stdio when !string.IsNullOrWhiteSpace(stdio.Command):
                definition = new McpServerDefinition(
                    id,
                    stdio,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(30),
                    McpServerDefinition.CreateGenerationKey(
                        stdio,
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromMinutes(2),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(30)));
                return true;

            case HttpMcpTransportDefinition { Endpoint.IsAbsoluteUri: true } http:
                definition = new McpServerDefinition(
                    id,
                    http,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(30),
                    McpServerDefinition.CreateGenerationKey(
                        http,
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromMinutes(2),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(30)));
                return true;

            default:
                return false;
        }
    }

    private void FailPending(string message)
    {
        foreach (var completion in pending.Values)
            completion.TrySetResult(ExtensionCallOutcome.Error("host_disconnected", message));

        pending.Clear();
    }

    private static T? ParsePayload<T>(ReplEnvelope envelope)
        where T : class
    {
        if (envelope.Payload is not { } payload) return null;

        return (T?)JsonSerializer.Deserialize(payload.GetRawText(), JsonTypeInfoFor<T>());
    }

    private static JsonTypeInfo JsonTypeInfoFor<T>()
    {
        return typeof(T) switch
        {
            _ when typeof(T) == typeof(ExtensionResultPayload) =>
                ReplControlJsonContext.Default.ExtensionResultPayload,
            _ when typeof(T) == typeof(ExtensionErrorPayload) =>
                ReplControlJsonContext.Default.ExtensionErrorPayload,
            _ when typeof(T) == typeof(ExtensionRegistryPayload) =>
                ReplControlJsonContext.Default.ExtensionRegistryPayload,
            _ when typeof(T) == typeof(HostReplSpawnedPayload) =>
                ReplControlJsonContext.Default.HostReplSpawnedPayload,
            _ when typeof(T) == typeof(HostReplExitedPayload) =>
                ReplControlJsonContext.Default.HostReplExitedPayload,
            _ => throw new InvalidOperationException($"Unsupported extension payload type '{typeof(T).Name}'.")
        };
    }

    private static async Task PushAsync(WebSocket socket, ReplEnvelope envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, ReplControlJsonContext.Default.ReplEnvelope);
        await socket
            .SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                true,
                cancellationToken)
            .ConfigureAwait(false);
    }

}
