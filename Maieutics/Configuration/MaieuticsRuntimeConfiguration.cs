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
        var generation = new ProviderGeneration(candidate.Factory.Create(candidate.Options), logger);
        current = new RuntimeSnapshot(
            1,
            candidate.Options,
            candidate.ProviderKey,
            candidate.RuntimeKey,
            generation);
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
            var generationLease = snapshot.Generation.Acquire();
            return new RuntimeProfileLease(
                generationLease,
                new AgentRunProfile(snapshot.Generation.Client, CreateAgentOptions(snapshot.Options)));
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

        ProviderGeneration generation;
        Task[] retired;
        lock (gate)
        {
            generation = GetCurrent().Generation;
            current = null;
            retired = retiredGenerations.ToArray();
        }

        var currentRetirement = generation.Retire();
        await Task.WhenAll(retired.Append(currentRetirement)).ConfigureAwait(false);
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
        if (configurationFile.Required &&
            configurationFile.Path is { } requiredPath &&
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
            if (Equals(previous.RuntimeKey, candidate.RuntimeKey))
            {
                return;
            }
        }

        ProviderGeneration? replacement = null;
        if (!Equals(previous.ProviderKey, candidate.ProviderKey))
        {
            replacement = new ProviderGeneration(candidate.Factory.Create(candidate.Options), logger);
        }

        lock (gate)
        {
            current = new RuntimeSnapshot(
                checked(previous.Version + 1),
                candidate.Options,
                candidate.ProviderKey,
                candidate.RuntimeKey,
                replacement ?? previous.Generation);
        }

        if (replacement is not null)
        {
            lock (gate)
            {
                retiredGenerations.Add(previous.Generation.Retire());
            }
        }

        logger.LogInformation("Applied Maieutics configuration version {ConfigurationVersion}.", Version);
    }

    private Candidate CreateCandidate()
    {
        var options = new MaieuticsOptions();
        configuration.GetSection(MaieuticsOptions.SectionName).Bind(options);
        options.Validate();

        if (!factories.TryGetValue(options.Model.Provider, out var factory))
        {
            throw new NotSupportedException($"The model provider '{options.Model.Provider}' is not registered.");
        }

        var key = new ProviderKey(factory.ProviderName, factory.GetConfigurationKey(options));
        return new Candidate(options, factory, key, CreateRuntimeKey(options, key));
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

    private static RuntimeKey CreateRuntimeKey(MaieuticsOptions options, ProviderKey providerKey) => new(
        providerKey,
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
        IConfiguredChatClientFactory Factory,
        ProviderKey ProviderKey,
        RuntimeKey RuntimeKey);

    private sealed record ProviderKey(string ProviderName, object Configuration);

    private sealed record RuntimeKey(
        ProviderKey ProviderKey,
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
        ProviderKey ProviderKey,
        RuntimeKey RuntimeKey,
        ProviderGeneration Generation);

    private sealed class RuntimeProfileLease(
        ProviderGenerationLease generationLease,
        AgentRunProfile profile) : IAgentRunProfileLease
    {
        public AgentRunProfile Profile { get; } = profile;

        public ValueTask DisposeAsync() => generationLease.DisposeAsync();
    }

    private sealed class ProviderGeneration(IChatClient client, ILogger logger)
    {
        private readonly Lock gate = new();
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int references = 1;
        private bool retired;

        internal IChatClient Client { get; } = client ?? throw new ArgumentNullException(nameof(client));

        internal ProviderGenerationLease Acquire()
        {
            lock (gate)
            {
                if (retired)
                {
                    throw new ObjectDisposedException(nameof(ProviderGeneration));
                }

                references = checked(references + 1);
                return new ProviderGenerationLease(this);
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
            var dispose = false;
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

    private sealed class ProviderGenerationLease(ProviderGeneration generation) : IAsyncDisposable
    {
        private int disposed;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref disposed, 1) == 0
                ? generation.ReleaseAsync()
                : ValueTask.CompletedTask;
    }
}