using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Maieutics.Agent;
using Maieutics.Jupyter;
using Maieutics.Mcp;
using Maieutics.Plugins;
using Maieutics.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Maieutics.Configuration;

internal sealed class MaieuticsRuntimeConfiguration :
    IMaieuticsRuntimeConfiguration,
    IMaieuticsMcpController,
    IAsyncDisposable
{
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReloadDrainTimeout = TimeSpan.FromSeconds(10);
    private readonly IReadOnlyList<AIFunction> builtInTools;
    private readonly IConfiguration configuration;
    private readonly MaieuticsConfigurationFile configurationFile;

    private readonly ConcurrentDictionary<string, CachedDiscovery> discoveryCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyDictionary<string, IConfiguredChatClientFactory> factories;
    private readonly MaieuticsConfigurationFileErrors fileErrors;
    private readonly Lock gate = new();
    private readonly Lock initializationGate = new();
    private readonly ILogger<MaieuticsRuntimeConfiguration> logger;
    private readonly ILoggerFactory loggerFactory;
    private readonly McpClientTransportFactory? mcpTransportFactory;
    private PluginHostManager? pluginHosts;

    // The bounded channel is only an edge trigger. reloadRequest remains authoritative when duplicate
    // notifications coalesce while the single reader is already processing an earlier request.
    private readonly Channel<byte> reloadSignals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    private readonly List<Task> retiredGenerations = [];
    private readonly McpStartupDirectory startupDirectory;
    private readonly TimeProvider timeProvider;
    private long completedReloadAttempt;
    private long completedReloadRequest;
    private RuntimeSnapshot? current;
    private int disposed;
    private IDisposable? fileErrorSubscription;
    private Task? initialization;
    private MaieuticsConfigurationReloadInfo? lastReload;
    private long reloadAttempt;
    private long reloadRequest;
    private Task reloadLoop = Task.CompletedTask;
    private TaskCompletionSource reloadCompletionSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? reloadSubscription;
    private ProfileOverride? sessionOverride;

    public MaieuticsRuntimeConfiguration(
        IConfiguration configuration,
        MaieuticsConfigurationFile configurationFile,
        MaieuticsConfigurationFileErrors fileErrors,
        IEnumerable<IConfiguredChatClientFactory> factories,
        IReadOnlyList<AIFunction> builtInTools,
        McpStartupDirectory startupDirectory,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ILogger<MaieuticsRuntimeConfiguration> logger,
        McpClientTransportFactory? mcpTransportFactory = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.configurationFile = configurationFile ?? throw new ArgumentNullException(nameof(configurationFile));
        this.fileErrors = fileErrors ?? throw new ArgumentNullException(nameof(fileErrors));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.startupDirectory = startupDirectory ?? throw new ArgumentNullException(nameof(startupDirectory));
        this.mcpTransportFactory = mcpTransportFactory;
        this.factories = CreateFactoryRegistry(factories);
        this.builtInTools = builtInTools ?? throw new ArgumentNullException(nameof(builtInTools));

        var candidate = CreateCandidate();
        ConnectionFile = Path.GetFullPath(candidate.Options.Jupyter.ConnectionFile);

        logger.LogInformation(
            "Using Maieutics configuration file {ConfigurationPath} selected from {ConfigurationSource}.",
            configurationFile.Path ?? "(none)",
            configurationFile.Source);
    }

    internal long ReloadAttempt => Interlocked.Read(ref reloadAttempt);

    internal long CompletedReloadAttempt => Interlocked.Read(ref completedReloadAttempt);

    internal long ReloadRequest => Interlocked.Read(ref reloadRequest);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            await ObserveInitializationAsync().ConfigureAwait(false);
            await WaitForReloadDrainAsync().ConfigureAwait(false);
            return;
        }

        reloadSubscription?.Dispose();
        fileErrorSubscription?.Dispose();
        lock (gate)
        {
            reloadSignals.Writer.TryComplete();
        }

        await WaitForReloadDrainAsync().ConfigureAwait(false);
        await ObserveInitializationAsync().ConfigureAwait(false);

        ProfileGeneration[] generations = [];
        McpServerGeneration[] mcpGenerations = [];
        Task[] retired;
        lock (gate)
        {
            if (current is { } snapshot)
            {
                var automaticGeneration = sessionOverride is AutomaticProfileOverride automatic
                    ? automatic.Generation
                    : null;
                generations = snapshot.Profiles.Values
                    .Select(static profile => profile.Generation)
                    .Concat(automaticGeneration is null ? [] : [automaticGeneration])
                    .Distinct<ProfileGeneration>(ReferenceEqualityComparer.Instance)
                    .ToArray();
                mcpGenerations = snapshot.McpServers.Values
                    .Distinct<McpServerGeneration>(ReferenceEqualityComparer.Instance)
                    .ToArray();
            }

            current = null;
            sessionOverride = null;
            retired = retiredGenerations.ToArray();
        }

        var currentRetirements = generations.Select(static generation => generation.Retire())
            .Concat(mcpGenerations.Select(static generation => generation.Retire()));
        await Task.WhenAll(retired.Concat(currentRetirements)).ConfigureAwait(false);
    }

    public IReadOnlyList<MaieuticsMcpServerInfo> GetMcpServers()
    {
        lock (gate)
        {
            return GetCurrent().McpServers.Values
                .OrderBy(static generation => generation.Id, StringComparer.OrdinalIgnoreCase)
                .Select(static generation => generation.GetInfo())
                .ToArray();
        }
    }

    public string ConnectionFile { get; }

    public long Version
    {
        get
        {
            lock (gate)
            {
                return GetCurrent().Version;
            }
        }
    }

    public MaieuticsRuntimeStatus GetStatus()
    {
        lock (gate)
        {
            var snapshot = GetCurrent();
            var capabilityInfos = new List<MaieuticsCapabilityInfo>();
            foreach (var profile in snapshot.Profiles.Values
                         .OrderBy(static profile => profile.Id, StringComparer.OrdinalIgnoreCase))
                if (snapshot.Sources.TryGetValue(profile.SourceId, out var source))
                    capabilityInfos.Add(CreateCapabilityInfo(
                        profile.SourceId,
                        profile.Identity.Model,
                        source,
                        snapshot.CapabilityRegistry.Resolve(source, profile.Identity.Model)));

            if (sessionOverride is AutomaticProfileOverride automatic &&
                snapshot.Sources.TryGetValue(automatic.SourceId, out var automaticSource))
                capabilityInfos.Add(CreateCapabilityInfo(
                    automatic.SourceId,
                    automatic.Model,
                    automaticSource,
                    snapshot.CapabilityRegistry.Resolve(automaticSource, automatic.Model)));

            return new MaieuticsRuntimeStatus(
                snapshot.Version,
                CreateModelProfileSelectionLocked(snapshot),
                lastReload ?? new MaieuticsConfigurationReloadInfo(
                    0,
                    MaieuticsConfigurationReloadOutcome.NotAttempted,
                    snapshot.Version),
                capabilityInfos);
        }
    }

    private static MaieuticsCapabilityInfo CreateCapabilityInfo(
        string sourceId,
        string model,
        IConfiguredChatClientSource source,
        CapabilityResolution resolution)
    {
        return new MaieuticsCapabilityInfo(
            sourceId,
            model,
            resolution.Matched,
            resolution.KnownVendor,
            source.Capabilities,
            resolution.Potential,
            resolution.Effective);
    }

    public async Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeProfileSelection? selection = null;
        ProfileGenerationLease? generationLease = null;
        var mcpLeases = new List<McpServerGeneration.McpServerLease>();
        try
        {
            lock (gate)
            {
                var snapshot = GetCurrent();
                selection = SelectRuntimeProfile(snapshot);
                generationLease = selection.Generation.Acquire();
                foreach (var server in snapshot.McpServers.Values
                             .OrderBy(static value => value.Id, StringComparer.OrdinalIgnoreCase))
                    if (server.TryAcquire() is { } mcpLease)
                        mcpLeases.Add(mcpLease);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref pluginHosts) is { } activePluginHosts)
            {
                var dynamicLeases = await activePluginHosts
                    .AcquireDynamicMcpLeasesAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var lease in dynamicLeases) mcpLeases.Add(lease);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var tools = new List<AIFunction>(builtInTools.Count);
            tools.AddRange(builtInTools);
            foreach (var lease in mcpLeases) tools.AddRange(lease.Tools);

            return new RuntimeProfileLease(
                generationLease,
                mcpLeases,
                new AgentRunProfile(
                    selection.Generation.Client,
                    CreateAgentOptions(selection.Snapshot.Options),
                    selection.Identity,
                    selection.Capabilities,
                    selection.HostedCapabilities,
                    tools));
        }
        catch
        {
            await RollbackRuntimeProfileAcquisitionAsync(generationLease, mcpLeases).ConfigureAwait(false);
            throw;
        }
    }

    public MaieuticsModelProfileSelection GetModelProfileSelection()
    {
        lock (gate)
        {
            var snapshot = GetCurrent();
            return CreateModelProfileSelectionLocked(snapshot);
        }
    }

    public IReadOnlyList<MaieuticsModelProfileInfo> GetCachedAutomaticModelProfiles()
    {
        lock (gate)
        {
            var snapshot = GetCurrent();
            var selected = sessionOverride as AutomaticProfileOverride;
            return GetAutomaticProfileCandidates(snapshot, DateTime.UtcNow)
                .Select(candidate => candidate.ToProfileInfo(
                    selected is not null && selected.Matches(candidate)))
                .ToArray();
        }
    }

    public IReadOnlyList<string> GetModelSourceIds()
    {
        lock (gate)
        {
            return GetCurrent().Sources.Keys
                .OrderBy(static sourceId => sourceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public async ValueTask SelectModelProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        cancellationToken.ThrowIfCancellationRequested();
        if (TrySelectConfiguredProfile(profileId))
        {
            ObserveCompletedRetirements();
            return;
        }

        lock (gate)
        {
            _ = GetCurrent();
            if (sessionOverride is AutomaticProfileOverride selected &&
                string.Equals(selected.Selector, profileId, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var candidate = ResolveAutomaticProfile(profileId);
        lock (gate)
        {
            if (sessionOverride is AutomaticProfileOverride selected && selected.Matches(candidate)) return;
        }

        ProfileGeneration generation;
        try
        {
            generation = new ProfileGeneration(candidate.Source.Create(candidate.Model), logger);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not create automatic model profile {ProfileSelector}.",
                candidate.Selector);
            throw new ArgumentException(
                $"The automatic model profile '{candidate.Selector}' could not be created.",
                nameof(profileId),
                exception);
        }

        var replacement = new AutomaticProfileOverride(candidate, generation);
        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var snapshot = GetCurrent();
                if (!snapshot.Sources.TryGetValue(candidate.SourceId, out var source) ||
                    !string.Equals(source.ProviderName, candidate.Provider, StringComparison.OrdinalIgnoreCase) ||
                    !Equals(source.ClientGenerationKey, candidate.ClientGenerationKey) ||
                    !AutomaticProfileMatches(
                        snapshot,
                        source,
                        candidate.Model,
                        candidate.Capabilities,
                        candidate.HostedCapabilities))
                    throw new ArgumentException(
                        $"The model source '{candidate.SourceId}' changed while the automatic profile was selected. " +
                        "Run model discovery and try again.",
                        nameof(profileId));

                var previous = sessionOverride as AutomaticProfileOverride;
                sessionOverride = replacement;
                committed = true;
                if (previous is not null) TrackRetirementLocked(previous.Generation);
            }
        }
        finally
        {
            if (!committed) await generation.Retire().ConfigureAwait(false);
        }

        ObserveCompletedRetirements();
    }

    public void ResetModelProfile()
    {
        lock (gate)
        {
            _ = GetCurrent();
            if (sessionOverride is AutomaticProfileOverride automatic) TrackRetirementLocked(automatic.Generation);

            sessionOverride = null;
        }

        ObserveCompletedRetirements();
    }

    public MaieuticsAgentKernelOptions GetKernelOptions()
    {
        lock (gate)
        {
            var jupyter = GetCurrent().Options.Jupyter;
            return new MaieuticsAgentKernelOptions
            {
                FlushInterval = jupyter.FlushInterval,
                FlushCharacters = jupyter.FlushCharacters
            };
        }
    }

    public async ValueTask<IReadOnlyList<DiscoveredModelGroup>> GetDiscoveredModelsAsync(
        string? sourceId = null,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(string sourceId, string provider, IConfiguredChatClientSource source, long version)> targets;
        lock (gate)
        {
            var snapshot = GetCurrent();
            targets =
            [
                .. snapshot.Sources
                    .Where(s => sourceId is null ||
                                string.Equals(s.Key, sourceId, StringComparison.OrdinalIgnoreCase))
                    .Select(s => (s.Key, s.Value.ProviderName, s.Value, snapshot.Version))
            ];
        }

        var now = DateTime.UtcNow;
        var results = new List<DiscoveredModelGroup>(targets.Count);
        foreach (var (sid, provider, source, version) in targets)
        {
            if (source is not IModelDiscoverySource discovery) continue;

            if (!refresh && discoveryCache.TryGetValue(sid, out var cached) &&
                now - cached.CachedAt < DiscoveryCacheTtl &&
                cached.ConfigurationVersion == version &&
                Equals(cached.ClientGenerationKey, source.ClientGenerationKey))
            {
                results.Add(cached.Result);
                continue;
            }

            try
            {
                var models = await discovery.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
                var group = new DiscoveredModelGroup(sid, provider, null, models);
                lock (gate)
                {
                    if (current is { } snapshot &&
                        snapshot.Version == version &&
                        snapshot.Sources.TryGetValue(sid, out var currentSource) &&
                        Equals(currentSource.ClientGenerationKey, source.ClientGenerationKey))
                        discoveryCache[sid] = new CachedDiscovery(
                            group,
                            now,
                            version,
                            source.ClientGenerationKey);
                }

                results.Add(group);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Model discovery failed for source '{SourceId}' using provider '{Provider}'.",
                    sid,
                    provider);
                results.Add(new DiscoveredModelGroup(sid, provider, ModelDiscoveryFailureKind.ProviderError, []));
            }
        }

        return results;
    }

    internal Task InitializeAsync(PluginHostManager activePluginHosts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activePluginHosts);
        lock (initializationGate)
        {
            if (pluginHosts is { } existing && !ReferenceEquals(existing, activePluginHosts))
                throw new InvalidOperationException(
                    "The runtime configuration is already bound to a different plugin host manager.");

            Volatile.Write(ref pluginHosts, activePluginHosts);
            activePluginHosts.SetReservedToolNames(
                builtInTools.Select(static function => function.Name).ToHashSet(StringComparer.Ordinal));
            return initialization ??= InitializeCoreAsync(cancellationToken);
        }
    }

    private bool TrySelectConfiguredProfile(string value)
    {
        lock (gate)
        {
            var snapshot = GetCurrent();
            if (snapshot.Profiles.TryGetValue(value, out var profile))
            {
                SetConfiguredProfileOverrideLocked(profile.Id);
                return true;
            }

            var modelMatches = snapshot.Profiles.Values
                .Where(profileEntry => string.Equals(
                    profileEntry.Identity.Model,
                    value,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(static profileEntry => profileEntry.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            switch (modelMatches.Length)
            {
                case 0:
                    return false;
                case 1:
                    SetConfiguredProfileOverrideLocked(modelMatches[0].Id);
                    return true;
                default:
                    throw new ArgumentException(
                        $"The model '{value}' matches multiple configured model profiles: " +
                        $"{string.Join(", ", modelMatches.Select(static match => $"'{match.Id}'"))}. " +
                        "Use a profile ID.",
                        nameof(value));
            }
        }
    }

    private AutomaticProfileCandidate ResolveAutomaticProfile(string value)
    {
        lock (gate)
        {
            var snapshot = GetCurrent();
            var candidates = GetAutomaticProfileCandidates(snapshot, DateTime.UtcNow);
            AutomaticProfileCandidate[] matches;
            if (value.StartsWith('@'))
            {
                if (!MaieuticsAutomaticProfileSelector.TryParse(value, out var sourceId, out var model))
                    throw new ArgumentException(
                        "Automatic model profile selectors must use the form '@source/model'.",
                        nameof(value));

                matches = candidates
                    .Where(candidate =>
                        string.Equals(candidate.SourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            else
            {
                matches = candidates
                    .Where(candidate => string.Equals(
                        candidate.Model,
                        value,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            return matches.Length switch
            {
                1 => matches[0],
                > 1 => throw new ArgumentException(
                    $"The discovered model '{value}' is available from multiple sources: " +
                    $"{string.Join(", ", matches.Select(static match => $"'{match.Selector}'"))}. " +
                    "Use a qualified automatic profile selector.",
                    nameof(value)),
                _ => throw new ArgumentException(
                    $"The model profile or discovered model '{value}' is not available in the discovery cache. " +
                    "Run '%model available' first.",
                    nameof(value))
            };
        }
    }

    private IReadOnlyList<AutomaticProfileCandidate> GetAutomaticProfileCandidates(
        RuntimeSnapshot snapshot,
        DateTime now)
    {
        var candidates = new Dictionary<string, AutomaticProfileCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var cached in discoveryCache.Values)
        {
            if (now - cached.CachedAt >= DiscoveryCacheTtl ||
                cached.ConfigurationVersion != snapshot.Version ||
                cached.Result.Failure is not null ||
                !snapshot.Sources.TryGetValue(cached.Result.SourceId, out var source) ||
                !Equals(cached.ClientGenerationKey, source.ClientGenerationKey))
                continue;

            foreach (var model in cached.Result.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Id)) continue;

                var selector = MaieuticsAutomaticProfileSelector.Format(cached.Result.SourceId, model.Id);
                var (capabilities, hostedCapabilities) =
                    ResolveSourceCapabilities(source, model.Id, snapshot.CapabilityRegistry);
                candidates.TryAdd(selector, new AutomaticProfileCandidate(
                    selector,
                    cached.Result.SourceId,
                    source.ProviderName,
                    model.Id,
                    source.ClientGenerationKey,
                    capabilities,
                    hostedCapabilities,
                    source));
            }
        }

        return candidates.Values
            .OrderBy(static candidate => candidate.Selector, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SetConfiguredProfileOverrideLocked(string profileId)
    {
        if (sessionOverride is AutomaticProfileOverride automatic) TrackRetirementLocked(automatic.Generation);

        sessionOverride = new ConfiguredProfileOverride(profileId);
    }

    private void TrackRetirementLocked(ProfileGeneration generation)
    {
        retiredGenerations.Add(generation.Retire());
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var candidate = CreateCandidate();
        var snapshot = await BuildSnapshotAsync(candidate, null, 1, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                current = snapshot;
                snapshot = null;
            }

            StartReloadLoop();
        }
        finally
        {
            if (snapshot is not null) await RetireSnapshotAsync(snapshot).ConfigureAwait(false);
        }
    }

    private async Task ObserveInitializationAsync()
    {
        Task? task;
        lock (initializationGate)
        {
            task = initialization;
        }

        if (task is null) return;

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Host startup observes readiness failures. Disposal only needs to observe terminal completion.
        }
    }

    private static Task RetireSnapshotAsync(RuntimeSnapshot snapshot)
    {
        var modelRetirements = snapshot.Profiles.Values
            .Select(static profile => profile.Generation)
            .Distinct<ProfileGeneration>(ReferenceEqualityComparer.Instance)
            .Select(static generation => generation.Retire());
        var mcpRetirements = snapshot.McpServers.Values
            .Distinct<McpServerGeneration>(ReferenceEqualityComparer.Instance)
            .Select(static generation => generation.Retire());
        return Task.WhenAll(modelRetirements.Concat(mcpRetirements));
    }

    private void StartReloadLoop()
    {
        lock (initializationGate)
        {
            if (reloadSubscription is not null) return;

            fileErrorSubscription = fileErrors.RegisterSignal(SignalReload);
            reloadSubscription = ChangeToken.OnChange(configuration.GetReloadToken, SignalReload);
            reloadLoop = Task.Run(ProcessReloadsAsync);
            SignalReload();
        }
    }

    private void SignalReload()
    {
        lock (gate)
        {
            if (disposed != 0) return;

            Interlocked.Increment(ref reloadRequest);
            reloadSignals.Writer.TryWrite(0);
        }
    }

    private async Task ProcessReloadsAsync()
    {
        await foreach (var _ in reloadSignals.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            while (true)
            {
                long request;
                lock (gate)
                {
                    request = reloadRequest;
                    if (completedReloadRequest >= request) break;
                }

                var attempt = Interlocked.Increment(ref reloadAttempt);
                var loadError = fileErrors.TakeLatest();
                var outcome = MaieuticsConfigurationReloadOutcome.Rejected;
                try
                {
                    outcome = await ReloadAsync().ConfigureAwait(false)
                        ? MaieuticsConfigurationReloadOutcome.Applied
                        : MaieuticsConfigurationReloadOutcome.Unchanged;
                }
                catch (Exception exception)
                {
                    logger.LogError(loadError ?? exception,
                        "Rejected an invalid Maieutics configuration update. Version {ConfigurationVersion} remains active.",
                        Version);
                }

                ObserveCompletedRetirements();

                TaskCompletionSource completed;
                lock (gate)
                {
                    if (current is { } snapshot)
                        lastReload = new MaieuticsConfigurationReloadInfo(attempt, outcome, snapshot.Version);

                    Interlocked.Exchange(ref completedReloadAttempt, attempt);
                    completedReloadRequest = request;
                    completed = reloadCompletionSignal;
                    reloadCompletionSignal =
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                completed.TrySetResult();
            }
        }
    }

    /// <summary>
    ///     Waits until the configuration reload loop has finished applying a request newer than
    ///     <paramref name="afterRequest"/>. The wait is signal-driven: each completed reload swaps a
    ///     fresh completion source, so waiters never busy-spin or rely on polling a counter.
    /// </summary>
    internal Task WaitForReloadCompletionAsync(long afterRequest, CancellationToken cancellationToken)
    {
        return WaitForReloadCompletionCoreAsync(afterRequest, cancellationToken);
    }

    private async Task WaitForReloadCompletionCoreAsync(long afterRequest, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource signal;
            lock (gate)
            {
                if (completedReloadRequest > afterRequest) return;

                signal = reloadCompletionSignal;
            }

            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForReloadDrainAsync()
    {
        if (reloadLoop.IsCompleted) return;
        try
        {
            await reloadLoop.WaitAsync(ReloadDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "The configuration reload loop did not drain within {TimeoutSeconds}s; disposal continues " +
                "with the remaining runtime state.",
                ReloadDrainTimeout.TotalSeconds);
        }
    }

    private async Task<bool> ReloadAsync()
    {
        if (configurationFile is { Required: true, Path: { } requiredPath } &&
            !File.Exists(requiredPath))
            throw new FileNotFoundException("The selected Maieutics configuration file no longer exists.",
                requiredPath);

        ValidateConfigurationFileSyntax();
        var candidate = CreateCandidate();
        var configuredConnectionFile = Path.GetFullPath(candidate.Options.Jupyter.ConnectionFile);
        if (!string.Equals(configuredConnectionFile, ConnectionFile, StringComparison.Ordinal))
            logger.LogWarning(
                "The Jupyter connection file changed in configuration. Restart Maieutics to apply this setting.");

        RuntimeSnapshot previous;
        lock (gate)
        {
            previous = GetCurrent();
            if (HasSameConfiguration(previous, candidate)) return false;
        }

        var replacement = await BuildSnapshotAsync(
            candidate,
            previous,
            checked(previous.Version + 1),
            CancellationToken.None).ConfigureAwait(false);
        string? removedOverride = null;
        var committed = false;
        try
        {
            lock (gate)
            {
                previous = GetCurrent();
                current = replacement;
                committed = true;
                switch (sessionOverride)
                {
                    case ConfiguredProfileOverride configured
                        when !replacement.Profiles.ContainsKey(configured.ProfileId):
                        removedOverride = configured.ProfileId;
                        sessionOverride = null;
                        break;
                    case AutomaticProfileOverride automatic
                        when !replacement.Sources.TryGetValue(automatic.SourceId, out var source) ||
                             !string.Equals(source.ProviderName, automatic.Provider,
                                 StringComparison.OrdinalIgnoreCase) ||
                             !Equals(source.ClientGenerationKey, automatic.ClientGenerationKey) ||
                             !AutomaticProfileMatches(
                                 replacement,
                                 source,
                                 automatic.Model,
                                 automatic.Capabilities,
                                 automatic.HostedCapabilities):
                        removedOverride = automatic.Selector;
                        sessionOverride = null;
                        TrackRetirementLocked(automatic.Generation);
                        break;
                }

                var retained = replacement.Profiles.Values
                    .Select(static profile => profile.Generation)
                    .ToHashSet<ProfileGeneration>(ReferenceEqualityComparer.Instance);
                var retired = previous.Profiles.Values
                    .Select(static profile => profile.Generation)
                    .Distinct<ProfileGeneration>(ReferenceEqualityComparer.Instance)
                    .Where(generation => !retained.Contains(generation))
                    .ToList();
                retiredGenerations.AddRange(retired.Select(static generation => generation.Retire()));

                var retainedMcp = replacement.McpServers.Values
                    .ToHashSet<McpServerGeneration>(ReferenceEqualityComparer.Instance);
                var retiredMcp = previous.McpServers.Values
                    .Distinct<McpServerGeneration>(ReferenceEqualityComparer.Instance)
                    .Where(generation => !retainedMcp.Contains(generation));
                retiredGenerations.AddRange(retiredMcp.Select(static generation => generation.Retire()));
            }
        }
        catch
        {
            // The runtime was disposed while this reload was in flight and the replacement never
            // became the active snapshot, so its freshly built generations would otherwise leak.
            // When the replacement was already committed, disposal owns its retirement instead.
            if (!committed) await RetireSnapshotAsync(replacement).ConfigureAwait(false);

            throw;
        }

        if (removedOverride is not null)
            logger.LogWarning(
                "The selected model profile {ProfileId} is no longer available; subsequent runs will use default profile {DefaultProfileId}.",
                removedOverride,
                replacement.DefaultProfileId);

        logger.LogInformation("Applied Maieutics configuration version {ConfigurationVersion}.", replacement.Version);
        return true;
    }

    private async Task<RuntimeSnapshot> BuildSnapshotAsync(
        Candidate candidate,
        RuntimeSnapshot? previous,
        long version,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mcpServers = new Dictionary<string, McpServerGeneration>(StringComparer.OrdinalIgnoreCase);
        var createdMcp = new List<McpServerGeneration>();
        var createdProfiles = new List<ProfileGeneration>();
        var reservedToolNames = builtInTools
            .Select(static function => function.Name)
            .ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var server in candidate.McpServers)
            {
                if (previous is not null &&
                    previous.McpServers.TryGetValue(server.Id, out var previousGeneration) &&
                    string.Equals(previousGeneration.GenerationKey, server.GenerationKey, StringComparison.Ordinal))
                {
                    mcpServers.Add(server.Id, previousGeneration);
                    continue;
                }

                var generation = await McpServerGeneration.CreateAsync(
                    server,
                    loggerFactory,
                    timeProvider,
                    cancellationToken,
                    mcpTransportFactory,
                    reservedToolNames).ConfigureAwait(false);
                createdMcp.Add(generation);
                mcpServers.Add(server.Id, generation);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entries = new Dictionary<string, ProfileEntry>(StringComparer.OrdinalIgnoreCase);
            var sourceMap = new Dictionary<string, IConfiguredChatClientSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in candidate.Sources) sourceMap.Add(source.Id, source.Source);

            foreach (var profile in candidate.Profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProfileGeneration generation;
                if (previous is not null &&
                    previous.Profiles.TryGetValue(profile.Id, out var previousProfile) &&
                    previousProfile.Key == profile.Key)
                {
                    generation = previousProfile.Generation;
                }
                else
                {
                    generation = new ProfileGeneration(profile.Source.Create(profile.Model), logger);
                    createdProfiles.Add(generation);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var identity = new AgentModelIdentity(
                    new AgentModelProfileId(profile.Id),
                    profile.Source.ProviderName,
                    profile.Model);
                var (capabilities, hostedCapabilities) =
                    ResolveSourceCapabilities(profile.Source, profile.Model, candidate.CapabilityRegistry);
                entries.Add(profile.Id, new ProfileEntry(
                    profile.Id,
                    profile.SourceId,
                    profile.Key,
                    identity,
                    capabilities,
                    hostedCapabilities,
                    generation));
            }

            cancellationToken.ThrowIfCancellationRequested();

            return new RuntimeSnapshot(
                version,
                candidate.Options,
                candidate.DefaultProfileId,
                entries,
                sourceMap,
                mcpServers,
                candidate.CapabilityRegistry,
                candidate.Key);
        }
        catch
        {
            var retirements = createdProfiles
                .Select(static generation => generation.Retire())
                .Concat(createdMcp.Select(static generation => generation.Retire()));
            await Task.WhenAll(retirements).ConfigureAwait(false);
            throw;
        }
    }

    private Candidate CreateCandidate()
    {
        var root = configuration.GetSection(MaieuticsOptions.SectionName);
        var options = new MaieuticsOptions();
        root.Bind(options);
        NormalizeAgentHistoryLimit(root, options);
        options.ValidateCommon();
        var capabilityRegistry = CapabilityRegistry.Create(root);
        var mcpServers = CreateMcpServers(GetMcpServersSection());

        var hasNewSchema = !string.IsNullOrWhiteSpace(root["DefaultProfile"]) ||
                           root.GetSection("Profiles").GetChildren().Any() ||
                           root.GetSection("Sources").GetChildren()
                               .Any(static source => !string.IsNullOrWhiteSpace(source["Provider"]));
        var hasLegacySchema = root.GetSection("Model").GetChildren().Any() ||
                              root.GetSection("Providers").GetChildren().Any();
        if (hasNewSchema && hasLegacySchema)
            throw new InvalidOperationException(
                "The named Sources/Profiles configuration cannot be combined with legacy Model configuration.");

        if (!hasNewSchema && !hasLegacySchema)
            return CreateCandidate(options, string.Empty, [], [], mcpServers, capabilityRegistry);

        return hasNewSchema
            ? CreateNamedCandidate(root, options, mcpServers, capabilityRegistry)
            : CreateLegacyCandidate(root, options, mcpServers, capabilityRegistry);
    }

    private IConfigurationSection GetMcpServersSection()
    {
        var mcpServers = configuration.GetSection("mcpServers");
        var servers = configuration.GetSection("servers");
        if (mcpServers.GetChildren().Any() && servers.GetChildren().Any())
            throw new InvalidOperationException(
                "mcp.json must not combine the 'mcpServers' and 'servers' top-level keys.");

        return mcpServers.GetChildren().Any() ? mcpServers : servers;
    }

    private Candidate CreateNamedCandidate(
        IConfigurationSection root,
        MaieuticsOptions options,
        IReadOnlyList<McpServerDefinition> mcpServers,
        CapabilityRegistry capabilityRegistry)
    {
        var sources = new Dictionary<string, BoundSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceSection in root.GetSection("Sources").GetChildren())
        {
            var sourceId = ValidateIdentifier(sourceSection.Key, "source");
            if (sources.ContainsKey(sourceId))
                throw new InvalidOperationException($"A model source named '{sourceId}' is configured more than once.");

            var provider = sourceSection["Provider"];
            ArgumentException.ThrowIfNullOrWhiteSpace(provider);
            if (!factories.TryGetValue(provider, out var factory))
                throw new NotSupportedException($"The model provider '{provider}' is not registered.");

            var source = factory.BindSource(sourceId, sourceSection);
            ValidateBoundSource(factory, source);
            sources.Add(sourceId, new BoundSource(sourceId, source));
        }

        if (sources.Count == 0) throw new InvalidOperationException("At least one model source must be configured.");

        var profiles = new List<CandidateProfile>();
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profileSection in root.GetSection("Profiles").GetChildren())
        {
            var profileId = ValidateIdentifier(profileSection.Key, "profile");
            if (!profileIds.Add(profileId))
                throw new InvalidOperationException(
                    $"A model profile named '{profileId}' is configured more than once.");

            ValidateKeys(profileSection, "Source", "Model");
            var sourceId = ValidateIdentifier(profileSection["Source"], "source");
            var model = profileSection["Model"];
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            if (!sources.TryGetValue(sourceId, out var source))
                throw new InvalidOperationException(
                    $"Model profile '{profileId}' references unknown source '{sourceId}'.");

            profiles.Add(new CandidateProfile(
                profileId,
                source.Id,
                model,
                source.Source,
                new ProfileKey(
                    NormalizeIdentifier(profileId),
                    NormalizeIdentifier(source.Id),
                    source.Source.ProviderName,
                    source.Source.ClientGenerationKey,
                    model)));
        }

        if (profiles.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(options.DefaultProfile))
                throw new InvalidOperationException(
                    $"Default model profile '{options.DefaultProfile}' does not exist.");

            return CreateCandidate(
                options,
                string.Empty,
                profiles,
                sources.Values.ToArray(),
                mcpServers,
                capabilityRegistry);
        }

        var defaultProfileId = ValidateIdentifier(options.DefaultProfile, "profile");
        var defaultProfile = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, defaultProfileId, StringComparison.OrdinalIgnoreCase));
        if (defaultProfile is null)
            throw new InvalidOperationException($"Default model profile '{defaultProfileId}' does not exist.");

        return CreateCandidate(
            options,
            defaultProfile.Id,
            profiles,
            sources.Values.ToArray(),
            mcpServers,
            capabilityRegistry);
    }

    private Candidate CreateLegacyCandidate(
        IConfigurationSection root,
        MaieuticsOptions options,
        IReadOnlyList<McpServerDefinition> mcpServers,
        CapabilityRegistry capabilityRegistry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model.Name);
        var provider = string.IsNullOrWhiteSpace(options.Model.Provider) ? "OpenAI" : options.Model.Provider;
        if (!factories.TryGetValue(provider, out var factory))
            throw new NotSupportedException($"The model provider '{provider}' is not registered.");

        var sourceId = ValidateIdentifier(provider.ToLowerInvariant(), "source");
        var profileId = "default";
        var sourceSection = MergeLegacySourceSections(
            root.GetSection($"Providers:{provider}"),
            root.GetSection($"Sources:{sourceId}"));
        var source = factory.BindSource(sourceId, sourceSection);
        ValidateBoundSource(factory, source);
        var profile = new CandidateProfile(
            profileId,
            sourceId,
            options.Model.Name,
            source,
            new ProfileKey(
                NormalizeIdentifier(profileId),
                NormalizeIdentifier(sourceId),
                source.ProviderName,
                source.ClientGenerationKey,
                options.Model.Name));
        return CreateCandidate(
            options,
            profileId,
            [profile],
            [new BoundSource(sourceId, source)],
            mcpServers,
            capabilityRegistry);
    }

    private static Candidate CreateCandidate(
        MaieuticsOptions options,
        string defaultProfileId,
        IReadOnlyList<CandidateProfile> profiles,
        IReadOnlyList<BoundSource> sources,
        IReadOnlyList<McpServerDefinition> mcpServers,
        CapabilityRegistry capabilityRegistry)
    {
        var key = new RuntimeKey(
            NormalizeIdentifier(defaultProfileId),
            options.SystemPrompt,
            options.Agent.MaxRetainedTurns,
            options.Agent.MaxHistoryBytes,
            options.Agent.MaxInputCharacters,
            options.Agent.MaxResponseCharacters,
            options.Agent.MaxModelIterationsPerTurn,
            options.Agent.MaxTurnDuration,
            options.Agent.MaxToolCallsPerTurn,
            options.Agent.MaxToolArgumentsBytes,
            options.Agent.MaxToolResultBytes,
            options.Agent.MaxToolProgressEventsPerCall,
            options.Agent.EventBufferCapacity,
            Path.GetFullPath(options.Jupyter.ConnectionFile),
            options.Jupyter.FlushInterval,
            options.Jupyter.FlushCharacters);
        return new Candidate(
            options,
            defaultProfileId,
            profiles,
            sources,
            mcpServers,
            capabilityRegistry,
            key);
    }

    private IReadOnlyList<McpServerDefinition> CreateMcpServers(IConfigurationSection section)
    {
        var result = new List<McpServerDefinition>();
        var serverIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serverSection in section.GetChildren())
        {
            var serverId = serverSection.Key;
            if (string.IsNullOrWhiteSpace(serverId) || !serverIds.Add(serverId))
                throw new InvalidOperationException("MCP server identifiers must be non-empty and unique.");

            var serverOptions = new MaieuticsMcpServerOptions();
            serverSection.Bind(serverOptions);
            if (!serverOptions.Enabled) continue;

            var transport = ResolveMcpTransport(serverId, serverSection);
            var allowedKeys = transport == McpServerTransportKind.Stdio
                ? new[]
                {
                    "Enabled", "Type", "Transport", "Command", "Arguments", "Args", "WorkingDirectory",
                    "EnvironmentVariables", "Env", "InitializationTimeout", "RequestTimeout", "ShutdownTimeout"
                }
                :
                [
                    "Enabled", "Type", "Transport", "Url", "Headers", "ConnectionTimeout",
                    "InitializationTimeout", "RequestTimeout"
                ];
            ValidateConfigurationKeys(serverSection, $"MCP server '{serverId}'", allowedKeys);

            ValidatePositiveTimeout(serverOptions.InitializationTimeout, serverId, "InitializationTimeout");
            ValidatePositiveTimeout(serverOptions.RequestTimeout, serverId, "RequestTimeout");

            McpTransportDefinition transportDefinition;
            var shutdownTimeout = TimeSpan.Zero;
            var connectionTimeout = TimeSpan.Zero;

            if (transport == McpServerTransportKind.Stdio)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(serverOptions.Command);
                ArgumentNullException.ThrowIfNull(serverOptions.Arguments);
                ArgumentNullException.ThrowIfNull(serverOptions.EnvironmentVariables);
                var command = serverOptions.Command;
                var arguments = serverOptions.Arguments.ToArray();
                var environmentVariables = new Dictionary<string, string?>(
                    serverOptions.EnvironmentVariables,
                    StringComparer.Ordinal);
                if (serverSection.GetSection("WorkingDirectory").Value is not null &&
                    string.IsNullOrWhiteSpace(serverOptions.WorkingDirectory))
                    throw new InvalidOperationException(
                        $"MCP server '{serverId}' WorkingDirectory cannot be empty when configured.");

                string? workingDirectory = null;
                if (!string.IsNullOrWhiteSpace(serverOptions.WorkingDirectory))
                    workingDirectory = Path.GetFullPath(serverOptions.WorkingDirectory, startupDirectory.Path);

                ValidatePositiveTimeout(serverOptions.ShutdownTimeout, serverId, "ShutdownTimeout");
                shutdownTimeout = serverOptions.ShutdownTimeout;
                transportDefinition = new StdioMcpTransportDefinition(
                    command,
                    arguments,
                    workingDirectory,
                    environmentVariables);
            }
            else
            {
                if (!Uri.TryCreate(serverOptions.Url, UriKind.Absolute, out var endpoint) ||
                    (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        $"MCP server '{serverId}' Url must be an absolute HTTP or HTTPS URI.");

                if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                    !endpoint.IsLoopback)
                    throw new InvalidOperationException(
                        $"MCP server '{serverId}' must use HTTPS unless its endpoint is loopback.");

                ArgumentNullException.ThrowIfNull(serverOptions.Headers);
                foreach (var pair in serverOptions.Headers)
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                        throw new InvalidOperationException(
                            $"MCP server '{serverId}' contains an invalid HTTP header.");

                var headers = new Dictionary<string, string>(serverOptions.Headers, StringComparer.OrdinalIgnoreCase);
                ValidatePositiveTimeout(serverOptions.ConnectionTimeout, serverId, "ConnectionTimeout");
                connectionTimeout = serverOptions.ConnectionTimeout;
                transportDefinition = new HttpMcpTransportDefinition(endpoint, headers);
            }

            var generationKey = McpServerDefinition.CreateGenerationKey(
                transportDefinition,
                serverOptions.InitializationTimeout,
                serverOptions.RequestTimeout,
                shutdownTimeout,
                connectionTimeout);
            result.Add(new McpServerDefinition(
                serverId,
                transportDefinition,
                serverOptions.InitializationTimeout,
                serverOptions.RequestTimeout,
                shutdownTimeout,
                connectionTimeout,
                generationKey));
        }

        return result;
    }

    private static McpServerTransportKind ResolveMcpTransport(
        string serverId,
        IConfigurationSection serverSection)
    {
        var configured = serverSection["Transport"] ?? serverSection["Type"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Enum.TryParse<McpServerTransportKind>(configured, true, out var transport) &&
                Enum.IsDefined(transport))
                return transport;

            if (string.Equals(configured, "sse", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"MCP server '{serverId}' uses the unsupported 'sse' transport.");

            throw new InvalidOperationException(
                $"MCP server '{serverId}' must configure Transport as 'stdio' or 'http'.");
        }

        return !string.IsNullOrWhiteSpace(serverSection["Url"])
            ? McpServerTransportKind.Http
            : McpServerTransportKind.Stdio;
    }

    private static void ValidatePositiveTimeout(TimeSpan value, string serverId, string field)
    {
        if (value <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"MCP server '{serverId}' {field} must be positive.");
    }

    private static void ValidateConfigurationKeys(
        IConfigurationSection section,
        string description,
        params string[] allowed)
    {
        var allowedKeys = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = section.GetChildren().FirstOrDefault(child => !allowedKeys.Contains(child.Key));
        if (unknown is not null)
            throw new InvalidOperationException(
                $"Configuration field '{unknown.Path}' is not valid for {description}.");
    }

    private static void NormalizeAgentHistoryLimit(IConfigurationSection root, MaieuticsOptions options)
    {
        var agent = root.GetSection("Agent");
        var configuredKeys = agent.GetChildren()
            .Select(static section => section.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasMaxHistoryBytes = configuredKeys.Contains("MaxHistoryBytes");
        var hasMaxHistoryCharacters = configuredKeys.Contains("MaxHistoryCharacters");
        if (hasMaxHistoryBytes && hasMaxHistoryCharacters)
            throw new InvalidOperationException(
                "Maieutics:Agent:MaxHistoryBytes cannot be combined with legacy MaxHistoryCharacters.");

        if (!hasMaxHistoryCharacters) return;

        if (options.Agent.MaxHistoryCharacters is not { } maxHistoryCharacters)
            throw new InvalidOperationException(
                "Legacy Maieutics:Agent:MaxHistoryCharacters must be an integer.");

        try
        {
            options.Agent.MaxHistoryBytes = checked(maxHistoryCharacters * 2);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "Legacy Maieutics:Agent:MaxHistoryCharacters is too large to convert to MaxHistoryBytes.",
                exception);
        }
    }

    private static bool HasSameConfiguration(RuntimeSnapshot snapshot, Candidate candidate)
    {
        if (snapshot.Key != candidate.Key ||
            snapshot.Profiles.Count != candidate.Profiles.Count ||
            snapshot.Sources.Count != candidate.Sources.Count ||
            snapshot.McpServers.Count != candidate.McpServers.Count ||
            snapshot.CapabilityRegistry != candidate.CapabilityRegistry)
            return false;

        foreach (var profile in candidate.Profiles)
            if (!snapshot.Profiles.TryGetValue(profile.Id, out var currentProfile) ||
                currentProfile.Key != profile.Key)
                return false;

        foreach (var source in candidate.Sources)
            if (!snapshot.Sources.TryGetValue(source.Id, out var currentSource) ||
                !string.Equals(currentSource.ProviderName, source.Source.ProviderName,
                    StringComparison.OrdinalIgnoreCase) ||
                !Equals(currentSource.ClientGenerationKey, source.Source.ClientGenerationKey))
                return false;

        foreach (var server in candidate.McpServers)
            if (!snapshot.McpServers.TryGetValue(server.Id, out var currentServer) ||
                !string.Equals(currentServer.GenerationKey, server.GenerationKey, StringComparison.Ordinal))
                return false;

        return true;
    }

    private static IConfigurationSection MergeLegacySourceSections(
        IConfigurationSection legacy,
        IConfigurationSection conventional)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        AddSection(values, legacy);
        AddSection(values, conventional);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build()
            .GetSection("Source");
    }

    private static void AddSection(Dictionary<string, string?> values, IConfigurationSection section)
    {
        foreach (var pair in section.AsEnumerable(true))
            if (!string.IsNullOrEmpty(pair.Key))
                values[$"Source:{pair.Key}"] = pair.Value;
    }

    private static string ValidateIdentifier(string? value, string kind)
    {
        try
        {
            return new AgentModelProfileId(value ?? string.Empty).Value;
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"The model {kind} identifier is invalid.", kind, exception);
        }
    }

    private static string NormalizeIdentifier(string value)
    {
        return value.ToUpperInvariant();
    }

    private static void ValidateKeys(IConfigurationSection section, params string[] allowed)
    {
        var allowedKeys = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = section.GetChildren().FirstOrDefault(child => !allowedKeys.Contains(child.Key));
        if (unknown is not null)
            throw new InvalidOperationException(
                $"Configuration field '{unknown.Path}' is not valid for a model profile.");
    }

    private static void ValidateBoundSource(
        IConfiguredChatClientFactory factory,
        IConfiguredChatClientSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(factory.ProviderName, source.ProviderName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Provider factory '{factory.ProviderName}' returned source '{source.ProviderName}'.");

        ArgumentNullException.ThrowIfNull(source.ClientGenerationKey);
    }

    private void ValidateConfigurationFileSyntax()
    {
        if (configurationFile.Path is not { } path || !File.Exists(path)) return;

        using var stream = File.OpenRead(path);
        using var _ = JsonDocument.Parse(stream);
    }

    private void ObserveCompletedRetirements()
    {
        lock (gate)
        {
            for (var index = retiredGenerations.Count - 1; index >= 0; index--)
            {
                var task = retiredGenerations[index];
                if (!task.IsCompleted) continue;

                _ = task.Exception;
                retiredGenerations.RemoveAt(index);
            }
        }
    }

    private RuntimeSnapshot GetCurrent()
    {
        return current ?? throw new ObjectDisposedException(nameof(MaieuticsRuntimeConfiguration));
    }

    private MaieuticsModelProfileSelection CreateModelProfileSelectionLocked(RuntimeSnapshot snapshot)
    {
        var configuredOverride = sessionOverride as ConfiguredProfileOverride;
        var automaticOverride = sessionOverride as AutomaticProfileOverride;
        var selectedConfiguredProfileId = configuredOverride?.ProfileId ??
                                          (automaticOverride is null ? snapshot.DefaultProfileId : null);
        var profiles = snapshot.Profiles.Values
            .OrderBy(static profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new MaieuticsModelProfileInfo(
                profile.Id,
                profile.SourceId,
                profile.Identity.Provider,
                profile.Identity.Model,
                string.Equals(profile.Id, snapshot.DefaultProfileId, StringComparison.OrdinalIgnoreCase),
                string.Equals(profile.Id, selectedConfiguredProfileId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (automaticOverride is not null) profiles.Add(automaticOverride.ToProfileInfo(true));

        return new MaieuticsModelProfileSelection(
            snapshot.DefaultProfileId,
            automaticOverride?.Selector ?? selectedConfiguredProfileId ?? string.Empty,
            sessionOverride is not null,
            profiles);
    }

    private RuntimeProfileSelection SelectRuntimeProfile(RuntimeSnapshot snapshot)
    {
        if (sessionOverride is AutomaticProfileOverride automatic)
            return new RuntimeProfileSelection(
                snapshot,
                automatic.Generation,
                automatic.Identity,
                automatic.Capabilities,
                automatic.HostedCapabilities);

        var profileId = sessionOverride is ConfiguredProfileOverride configured
            ? configured.ProfileId
            : snapshot.DefaultProfileId;
        if (string.IsNullOrEmpty(profileId)) throw new InvalidOperationException("No model profile is configured.");

        var entry = snapshot.Profiles[profileId];
        return new RuntimeProfileSelection(
            snapshot,
            entry.Generation,
            entry.Identity,
            entry.Capabilities,
            entry.HostedCapabilities);
    }

    private async Task RollbackRuntimeProfileAcquisitionAsync(
        ProfileGenerationLease? generationLease,
        IReadOnlyList<McpServerGeneration.McpServerLease> mcpLeases)
    {
        foreach (var lease in mcpLeases)
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "An MCP connection lease failed during run-profile acquisition rollback.");
            }

        if (generationLease is null) return;
        try
        {
            await generationLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "The model provider lease failed during run-profile acquisition rollback.");
        }
    }

    private static AgentModelProfileId CreateAutomaticProfileId(string sourceId, string model)
    {
        var identity = Encoding.UTF8.GetBytes($"{sourceId}\0{model}");
        var hash = SHA256.HashData(identity);
        return new AgentModelProfileId($"auto-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}");
    }

    private static AgentSessionOptions CreateAgentOptions(MaieuticsOptions options)
    {
        return new AgentSessionOptions
        {
            SystemPrompt = options.SystemPrompt,
            MaxRetainedTurns = options.Agent.MaxRetainedTurns,
            MaxHistoryBytes = options.Agent.MaxHistoryBytes,
            MaxInputCharacters = options.Agent.MaxInputCharacters,
            MaxResponseCharacters = options.Agent.MaxResponseCharacters,
            MaxModelIterationsPerTurn = options.Agent.MaxModelIterationsPerTurn,
            MaxTurnDuration = options.Agent.MaxTurnDuration,
            MaxToolCallsPerTurn = options.Agent.MaxToolCallsPerTurn,
            MaxToolArgumentsBytes = options.Agent.MaxToolArgumentsBytes,
            MaxToolResultBytes = options.Agent.MaxToolResultBytes,
            MaxToolProgressEventsPerCall = options.Agent.MaxToolProgressEventsPerCall,
            EventBufferCapacity = options.Agent.EventBufferCapacity
        };
    }

    private static bool AutomaticProfileMatches(
        RuntimeSnapshot snapshot,
        IConfiguredChatClientSource source,
        string model,
        AgentModelCapabilities capabilities,
        IReadOnlyList<string> hostedCapabilities)
    {
        var (resolvedCapabilities, resolvedHosted) =
            ResolveSourceCapabilities(source, model, snapshot.CapabilityRegistry);
        return resolvedCapabilities == capabilities &&
               HostedCapabilitiesEqual(resolvedHosted, hostedCapabilities);
    }

    private static (AgentModelCapabilities Capabilities, IReadOnlyList<string> HostedCapabilities)
        ResolveSourceCapabilities(
            IConfiguredChatClientSource source,
            string model,
            CapabilityRegistry capabilityRegistry)
    {
        return (source.Capabilities, capabilityRegistry.Resolve(source, model).Effective);
    }

    private static bool HostedCapabilitiesEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IConfiguredChatClientFactory> CreateFactoryRegistry(
        IEnumerable<IConfiguredChatClientFactory> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new Dictionary<string, IConfiguredChatClientFactory>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in source)
        {
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentException.ThrowIfNullOrWhiteSpace(factory.ProviderName);
            if (!result.TryAdd(factory.ProviderName, factory))
                throw new InvalidOperationException(
                    $"A chat client provider named '{factory.ProviderName}' is already registered.");
        }

        return result;
    }

    private sealed record Candidate(
        MaieuticsOptions Options,
        string DefaultProfileId,
        IReadOnlyList<CandidateProfile> Profiles,
        IReadOnlyList<BoundSource> Sources,
        IReadOnlyList<McpServerDefinition> McpServers,
        CapabilityRegistry CapabilityRegistry,
        RuntimeKey Key);

    private sealed record CandidateProfile(
        string Id,
        string SourceId,
        string Model,
        IConfiguredChatClientSource Source,
        ProfileKey Key);

    private sealed record BoundSource(string Id, IConfiguredChatClientSource Source);

    private sealed record ProfileKey(
        string ProfileId,
        string SourceId,
        string ProviderName,
        object ClientGenerationKey,
        string Model);

    private sealed record RuntimeKey(
        string DefaultProfileId,
        string? SystemPrompt,
        int MaxRetainedTurns,
        int MaxHistoryBytes,
        int MaxInputCharacters,
        int MaxResponseCharacters,
        int MaxModelIterationsPerTurn,
        TimeSpan MaxTurnDuration,
        int MaxToolCallsPerTurn,
        int MaxToolArgumentsBytes,
        int MaxToolResultBytes,
        int MaxToolProgressEventsPerCall,
        int EventBufferCapacity,
        string ConnectionFile,
        TimeSpan FlushInterval,
        int FlushCharacters);

    private sealed record RuntimeSnapshot(
        long Version,
        MaieuticsOptions Options,
        string DefaultProfileId,
        IReadOnlyDictionary<string, ProfileEntry> Profiles,
        IReadOnlyDictionary<string, IConfiguredChatClientSource> Sources,
        IReadOnlyDictionary<string, McpServerGeneration> McpServers,
        CapabilityRegistry CapabilityRegistry,
        RuntimeKey Key);

    private sealed record ProfileEntry(
        string Id,
        string SourceId,
        ProfileKey Key,
        AgentModelIdentity Identity,
        AgentModelCapabilities Capabilities,
        IReadOnlyList<string> HostedCapabilities,
        ProfileGeneration Generation);

    private abstract class ProfileOverride;

    private sealed class ConfiguredProfileOverride(string profileId) : ProfileOverride
    {
        internal string ProfileId { get; } = profileId;
    }

    private sealed class AutomaticProfileOverride(
        AutomaticProfileCandidate candidate,
        ProfileGeneration generation) : ProfileOverride
    {
        internal string Selector { get; } = candidate.Selector;

        internal string SourceId { get; } = candidate.SourceId;

        internal string Provider { get; } = candidate.Provider;

        internal string Model { get; } = candidate.Model;

        internal object ClientGenerationKey { get; } = candidate.ClientGenerationKey;

        internal AgentModelCapabilities Capabilities { get; } = candidate.Capabilities;

        internal IReadOnlyList<string> HostedCapabilities { get; } = candidate.HostedCapabilities;

        internal ProfileGeneration Generation { get; } = generation;

        internal AgentModelIdentity Identity { get; } = new(
            CreateAutomaticProfileId(candidate.SourceId, candidate.Model),
            candidate.Provider,
            candidate.Model);

        internal bool Matches(AutomaticProfileCandidate other)
        {
            return string.Equals(SourceId, other.SourceId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Model, other.Model, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Provider, other.Provider, StringComparison.OrdinalIgnoreCase) &&
                   Equals(ClientGenerationKey, other.ClientGenerationKey) &&
                   Capabilities == other.Capabilities &&
                   HostedCapabilitiesEqual(HostedCapabilities, other.HostedCapabilities);
        }

        internal MaieuticsModelProfileInfo ToProfileInfo(bool isSelected)
        {
            return new MaieuticsModelProfileInfo(
                Selector,
                SourceId,
                Provider,
                Model,
                false,
                isSelected,
                true);
        }
    }

    private sealed record AutomaticProfileCandidate(
        string Selector,
        string SourceId,
        string Provider,
        string Model,
        object ClientGenerationKey,
        AgentModelCapabilities Capabilities,
        IReadOnlyList<string> HostedCapabilities,
        IConfiguredChatClientSource Source)
    {
        internal MaieuticsModelProfileInfo ToProfileInfo(bool isSelected)
        {
            return new MaieuticsModelProfileInfo(
                Selector,
                SourceId,
                Provider,
                Model,
                false,
                isSelected,
                true);
        }
    }

    private sealed record RuntimeProfileSelection(
        RuntimeSnapshot Snapshot,
        ProfileGeneration Generation,
        AgentModelIdentity Identity,
        AgentModelCapabilities Capabilities,
        IReadOnlyList<string> HostedCapabilities);

    private sealed class RuntimeProfileLease(
        ProfileGenerationLease generationLease,
        IReadOnlyList<McpServerGeneration.McpServerLease> mcpLeases,
        AgentRunProfile profile) : IAgentRunProfileLease
    {
        private readonly TaskCompletionSource disposal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int disposed;

        public AgentRunProfile Profile { get; } = profile;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) _ = DisposeCoreAsync();

            return new ValueTask(disposal.Task);
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                var releases = mcpLeases
                    .Select(static lease => lease.DisposeAsync().AsTask())
                    .Append(generationLease.DisposeAsync().AsTask());
                await Task.WhenAll(releases).ConfigureAwait(false);
                disposal.TrySetResult();
            }
            catch (Exception exception)
            {
                disposal.TrySetException(exception);
            }
        }
    }

    private sealed class ProfileGeneration(IChatClient client, ILogger logger)
    {
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock gate = new();
        private int references = 1;
        private bool retired;

        internal IChatClient Client { get; } = client ?? throw new ArgumentNullException(nameof(client));

        internal ProfileGenerationLease Acquire()
        {
            lock (gate)
            {
                if (retired) throw new ObjectDisposedException(nameof(ProfileGeneration));

                references = checked(references + 1);
                return new ProfileGenerationLease(this);
            }
        }

        internal Task Retire()
        {
            var dispose = false;
            lock (gate)
            {
                if (!retired)
                {
                    retired = true;
                    dispose = --references == 0;
                }
            }

            if (dispose) _ = DisposeClientAsync();

            return disposed.Task;
        }

        internal ValueTask ReleaseAsync()
        {
            bool dispose;
            lock (gate)
            {
                if (references <= 0) return ValueTask.CompletedTask;

                dispose = --references == 0 && retired;
            }

            return dispose ? new ValueTask(DisposeClientAsync()) : ValueTask.CompletedTask;
        }

        private async Task DisposeClientAsync()
        {
            try
            {
                switch (Client)
                {
                    // ReSharper disable once SuspiciousTypeConversion.Global
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The retired model provider client failed during disposal.");
            }
            finally
            {
                disposed.TrySetResult();
            }
        }
    }

    private sealed class ProfileGenerationLease(ProfileGeneration generation) : IAsyncDisposable
    {
        private int disposed;

        public ValueTask DisposeAsync()
        {
            return Interlocked.Exchange(ref disposed, 1) == 0
                ? generation.ReleaseAsync()
                : ValueTask.CompletedTask;
        }
    }

    private sealed record CachedDiscovery(
        DiscoveredModelGroup Result,
        DateTime CachedAt,
        long ConfigurationVersion,
        object ClientGenerationKey);
}
