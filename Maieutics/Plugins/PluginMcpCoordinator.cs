using System.Collections.Immutable;
using System.Threading.Channels;
using Maieutics.Mcp;
using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins;

internal delegate Task<PluginMcpDiscoveryResult> PluginMcpDiscovery(
    PluginRegistration registration,
    CancellationToken cancellationToken);

internal delegate Task<McpServerGeneration> PluginMcpGenerationFactory(
    McpServerDefinition definition,
    CancellationToken cancellationToken);

internal sealed record PluginMcpDiscoveryResult(
    bool IsSuccess,
    IReadOnlyList<McpServerDefinition> Definitions,
    string? Failure = null)
{
    internal static PluginMcpDiscoveryResult Success(IReadOnlyList<McpServerDefinition> definitions)
    {
        return new PluginMcpDiscoveryResult(true, definitions);
    }

    internal static PluginMcpDiscoveryResult Failed(string failure)
    {
        return new PluginMcpDiscoveryResult(false, [], failure);
    }
}

/// <summary>
///     Serializes plugin MCP discovery revisions and owns the atomically published generation
///     snapshot. Each active registration retains its last successful contribution when a later
///     discovery attempt fails.
/// </summary>
internal sealed class PluginMcpCoordinator(
    PluginMcpDiscovery discovery,
    PluginMcpGenerationFactory generationFactory,
    ILogger logger) : IAsyncDisposable
{
    private readonly PluginMcpDiscovery discovery =
        discovery ?? throw new ArgumentNullException(nameof(discovery));

    private readonly Lock gate = new();

    private readonly PluginMcpGenerationFactory generationFactory =
        generationFactory ?? throw new ArgumentNullException(nameof(generationFactory));

    private readonly CancellationTokenSource lifetime = new();
    private readonly ILogger logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly List<Task> retirementObservers = [];

    private readonly Channel<byte> revisionSignals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    private IReadOnlyDictionary<PluginRegistration, IReadOnlyList<McpServerDefinition>> contributions =
        new Dictionary<PluginRegistration, IReadOnlyList<McpServerDefinition>>();

    private Task? disposal;
    private int disposeState;
    private IReadOnlyDictionary<string, McpServerGeneration> generations =
        new Dictionary<string, McpServerGeneration>(StringComparer.Ordinal);

    private RegistryRevision? latestRevision;
    private long nextRevision;
    private Task refreshLoop = Task.CompletedTask;
    private int startState;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        if (Interlocked.Exchange(ref startState, 1) != 0) return;

        refreshLoop = RunRefreshLoopAsync();
    }

    internal void PublishRegistry(IReadOnlyList<PluginRegistration> registrations)
    {
        EnqueueRegistry(registrations);
    }

    internal Task<bool> PublishRegistryAsync(IReadOnlyList<PluginRegistration> registrations)
    {
        return EnqueueRegistry(registrations).Completion.Task;
    }

    private RegistryRevision EnqueueRegistry(IReadOnlyList<PluginRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        if (Volatile.Read(ref startState) == 0)
            throw new InvalidOperationException("The plugin MCP coordinator has not been started.");

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = new RegistryRevision(
            Interlocked.Increment(ref nextRevision),
            registrations
                .Distinct()
                .OrderBy(static registration => registration.PluginId, StringComparer.Ordinal)
                .ThenBy(static registration => registration.ExportName, StringComparer.Ordinal)
                .ToImmutableArray(),
            completion);
        RegistryRevision? superseded;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
            superseded = latestRevision;
            latestRevision = revision;
        }

        superseded?.Completion.TrySetResult(false);
        if (!revisionSignals.Writer.TryWrite(0) && Volatile.Read(ref disposeState) != 0)
            completion.TrySetResult(false);

        return revision;
    }

    internal IReadOnlyList<McpServerGeneration.McpServerLease> AcquireLeases()
    {
        McpServerGeneration[] snapshot;
        lock (gate)
        {
            if (Volatile.Read(ref disposeState) != 0) return [];

            snapshot = generations.Values
                .OrderBy(static generation => generation.Id, StringComparer.Ordinal)
                .ToArray();
        }

        var leases = new List<McpServerGeneration.McpServerLease>(snapshot.Length);
        foreach (var generation in snapshot)
            if (generation.TryAcquire() is { } lease)
                leases.Add(lease);

        return leases;
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (gate)
        {
            if (disposal is null)
            {
                Volatile.Write(ref disposeState, 1);
                disposal = DisposeCoreAsync();
            }

            task = disposal;
        }

        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        revisionSignals.Writer.TryComplete();
        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await refreshLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }

        McpServerGeneration[] active;
        RegistryRevision? pending;
        lock (gate)
        {
            active = generations.Values.ToArray();
            generations = new Dictionary<string, McpServerGeneration>(StringComparer.Ordinal);
            contributions = new Dictionary<PluginRegistration, IReadOnlyList<McpServerDefinition>>();
            pending = latestRevision;
            latestRevision = null;
        }

        pending?.Completion.TrySetResult(false);
        foreach (var generation in active) TrackRetirement(generation);

        Task[] retirements;
        lock (gate)
        {
            retirements = retirementObservers.ToArray();
        }

        await Task.WhenAll(retirements).ConfigureAwait(false);
        lifetime.Dispose();
    }

    private async Task RunRefreshLoopAsync()
    {
        try
        {
            await foreach (var signal in revisionSignals.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (signal != 0) continue;
                while (revisionSignals.Reader.TryRead(out _))
                {
                }

                RegistryRevision? revision;
                lock (gate)
                {
                    revision = latestRevision;
                }

                if (revision is null || revision.Completion.Task.IsCompleted) continue;

                var applied = false;
                try
                {
                    applied = await ReconcileRevisionAsync(revision, lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Plugin MCP registry revision {Revision} could not be applied; the previous snapshot remains active.",
                        revision.Number);
                }
                finally
                {
                    revision.Completion.TrySetResult(applied);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> ReconcileRevisionAsync(
        RegistryRevision revision,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<PluginRegistration, IReadOnlyList<McpServerDefinition>> previousContributions;
        lock (gate)
        {
            previousContributions = contributions;
        }

        var active = revision.Registrations.ToHashSet();
        var candidateContributions = previousContributions
            .Where(pair => active.Contains(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        foreach (var registration in revision.Registrations)
        {
            if (!IsCurrent(revision)) return false;

            try
            {
                var result = await discovery(registration, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    candidateContributions[registration] = result.Definitions.ToArray();
                    continue;
                }

                logger.LogWarning(
                    "Plugin '{PluginId}' export '{ExportName}' MCP discovery failed ({Failure}); its previous contribution remains active.",
                    registration.PluginId,
                    registration.ExportName,
                    result.Failure ?? "unknown_failure");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Plugin '{PluginId}' export '{ExportName}' MCP discovery raised an unexpected failure; its previous contribution remains active.",
                    registration.PluginId,
                    registration.ExportName);
            }
        }

        if (!IsCurrent(revision)) return false;
        if (!TryMergeDefinitions(candidateContributions.Values, out var definitions)) return false;

        return await ReconcileGenerationsAsync(
                revision,
                candidateContributions,
                definitions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private bool TryMergeDefinitions(
        IEnumerable<IReadOnlyList<McpServerDefinition>> contributionSets,
        out IReadOnlyList<McpServerDefinition> definitions)
    {
        var merged = new Dictionary<string, McpServerDefinition>(StringComparer.Ordinal);
        foreach (var definition in contributionSets.SelectMany(static values => values)
                     .OrderBy(static definition => definition.Id, StringComparer.Ordinal))
        {
            if (!merged.TryGetValue(definition.Id, out var existing))
            {
                merged.Add(definition.Id, definition);
                continue;
            }

            if (string.Equals(existing.GenerationKey, definition.GenerationKey, StringComparison.Ordinal)) continue;

            logger.LogWarning(
                "Plugin MCP discovery produced conflicting definitions for server '{ServerId}'; the previous snapshot remains active.",
                definition.Id);
            definitions = [];
            return false;
        }

        definitions = merged.Values.ToArray();
        return true;
    }

    private async Task<bool> ReconcileGenerationsAsync(
        RegistryRevision revision,
        IReadOnlyDictionary<PluginRegistration, IReadOnlyList<McpServerDefinition>> candidateContributions,
        IReadOnlyList<McpServerDefinition> definitions,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, McpServerGeneration> previous;
        lock (gate)
        {
            previous = generations;
        }

        var next = new Dictionary<string, McpServerGeneration>(StringComparer.Ordinal);
        var created = new List<McpServerGeneration>();
        try
        {
            foreach (var definition in definitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previous.TryGetValue(definition.Id, out var existing) &&
                    string.Equals(existing.GenerationKey, definition.GenerationKey, StringComparison.Ordinal))
                {
                    next.Add(definition.Id, existing);
                    continue;
                }

                var generation = await generationFactory(definition, cancellationToken).ConfigureAwait(false);
                created.Add(generation);
                next.Add(definition.Id, generation);
            }
        }
        catch
        {
            await RetireCreatedAsync(created).ConfigureAwait(false);
            throw;
        }

        if (!IsCurrent(revision))
        {
            await RetireCreatedAsync(created).ConfigureAwait(false);
            return false;
        }

        McpServerGeneration[] retired;
        var published = false;
        lock (gate)
        {
            if (!ReferenceEquals(latestRevision, revision) || Volatile.Read(ref disposeState) != 0)
            {
                retired = [];
            }
            else
            {
                var retained = next.Values.ToHashSet(ReferenceEqualityComparer.Instance);
                retired = previous.Values.Where(generation => !retained.Contains(generation)).ToArray();
                generations = next;
                contributions = candidateContributions;
                published = true;
            }
        }

        if (!published)
        {
            await RetireCreatedAsync(created).ConfigureAwait(false);
            return false;
        }

        foreach (var generation in retired) TrackRetirement(generation);
        return true;
    }

    private bool IsCurrent(RegistryRevision revision)
    {
        lock (gate)
        {
            return Volatile.Read(ref disposeState) == 0 && ReferenceEquals(latestRevision, revision);
        }
    }

    private void TrackRetirement(McpServerGeneration generation)
    {
        var observer = ObserveRetirementAsync(generation);
        lock (gate)
        {
            retirementObservers.RemoveAll(static task => task.IsCompleted);
            retirementObservers.Add(observer);
        }
    }

    private async Task ObserveRetirementAsync(McpServerGeneration generation)
    {
        try
        {
            await generation.Retire().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Plugin-discovered MCP server '{ServerId}' failed to retire.",
                generation.Id);
        }
    }

    private static Task RetireCreatedAsync(IReadOnlyList<McpServerGeneration> generations)
    {
        return Task.WhenAll(generations.Select(static generation => generation.Retire()));
    }

    private sealed record RegistryRevision(
        long Number,
        ImmutableArray<PluginRegistration> Registrations,
        TaskCompletionSource<bool> Completion);
}
