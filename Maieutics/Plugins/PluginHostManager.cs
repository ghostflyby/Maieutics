using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Control;
using Maieutics.Execution;
using Maieutics.Mcp;
using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins;

internal sealed record PluginRegistration(string PluginId, string ExportName, string ExtensionPoint);

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
///     reverse dependency. Instances are created already started through <see cref="CreateAsync" />.
/// </summary>
internal sealed class PluginHostManager(
    string pluginsRoot,
    string socketPath,
    DenoReplOptions denoOptions,
    PluginHostModule modules,
    ReplControlSessionRegistry sessionRegistry,
    ILogger<PluginHostManager> logger,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider)
    : IAsyncDisposable
{
    private const int EnvelopeVersion = 1;
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(15);

    private readonly DenoReplOptions denoOptions = denoOptions ?? throw new ArgumentNullException(nameof(denoOptions));
    private readonly List<PluginDescriptor> descriptors = [];
    private readonly Lock gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock lifecycleGate = new();

    private readonly ILogger<PluginHostManager> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ILoggerFactory loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    private readonly PluginHostModule modules = modules ?? throw new ArgumentNullException(nameof(modules));

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ExtensionCallOutcome>> pending =
        new(StringComparer.Ordinal);

    private readonly List<PluginRegistration> registrations = [];

    private readonly ReplControlSessionRegistry sessionRegistry =
        sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));

    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private string? configPath;

    private PluginMcpCoordinator? dynamicMcpCoordinator;
    private PluginHostProcess? process;
    private Task processExitObservation = Task.CompletedTask;
    private IReadOnlySet<string> reservedToolNames = new HashSet<string>(StringComparer.Ordinal);
    private Task? stopping;
    private WebSocket? Socket { get; set; }

    private string HostId { get; } = $"host-{Guid.NewGuid():N}"[..12];

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }

    internal static Task<PluginHostManager> CreateAsync(
        string pluginsRoot,
        string socketPath,
        DenoReplOptions denoOptions,
        PluginHostModule modules,
        ReplControlSessionRegistry sessionRegistry,
        ILogger<PluginHostManager> logger,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        var manager = new PluginHostManager(
            pluginsRoot,
            socketPath,
            denoOptions,
            modules,
            sessionRegistry,
            logger,
            loggerFactory,
            timeProvider);
        manager.Start();
        return Task.FromResult(manager);
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
                BuildProcessGrants(configPath)),
            logger);
        sessionRegistry.RegisterPluginHost(process.ProcessId, HostId);
        processExitObservation = ObserveExitAsync(process, configPath);
        StartDynamicMcpCoordinator();
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
    public IReadOnlyList<McpServerGeneration.McpServerLease> AcquireDynamicMcpLeases()
    {
        return dynamicMcpCoordinator?.AcquireLeases() ?? [];
    }

    /// <summary>Runs the receiving loop for a plugin host WebSocket attached by the control host.</summary>
    public async Task AttachHostAsync(WebSocket socket, CancellationToken cancellationToken)
    {
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

    private Task StopAsync()
    {
        lock (lifecycleGate)
        {
            return stopping ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await Task.Yield();
        await lifetime.CancelAsync().ConfigureAwait(false);
        FailPending("The plugin host is stopping.");
        if (dynamicMcpCoordinator is { } coordinator) await coordinator.DisposeAsync().ConfigureAwait(false);

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
                descriptor.RootDirectory,
                [
                    .. descriptor.Workers
                        .Select(worker => new PluginHostConfigWorker(worker.ExportName, worker.EntryUrl))
                ],
                ToConfigPermissions(descriptor.Permissions))).ToArray());
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
        lock (gate)
        {
            registrations.Clear();
            foreach (var plugin in payload.Plugins)
                foreach (var extensionPoint in plugin.ExtensionPoints)
                    registrations.Add(new PluginRegistration(plugin.PluginId, plugin.ExportName, extensionPoint));

            logger.LogInformation(
                "Plugin host registered {Count} extension point(s) across {PluginCount} plugin(s).",
                registrations.Count,
                payload.Plugins.Count);

            snapshot = registrations
                .Where(static registration => registration.ExtensionPoint == ReplExtensionPointName.McpDiscover)
                .ToArray();
        }

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
