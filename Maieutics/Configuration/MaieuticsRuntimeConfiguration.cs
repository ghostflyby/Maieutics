using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Maieutics.Agent;
using Maieutics.Jupyter;
using Maieutics.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Maieutics.Configuration;

internal sealed class MaieuticsRuntimeConfiguration : IMaieuticsRuntimeConfiguration, IAsyncDisposable
{
    private readonly IConfiguration configuration;
    private readonly MaieuticsConfigurationFile configurationFile;
    private readonly MaieuticsConfigurationFileErrors fileErrors;
    private readonly IReadOnlyDictionary<string, IConfiguredChatClientFactory> factories;
    private readonly ILogger<MaieuticsRuntimeConfiguration> logger;
    private readonly Lock gate = new();

    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, CachedDiscovery> discoveryCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Channel<byte> reloadSignals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    private readonly List<Task> retiredGenerations = [];
    private readonly IDisposable reloadSubscription;
    private readonly IDisposable fileErrorSubscription;
    private readonly Task reloadLoop;
    private RuntimeSnapshot? current;
    private string? sessionOverride;
    private int disposed;
    private long reloadAttempt;
    private long completedReloadAttempt;

    public MaieuticsRuntimeConfiguration(
        IConfiguration configuration,
        MaieuticsConfigurationFile configurationFile,
        MaieuticsConfigurationFileErrors fileErrors,
        IEnumerable<IConfiguredChatClientFactory> factories,
        ILogger<MaieuticsRuntimeConfiguration> logger)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.configurationFile = configurationFile ?? throw new ArgumentNullException(nameof(configurationFile));
        this.fileErrors = fileErrors ?? throw new ArgumentNullException(nameof(fileErrors));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.factories = CreateFactoryRegistry(factories);

        var candidate = CreateCandidate();
        current = BuildSnapshot(candidate, previous: null, version: 1);
        ConnectionFile = Path.GetFullPath(candidate.Options.Jupyter.ConnectionFile);

        fileErrorSubscription = fileErrors.RegisterSignal(SignalReload);
        reloadSubscription = ChangeToken.OnChange(configuration.GetReloadToken, SignalReload);
        reloadLoop = Task.Run(ProcessReloadsAsync);
        SignalReload();

