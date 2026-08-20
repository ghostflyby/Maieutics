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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins;

/// <summary>Plugin watch behavior for source-level hot reload, bound from <c>Maieutics:Plugins</c>.</summary>
internal sealed class PluginHostOptions
{
    internal const string SectionName = "Maieutics:Plugins";

    public bool WatchEnabled { get; set; } = true;

    public TimeSpan WatchDebounce { get; set; } = TimeSpan.FromMilliseconds(500);

    internal void Validate()
    {
        if (WatchDebounce <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(WatchDebounce), "The debounce interval must be positive.");
    }
}

internal sealed record PluginRegistration(string PluginId, string ExportName, string ExtensionPoint);

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
    DenoPermissionBroker broker,
    PluginHostOptions? options = null)
    : IHostedService, IAsyncDisposable
{
    private const int EnvelopeVersion = 1;
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(15);

    private readonly DenoReplOptions denoOptions = denoOptions ?? throw new ArgumentNullException(nameof(denoOptions));
    private readonly PluginHostOptions options = options ?? new PluginHostOptions();
    private readonly bool watchEnabled = (options ?? new PluginHostOptions()).WatchEnabled;
    private readonly TimeSpan watchDebounce = (options ?? new PluginHostOptions()).WatchDebounce;
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

    /// <summary>
    ///     Latest per-plugin lifecycle state reported by the host (running, stopped, failed, disabled),
    ///     keyed by plugin id. Tracks reloads and crash-disables driven by the host.
    /// </summary>
    private readonly Dictionary<string, PluginHostState> pluginStates = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> pluginReasons = new(StringComparer.Ordinal);

    private readonly Channel<string> reloadRequests = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private CancellationTokenSource? watcherLifetime;
    private Task? watcherLoop;
    private FileSystemWatcher? watcher;

    /// <summary>
    ///     Publishes the latest registry snapshot produced by the plugin host so tests can wait for a
    ///     registration without polling. Completed when the manager is disposed. Bounded and
    ///     drop-oldest so an unconsumed production stream retains only the newest snapshot.
    /// </summary>
    internal readonly Channel<PluginRegistration[]> RegistryChanges = Channel.CreateBounded<PluginRegistration[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly ReplControlSessionRegistry sessionRegistry =
        sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));

    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private string? configPath;

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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return EnsureStoppedAsync().WaitAsync(cancellationToken);
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
        lock (gate)
        {
            descriptors.Clear();
            descriptors.AddRange(ScanPlugins());
        }

        var graph = PluginDependencyGraph.Build(descriptors);
        foreach (var (pluginId, reason) in graph.ExcludedReasons)
            logger.LogWarning(
                "Plugin '{PluginId}' is excluded from the host: {Reason}.",
                pluginId,
                reason);

        var enabled = graph.StartOrder;
        lock (gate)
        {
            descriptors.Clear();
            descriptors.AddRange(enabled);
        }

        if (descriptors.Count == 0)
        {
            StartDynamicMcpCoordinator();
            logger.LogInformation("No Maieutics plugins found under '{PluginsRoot}'.", pluginsRoot);
            return;
        }

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
                BuildProcessGrants(configPath),
                broker),
            logger);
        sessionRegistry.RegisterPluginHost(process.ProcessId, HostId);
        processExitObservation = ObserveExitAsync(process, configPath);
        StartDynamicMcpCoordinator();
        StartWatcher();
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

        await StopProcessCoreAsync().ConfigureAwait(false);

        lifetime.Dispose();
    }

    /// <summary>
    ///     Stops the host process and watcher without disposing the lifetime or completing the
    ///     registry channel, so the manager can be restarted (used by manifest-change restarts).
    /// </summary>
    private async Task StopProcessCoreAsync()
    {
        if (process is not null)
        {
            await process.StopAsync().ConfigureAwait(false);
            await processExitObservation.ConfigureAwait(false);
            process = null;
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

        await StopWatcherAsync().ConfigureAwait(false);
    }

    private void StartWatcher()
    {
        if (!watchEnabled || !Directory.Exists(pluginsRoot)) return;
        if (watcher is not null) return;

        watcherLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        watcher = new FileSystemWatcher(pluginsRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite
                | NotifyFilters.FileName
                | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Deleted += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        watcherLoop = RunWatcherLoopAsync(watcherLifetime.Token);
        logger.LogDebug("Plugin source watcher started on '{PluginsRoot}'.", pluginsRoot);
    }

    private async Task StopWatcherAsync()
    {
        if (watcher is null) return;

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        watcher = null;
        if (watcherLifetime is not null)
        {
            await watcherLifetime.CancelAsync().ConfigureAwait(false);
            if (watcherLoop is not null)
            {
                try
                {
                    await watcherLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            watcherLifetime.Dispose();
            watcherLifetime = null;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        var pluginId = PluginIdForPath(args.FullPath);
        if (pluginId is null) return;
        reloadRequests.Writer.TryWrite(pluginId);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        OnFileChanged(sender, args);
    }

    private string? PluginIdForPath(string fullPath)
    {
        var relative = Path.GetRelativePath(pluginsRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal)) return null;
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, 2)[0];
        if (string.IsNullOrEmpty(firstSegment)) return null;

        string[] pluginIds;
        lock (gate)
        {
            pluginIds = descriptors.Select(descriptor => descriptor.Id).ToArray();
        }

        return pluginIds.Contains(firstSegment, StringComparer.Ordinal) ? firstSegment : null;
    }

    private async Task RunWatcherLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var batch in DebounceReloadRequestsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (batch.Count == 0) continue;
                var needsRestart = await DetectManifestOrTopologyChangesAsync(batch, cancellationToken)
                    .ConfigureAwait(false);
                if (needsRestart)
                {
                    await RestartHostProcessAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendPluginReloadAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Plugin source watcher loop failed.");
        }
    }

    private async IAsyncEnumerable<List<string>> DebounceReloadRequestsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await reloadRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var batch = new List<string>();
            if (reloadRequests.Reader.TryRead(out var first)) batch.Add(first);

            // Drain everything that arrives within the debounce window. A timeout
            // while draining is the normal "quiet" end of the window, not an error.
            var drainDeadline = DateTimeOffset.UtcNow + watchDebounce;
            while (DateTimeOffset.UtcNow < drainDeadline)
            {
                var remaining = drainDeadline - DateTimeOffset.UtcNow;
                using var drainTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                drainTimeout.CancelAfter(remaining);
                bool more;
                try
                {
                    more = await reloadRequests.Reader.WaitToReadAsync(drainTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!more) break;
                while (reloadRequests.Reader.TryRead(out var id))
                    if (!batch.Contains(id, StringComparer.Ordinal))
                        batch.Add(id);
            }

            yield return batch;
        }
    }

    private async Task<bool> DetectManifestOrTopologyChangesAsync(
        IReadOnlyList<string> pluginIds,
        CancellationToken cancellationToken)
    {
        // A manifest/topology change (deno.json edit, directory add/remove) requires a host
        // process restart so the new permission policy is captured once. Everything else
        // (source edits) can be reloaded in place.
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var pluginId in pluginIds)
        {
            string root;
            lock (gate)
            {
                var descriptor = descriptors.FirstOrDefault(candidate => candidate.Id == pluginId);
                if (descriptor is null) continue;
                root = descriptor.RootDirectory;
            }

            var config = Path.Combine(root, "deno.json");
            if (!File.Exists(config)) config = Path.Combine(root, "deno.jsonc");
            if (!File.Exists(config)) continue;
            if (ManifestDiffersFromSnapshot(pluginId)) return true;
        }

        return false;
    }

    private bool ManifestDiffersFromSnapshot(string pluginId)
    {
        PluginDescriptor? current;
        lock (gate)
        {
            current = descriptors.FirstOrDefault(descriptor => descriptor.Id == pluginId);
        }

        if (current is null) return true;
        if (!PluginManifest.TryLoad(current.RootDirectory, out var fresh, out _)) return true;

        // Records with reference-type members compare by reference; compare structurally.
        return !string.Equals(fresh.Name, current.Name, StringComparison.Ordinal)
               || fresh.Workers.Select(worker => worker.ExportName)
                   .SequenceEqual(current.Workers.Select(worker => worker.ExportName)) is false
               || fresh.Dependencies.SequenceEqual(current.Dependencies) is false
               || PermissionsDiffer(fresh.Permissions, current.Permissions);
    }

    private static bool PermissionsDiffer(PluginPermissionGrants left, PluginPermissionGrants right)
    {
        return GrantDiffers(left.Env, right.Env)
               || GrantDiffers(left.Net, right.Net)
               || GrantDiffers(left.Read, right.Read)
               || GrantDiffers(left.Write, right.Write)
               || GrantDiffers(left.Run, right.Run)
               || GrantDiffers(left.Ffi, right.Ffi)
               || GrantDiffers(left.Sys, right.Sys)
               || GrantDiffers(left.Import, right.Import);
    }

    private static bool GrantDiffers(PluginPermissionGrant left, PluginPermissionGrant right)
    {
        return left.AllowAll != right.AllowAll
               || left.Values.SequenceEqual(right.Values) is false;
    }

    private async Task RestartHostProcessAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Plugin manifest or topology changed; restarting the plugin host process.");
        lock (lifecycleGate)
        {
            if (stopping is not null)
                return; // A stop is already in flight; the restart will be superseded.
        }

        await StopProcessCoreAsync().ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return;

        // Re-arm the lifecycle for a fresh start (the host process is gone now).
        lock (lifecycleGate)
        {
            starting = null;
            stopping = null;
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RestartCoreAsync(CancellationToken cancellationToken)
    {
        lock (lifecycleGate)
        {
            starting = null;
            stopping = null;
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendPluginReloadAsync(
        IReadOnlyList<string> pluginIds,
        CancellationToken cancellationToken)
    {
        await SendPluginReloadCoreAsync(pluginIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reloads the given plugins in the host (source-only change); used by the watcher and tests.</summary>
    internal async Task ReloadPluginsAsync(IReadOnlyList<string> pluginIds, CancellationToken cancellationToken)
    {
        await SendPluginReloadCoreAsync(pluginIds, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendPluginReloadCoreAsync(
        IReadOnlyList<string> pluginIds,
        CancellationToken cancellationToken)
    {
        if (pluginIds.Count == 0) return;
        if (Socket is not { State: WebSocketState.Open } socket)
        {
            logger.LogWarning("Plugin host is not connected; skipping reload for {Count} plugin(s).", pluginIds.Count);
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ExtensionCallOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pending[correlationId] = tcs;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(InvokeTimeout);
        try
        {
            var payload = new PluginReloadPayload(pluginIds.ToArray());
            await PushAsync(
                socket,
                new ReplEnvelope(
                    EnvelopeVersion,
                    ReplMessageType.PluginReload,
                    correlationId,
                    JsonSerializer.SerializeToElement(payload, ReplControlJsonContext.Default.PluginReloadPayload)),
                timeout.Token).ConfigureAwait(false);
            await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            logger.LogInformation("Reloaded {Count} plugin(s) in the host.", pluginIds.Count);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plugin reload failed for {Count} plugin(s).", pluginIds.Count);
        }
        finally
        {
            pending.TryRemove(correlationId, out _);
        }
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
        if (!Directory.Exists(pluginsRoot)) return [];

        var result = new List<PluginDescriptor>();
        foreach (var directory in Directory.EnumerateDirectories(pluginsRoot))
            if (PluginManifest.TryLoad(directory, out var descriptor, out var error))
            {
                if (!RequiresProcessIsolation(descriptor))
                {
                    result.Add(descriptor);
                    logger.LogInformation(
                        "Discovered Maieutics plugin '{PluginName}' with {WorkerCount} extension carrier(s).",
                        descriptor.Name,
                        descriptor.Workers.Count);
                    continue;
                }

                logger.LogWarning(
                    "Plugin '{Directory}' requires process isolation (run/ffi or isolation=process), " +
                    "which is not implemented yet; it is disabled.",
                    Path.GetFileName(directory));
            }
            else if (error.Contains("is not a Maieutics plugin", StringComparison.Ordinal))
            {
            }
            else
            {
                logger.LogWarning("Skipping plugin directory '{Directory}': {Error}", directory, error);
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
        var config = new PluginHostConfigFile(
            plugins.Select(descriptor => new PluginHostConfigPlugin(
                descriptor.Id,
                descriptor.Name,
                descriptor.RootDirectory,
                [
                    .. descriptor.Workers
                        .Select(worker => new PluginHostConfigWorker(worker.ExportName, worker.EntryUrl))
                ],
                ToConfigPermissions(descriptor.Permissions),
                descriptor.Dependencies)).ToArray());
        var json = JsonSerializer.Serialize(config, PluginHostJsonContext.Default.PluginHostConfigFile);
        var path = Path.Combine(Path.GetTempPath(), $"mc-plugins-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
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

    private PluginHostProcessGrants BuildProcessGrants(string config)
    {
        PluginDescriptor[] plugins;
        lock (gate)
        {
            plugins = [.. descriptors];
        }

        var read = new List<string> { config, modules.ModuleDirectory, socketPath };
        var write = new List<string> { socketPath };
        var net = new List<string> { "localhost", $"unix:{socketPath}" };
        var env = new List<string>();
        var imports = new List<string>();
        var readAll = false;
        var writeAll = false;
        var netAll = false;
        var envAll = false;
        var importAll = false;
        foreach (var plugin in plugins)
        {
            read.Add(plugin.RootDirectory);
            Merge(plugin.Permissions.Read, read, ref readAll);
            Merge(plugin.Permissions.Write, write, ref writeAll);
            Merge(plugin.Permissions.Net, net, ref netAll);
            Merge(plugin.Permissions.Env, env, ref envAll);
            Merge(plugin.Permissions.Import, imports, ref importAll);
        }

        env.AddRange(ReplControlEnvironmentNames());
        return new PluginHostProcessGrants(
            readAll,
            read,
            writeAll,
            write,
            netAll,
            net,
            envAll,
            env,
            importAll,
            imports);
    }

    private static void Merge(
        PluginPermissionGrant grant,
        ICollection<string> target,
        ref bool allowAll)
    {
        if (grant.AllowAll)
        {
            allowAll = true;
            return;
        }

        foreach (var value in grant.Values) target.Add(value);
    }

    private static IEnumerable<string> ReplControlEnvironmentNames()
    {
        yield return ReplControlEnvironment.IpcAddress;
        yield return ReplControlEnvironment.PluginHostId;
        yield return ReplControlEnvironment.PluginConfig;
        yield return ReplControlEnvironment.PluginSdk;
        yield return ReplControlEnvironment.PluginWorkerEntry;
    }

    private void HandleHostMessage(string text)
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
        }
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

            pluginStates.Clear();
            pluginReasons.Clear();
            foreach (var state in payload.States ?? [])
            {
                pluginStates[state.PluginId] = ToManagedState(state.State);
                if (!string.IsNullOrEmpty(state.Reason)) pluginReasons[state.PluginId] = state.Reason;
            }

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

    private static PluginHostState ToManagedState(string hostState)
    {
        return hostState switch
        {
            "running" => PluginHostState.Ready,
            "starting" => PluginHostState.Starting,
            "stopping" => PluginHostState.Stopping,
            "disabled" or "failed" => PluginHostState.Failed,
            _ => PluginHostState.Stopped
        };
    }

    internal IReadOnlyDictionary<string, PluginHostState> GetPluginStates()
    {
        lock (gate)
        {
            return new Dictionary<string, PluginHostState>(pluginStates, StringComparer.Ordinal);
        }
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
