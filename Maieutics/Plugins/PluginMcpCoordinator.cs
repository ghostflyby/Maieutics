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
///     discovery attempt fails. Revisions are differential: a registration that already carries a
///     contribution and stays in the published set reuses it without re-running discovery (no
///     worker invoke, no manifest re-read); only new registrations — or plugins the publish forced
///     (a trigger or a worker reload, whose exports may return different results over unchanged
///     registration records) — run discovery again. Removed registrations simply drop their
///     contributions, and a registration whose discovery never succeeded stays "new" so the next
///     revision retries it.
/// </summary>
internal sealed class PluginMcpCoordinator(
    PluginMcpDiscovery discovery,
    PluginMcpGenerationFactory generationFactory,
    ILogger logger,
    McpAdjustmentChain? adjustments = null) : IAsyncDisposable
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

    private readonly Dictionary<string, string> adjustmentFingerprints = new(StringComparer.Ordinal);

    /// <summary>The shared empty forced set for plain differential publishes.</summary>
    private static readonly IReadOnlySet<string> NoForcedPlugins = new HashSet<string>(StringComparer.Ordinal);
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

    /// <summary>Plain differential publish: registrations new to the published set run
    /// discovery, unchanged ones reuse their contributions, removed ones drop them.</summary>
    internal void PublishRegistry(IReadOnlyList<PluginRegistration> registrations)
    {
        EnqueueRegistry(registrations, NoForcedPlugins);
    }

    /// <summary>Forced differential publish: same registration diff, plus re-discovery of
    /// every registration owned by <paramref name="forcedPlugins"/> — the trigger and
    /// worker-reload form (the plugin's exports must re-run even though its registration
    /// records are unchanged).</summary>
    internal void PublishRegistry(
        IReadOnlyList<PluginRegistration> registrations,
        IReadOnlySet<string> forcedPlugins)
    {
        EnqueueRegistry(registrations, forcedPlugins);
    }

    internal Task<bool> PublishRegistryAsync(IReadOnlyList<PluginRegistration> registrations)
    {
        return EnqueueRegistry(registrations, NoForcedPlugins).Completion.Task;
    }

    internal Task<bool> PublishRegistryAsync(
        IReadOnlyList<PluginRegistration> registrations,
        IReadOnlySet<string> forcedPlugins)
    {
        return EnqueueRegistry(registrations, forcedPlugins).Completion.Task;
    }

    private RegistryRevision EnqueueRegistry(
        IReadOnlyList<PluginRegistration> registrations,
        IReadOnlySet<string> forcedPlugins)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(forcedPlugins);
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
            forcedPlugins,
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

    /// <summary>Status snapshot of the published MCP servers, ordered by id: reconnecting
    /// generations report the reconnecting state with an empty tool list. Disposed
    /// coordinators report nothing.</summary>
    internal IReadOnlyList<MaieuticsMcpServerInfo> GetServerInfos()
    {
        var snapshot = SnapshotGenerations();
        return snapshot.Select(static generation => generation.GetInfo()).ToArray();
    }

    /// <summary>The published generations for resource access (ADR 0026 decision 3),
    /// ordered by id. A retired generation keeps answering reads with the typed
    /// unavailable error until the caller releases it, so a racy snapshot is safe.</summary>
    internal IReadOnlyList<McpServerGeneration> SnapshotGenerations()
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposeState) != 0) return [];

            return generations.Values
                .OrderBy(static generation => generation.Id, StringComparer.Ordinal)
                .ToArray();
        }
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

            // Differential skip: a registration the last published revision already
            // resolved keeps its contribution unless this publish forced its plugin.
            // Discovery (a worker invoke or a manifest re-read) runs only for new,
            // forced, or never-yet-succeeded registrations.
            if (!revision.ForcedPlugins.Contains(registration.PluginId) &&
                candidateContributions.TryGetValue(registration, out _))
                continue;

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

        // The adjustment chain folds the composed view before the merge: adjusters
        // run in dependency-topological order, each seeing only its declared
        // dependencies' servers, and drops remove servers from the view that
        // downstream adjusters and the merge see (ADR 0034).
        if (adjustments is not null)
        {
            var view = candidateContributions
                .SelectMany(pair => pair.Value.Select(definition =>
                    (Owner: pair.Key.PluginId, ServerId: definition.Id, definition.Transport)))
                .ToList();
            if (view.Count > 0)
            {
                var dropped = await adjustments.FoldCompositionAsync(view, cancellationToken)
                    .ConfigureAwait(false);
                if (dropped.Count > 0)
                {
                    foreach (var key in candidateContributions.Keys.ToList())
                    {
                        if (candidateContributions[key].Any(definition => dropped.Contains(definition.Id)))
                        {
                            candidateContributions[key] = candidateContributions[key]
                                .Where(definition => !dropped.Contains(definition.Id))
                                .ToArray();
                        }
                    }
                }
            }
        }

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

        // A retained generation whose adjuster chain changed re-projects its tool
        // surface over the live connection; freshly created generations already
        // projected with the current chain (ADR 0034).
        if (adjustments is not null)
        {
            var createdSet = created.ToHashSet(ReferenceEqualityComparer.Instance);
            var pendingRefresh = new List<McpServerGeneration>();
            foreach (var generation in next.Values)
            {
                var fingerprint = adjustments.FingerprintFor(generation.Id);
                if (createdSet.Contains(generation))
                {
                    adjustmentFingerprints[generation.Id] = fingerprint;
                    continue;
                }

                if (adjustmentFingerprints.TryGetValue(generation.Id, out var stored) &&
                    string.Equals(stored, fingerprint, StringComparison.Ordinal))
                    continue;
                adjustmentFingerprints[generation.Id] = fingerprint;
                pendingRefresh.Add(generation);
            }

            foreach (var generation in pendingRefresh)
            {
                logger.LogInformation(
                    "The adjustment chain changed for server '{ServerId}'; its tool surface will be re-projected.",
                    generation.Id);
                generation.RequestToolRefresh();
            }
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
        IReadOnlySet<string> ForcedPlugins,
        TaskCompletionSource<bool> Completion);
}