        logger.LogInformation(
            "Using Maieutics configuration file {ConfigurationPath} selected from {ConfigurationSource}.",
            configurationFile.Path ?? "(none)",
            configurationFile.Source);
    }

    public string ConnectionFile { get; }

    internal long ReloadAttempt => Interlocked.Read(ref reloadAttempt);

    internal long CompletedReloadAttempt => Interlocked.Read(ref completedReloadAttempt);

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

    public IAgentRunProfileLease Acquire()
    {
        lock (gate)
        {
            var snapshot = GetCurrent();
            var profileId = sessionOverride ?? snapshot.DefaultProfileId;
            var entry = snapshot.Profiles[profileId];
            var generationLease = entry.Generation.Acquire();
            return new RuntimeProfileLease(
                generationLease,
                new AgentRunProfile(
                    entry.Generation.Client,
                    CreateAgentOptions(snapshot.Options),
                    entry.Identity,
                    entry.Capabilities));
        }
    }

    public MaieuticsModelProfileSelection GetModelProfileSelection()
    {
        lock (gate)
        {
            var snapshot = GetCurrent();
            var selected = sessionOverride ?? snapshot.DefaultProfileId;
            var profiles = snapshot.Profiles.Values
                .OrderBy(static profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                .Select(profile => new MaieuticsModelProfileInfo(
                    profile.Id,
                    profile.SourceId,
                    profile.Identity.Provider,
                    profile.Identity.Model,
                    string.Equals(profile.Id, snapshot.DefaultProfileId, StringComparison.OrdinalIgnoreCase),
                    string.Equals(profile.Id, selected, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            return new MaieuticsModelProfileSelection(
                snapshot.DefaultProfileId,
                selected,
                sessionOverride is not null,
                profiles);
        }
    }

    public void SelectModelProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        lock (gate)
        {
            var snapshot = GetCurrent();
            if (!snapshot.Profiles.TryGetValue(profileId, out var profile))
            {
                throw new ArgumentException($"The model profile '{profileId}' does not exist.", nameof(profileId));
            }

            sessionOverride = profile.Id;
        }
    }

    public void ResetModelProfile()
    {
        lock (gate)
        {
            _ = GetCurrent();
            sessionOverride = null;
        }
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
        IReadOnlyList<(string sourceId, string provider, IConfiguredChatClientSource source)> targets;
        lock (gate)
        {
            var snapshot = GetCurrent();
            targets =
            [
                .. snapshot.Sources
                    .Where(s => sourceId is null ||
                                string.Equals(s.Key, sourceId, StringComparison.OrdinalIgnoreCase))
                    .Select(s => (s.Key, s.Value.ProviderName, s.Value))
            ];
        }

        var now = DateTime.UtcNow;
        var results = new List<DiscoveredModelGroup>(targets.Count);
        foreach (var (sid, provider, source) in targets)
        {
            if (source is not IModelDiscoverySource discovery)
            {
                continue;
            }

            if (!refresh && discoveryCache.TryGetValue(sid, out var cached) &&
                now - cached.CachedAt < DiscoveryCacheTtl)
            {
                results.Add(cached.Result);
                continue;
            }

            try
            {
                var models = await discovery.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
                var group = new DiscoveredModelGroup(sid, provider, null, models);
                discoveryCache[sid] = new CachedDiscovery(group, now);
                results.Add(group);
            }
            catch (Exception exception)
            {
                results.Add(new DiscoveredModelGroup(sid, provider, exception.Message, []));
            }
        }

        return results;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            await reloadLoop.ConfigureAwait(false);
            return;
        }

        reloadSubscription.Dispose();
        fileErrorSubscription.Dispose();
        reloadSignals.Writer.TryComplete();
        await reloadLoop.ConfigureAwait(false);

        ProfileGeneration[] generations;
        Task[] retired;
        lock (gate)
        {
            generations = GetCurrent().Profiles.Values
                .Select(static profile => profile.Generation)
                .Distinct<ProfileGeneration>(ReferenceEqualityComparer.Instance)
                .ToArray();
            current = null;
            retired = retiredGenerations.ToArray();
        }

        var currentRetirements = generations.Select(static generation => generation.Retire());
        await Task.WhenAll(retired.Concat(currentRetirements)).ConfigureAwait(false);
    }

    private void SignalReload() => reloadSignals.Writer.TryWrite(0);

    private async Task ProcessReloadsAsync()
    {
        await foreach (var _ in reloadSignals.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var attempt = Interlocked.Increment(ref reloadAttempt);
            var loadError = fileErrors.TakeLatest();
            try
            {
                Reload();
            }
            catch (Exception exception)
            {
                logger.LogError(loadError ?? exception,
                    "Rejected an invalid Maieutics configuration update. Version {ConfigurationVersion} remains active.",
                    Version);
            }

            ObserveCompletedRetirements();
            Interlocked.Exchange(ref completedReloadAttempt, attempt);
        }
    }

    private void Reload()
    {
        discoveryCache.Clear();
        if (configurationFile is { Required: true, Path: { } requiredPath } &&
            !File.Exists(requiredPath))
        {
            throw new FileNotFoundException("The selected Maieutics configuration file no longer exists.",
                requiredPath);
        }

        ValidateConfigurationFileSyntax();
        var candidate = CreateCandidate();
        var configuredConnectionFile = Path.GetFullPath(candidate.Options.Jupyter.ConnectionFile);
        if (!string.Equals(configuredConnectionFile, ConnectionFile, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "The Jupyter connection file changed in configuration. Restart Maieutics to apply this setting.");
        }

        RuntimeSnapshot previous;
        lock (gate)
        {
            previous = GetCurrent();
            if (HasSameConfiguration(previous, candidate))
            {
                return;
            }
        }

        var replacement = BuildSnapshot(candidate, previous, checked(previous.Version + 1));
        string? removedOverride = null;
        lock (gate)
        {
            previous = GetCurrent();
            current = replacement;
            if (sessionOverride is not null && !replacement.Profiles.ContainsKey(sessionOverride))
            {
                removedOverride = sessionOverride;
                sessionOverride = null;
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
        }

        if (removedOverride is not null)
        {
            logger.LogWarning(
                "The selected model profile {ProfileId} was removed; subsequent runs will use default profile {DefaultProfileId}.",
                removedOverride,
                replacement.DefaultProfileId);
        }

        logger.LogInformation("Applied Maieutics configuration version {ConfigurationVersion}.", replacement.Version);
    }

    private RuntimeSnapshot BuildSnapshot(Candidate candidate, RuntimeSnapshot? previous, long version)
    {
        var entries = new Dictionary<string, ProfileEntry>(StringComparer.OrdinalIgnoreCase);
        var sourceMap = new Dictionary<string, IConfiguredChatClientSource>(StringComparer.OrdinalIgnoreCase);
        var created = new List<ProfileGeneration>();
        try
        {
            foreach (var profile in candidate.Profiles)
            {
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
                    created.Add(generation);
                }

                var identity = new AgentModelIdentity(
                    new AgentModelProfileId(profile.Id),
                    profile.Source.ProviderName,
                    profile.Model);
                entries.Add(profile.Id, new ProfileEntry(
                    profile.Id,
                    profile.SourceId,
                    profile.Key,
                    identity,
                    profile.Source.Capabilities,
                    generation));

                sourceMap.TryAdd(profile.SourceId, profile.Source);
            }

            return new RuntimeSnapshot(
                version,
                candidate.Options,
                candidate.DefaultProfileId,
                entries,
                sourceMap,
                candidate.Key);
        }
        catch
        {
            foreach (var generation in created)
            {
                generation.Retire().GetAwaiter().GetResult();
            }

            throw;
        }
    }

    private Candidate CreateCandidate()
    {
        var root = configuration.GetSection(MaieuticsOptions.SectionName);
        var options = new MaieuticsOptions();
        root.Bind(options);
        options.ValidateCommon();

        var hasNewSchema = !string.IsNullOrWhiteSpace(root["DefaultProfile"]) ||
                           root.GetSection("Profiles").GetChildren().Any();
        var hasLegacySchema = root.GetSection("Model").GetChildren().Any() ||
                              root.GetSection("Providers").GetChildren().Any();
        if (hasNewSchema && hasLegacySchema)
        {
            throw new InvalidOperationException(
                "The named Sources/Profiles configuration cannot be combined with legacy Model configuration.");
        }

        return hasNewSchema
            ? CreateNamedCandidate(root, options)
            : CreateLegacyCandidate(root, options);
    }

    private Candidate CreateNamedCandidate(IConfigurationSection root, MaieuticsOptions options)
    {
        var sources = new Dictionary<string, BoundSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceSection in root.GetSection("Sources").GetChildren())
        {
            var sourceId = ValidateIdentifier(sourceSection.Key, "source");
            if (sources.ContainsKey(sourceId))
            {
                throw new InvalidOperationException($"A model source named '{sourceId}' is configured more than once.");
            }

            var provider = sourceSection["Provider"];
            ArgumentException.ThrowIfNullOrWhiteSpace(provider);
            if (!factories.TryGetValue(provider, out var factory))
            {
                throw new NotSupportedException($"The model provider '{provider}' is not registered.");
            }

            var source = factory.BindSource(sourceId, sourceSection);
            ValidateBoundSource(factory, source);
            sources.Add(sourceId, new BoundSource(sourceId, source));
        }

        if (sources.Count == 0)
        {
            throw new InvalidOperationException("At least one model source must be configured.");
        }

        var profiles = new List<CandidateProfile>();
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profileSection in root.GetSection("Profiles").GetChildren())
        {
            var profileId = ValidateIdentifier(profileSection.Key, "profile");
            if (!profileIds.Add(profileId))
            {
                throw new InvalidOperationException(
                    $"A model profile named '{profileId}' is configured more than once.");
            }

            ValidateKeys(profileSection, "Source", "Model");
            var sourceId = ValidateIdentifier(profileSection["Source"], "source");
            var model = profileSection["Model"];
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            if (!sources.TryGetValue(sourceId, out var source))
            {
                throw new InvalidOperationException(
                    $"Model profile '{profileId}' references unknown source '{sourceId}'.");
            }

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
            throw new InvalidOperationException("At least one model profile must be configured.");
        }

        var defaultProfileId = ValidateIdentifier(options.DefaultProfile, "profile");
        var defaultProfile = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, defaultProfileId, StringComparison.OrdinalIgnoreCase));
        if (defaultProfile is null)
        {
            throw new InvalidOperationException($"Default model profile '{defaultProfileId}' does not exist.");
        }

        return CreateCandidate(options, defaultProfile.Id, profiles);
    }

    private Candidate CreateLegacyCandidate(IConfigurationSection root, MaieuticsOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model.Name);
        var provider = string.IsNullOrWhiteSpace(options.Model.Provider) ? "OpenAI" : options.Model.Provider;
        if (!factories.TryGetValue(provider, out var factory))
        {
            throw new NotSupportedException($"The model provider '{provider}' is not registered.");
        }

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
        return CreateCandidate(options, profileId, [profile]);
    }

    private static Candidate CreateCandidate(
        MaieuticsOptions options,
        string defaultProfileId,
        IReadOnlyList<CandidateProfile> profiles)
    {
        var key = new RuntimeKey(
            NormalizeIdentifier(defaultProfileId),
            options.SystemPrompt,
            options.Agent.MaxRetainedTurns,
            options.Agent.MaxHistoryCharacters,
            options.Agent.MaxInputCharacters,
            options.Agent.MaxResponseCharacters,
            options.Agent.MaxModelIterationsPerTurn,
            options.Agent.MaxToolCallsPerTurn,
            options.Agent.MaxToolArgumentsBytes,
            options.Agent.MaxToolResultBytes,
            options.Agent.MaxToolProgressEventsPerCall,
            options.Agent.EventBufferCapacity,
            Path.GetFullPath(options.Jupyter.ConnectionFile),
            options.Jupyter.FlushInterval,
            options.Jupyter.FlushCharacters);
        return new Candidate(options, defaultProfileId, profiles, key);
    }

    private static bool HasSameConfiguration(RuntimeSnapshot snapshot, Candidate candidate)
    {
        if (snapshot.Key != candidate.Key || snapshot.Profiles.Count != candidate.Profiles.Count)
        {
            return false;
        }

        foreach (var profile in candidate.Profiles)
        {
            if (!snapshot.Profiles.TryGetValue(profile.Id, out var currentProfile) ||
                currentProfile.Key != profile.Key)
            {
                return false;
            }
        }

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
        foreach (var pair in section.AsEnumerable(makePathsRelative: true))
        {
            if (!string.IsNullOrEmpty(pair.Key))
            {
                values[$"Source:{pair.Key}"] = pair.Value;
            }
        }
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

    private static string NormalizeIdentifier(string value) => value.ToUpperInvariant();

    private static void ValidateKeys(IConfigurationSection section, params string[] allowed)
    {
        var allowedKeys = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = section.GetChildren().FirstOrDefault(child => !allowedKeys.Contains(child.Key));
        if (unknown is not null)
        {
            throw new InvalidOperationException(
                $"Configuration field '{unknown.Path}' is not valid for a model profile.");
        }
    }

    private static void ValidateBoundSource(
        IConfiguredChatClientFactory factory,
        IConfiguredChatClientSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(factory.ProviderName, source.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Provider factory '{factory.ProviderName}' returned source '{source.ProviderName}'.");
        }

        ArgumentNullException.ThrowIfNull(source.ClientGenerationKey);
    }

    private void ValidateConfigurationFileSyntax()
    {
        if (configurationFile.Path is not { } path || !File.Exists(path))
        {
            return;
        }

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
                if (!task.IsCompleted)
                {
                    continue;
                }

                _ = task.Exception;
                retiredGenerations.RemoveAt(index);
            }
        }
    }

    private RuntimeSnapshot GetCurrent() =>
        current ?? throw new ObjectDisposedException(nameof(MaieuticsRuntimeConfiguration));

    private static AgentSessionOptions CreateAgentOptions(MaieuticsOptions options) => new()
    {
        SystemPrompt = options.SystemPrompt,
        MaxRetainedTurns = options.Agent.MaxRetainedTurns,
        MaxHistoryCharacters = options.Agent.MaxHistoryCharacters,
        MaxInputCharacters = options.Agent.MaxInputCharacters,
        MaxResponseCharacters = options.Agent.MaxResponseCharacters,
        MaxModelIterationsPerTurn = options.Agent.MaxModelIterationsPerTurn,
        MaxToolCallsPerTurn = options.Agent.MaxToolCallsPerTurn,
        MaxToolArgumentsBytes = options.Agent.MaxToolArgumentsBytes,
        MaxToolResultBytes = options.Agent.MaxToolResultBytes,
        MaxToolProgressEventsPerCall = options.Agent.MaxToolProgressEventsPerCall,
        EventBufferCapacity = options.Agent.EventBufferCapacity
    };

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
            {
                throw new InvalidOperationException(
                    $"A chat client provider named '{factory.ProviderName}' is already registered.");
            }
        }

        return result;
    }

    private sealed record Candidate(
        MaieuticsOptions Options,
        string DefaultProfileId,
        IReadOnlyList<CandidateProfile> Profiles,
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
        int MaxHistoryCharacters,
        int MaxInputCharacters,
        int MaxResponseCharacters,
        int MaxModelIterationsPerTurn,
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
        RuntimeKey Key);

    private sealed record ProfileEntry(
        string Id,
        string SourceId,
        ProfileKey Key,
        AgentModelIdentity Identity,
        AgentModelCapabilities Capabilities,
        ProfileGeneration Generation);

    private sealed class RuntimeProfileLease(
        ProfileGenerationLease generationLease,
        AgentRunProfile profile) : IAgentRunProfileLease
    {
        public AgentRunProfile Profile { get; } = profile;

        public ValueTask DisposeAsync() => generationLease.DisposeAsync();
    }

    private sealed class ProfileGeneration(IChatClient client, ILogger logger)
    {
        private readonly Lock gate = new();
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int references = 1;
        private bool retired;

        internal IChatClient Client { get; } = client ?? throw new ArgumentNullException(nameof(client));

        internal ProfileGenerationLease Acquire()
        {
            lock (gate)
            {
                if (retired)
                {
                    throw new ObjectDisposedException(nameof(ProfileGeneration));
                }

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

            if (dispose)
            {
                _ = DisposeClientAsync();
            }

            return disposed.Task;
        }

        internal ValueTask ReleaseAsync()
        {
            bool dispose;
            lock (gate)
            {
                if (references <= 0)
                {
                    return ValueTask.CompletedTask;
                }

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

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref disposed, 1) == 0
                ? generation.ReleaseAsync()
                : ValueTask.CompletedTask;
    }

    private sealed record CachedDiscovery(
        DiscoveredModelGroup Result,
        DateTime CachedAt);
}