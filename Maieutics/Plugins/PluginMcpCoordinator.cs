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
///     registration records) — run discovery again. A superseded-but-unapplied revision's forced
///     set is inherited by its successor, so a concurrent plain publish cannot swallow a pending
///     forced re-run. Removed registrations drop their contributions, and a registration whose
///     discovery never succeeded stays "new" so the next revision retries it. Adjuster drops are
///     view-only: the persisted contributions stay raw, so an adjuster that reverts (or re-judges)
///     a drop restores the server on the next revision without re-running discovery.
/// </summary>
internal sealed class PluginMcpCoordinator(
    PluginMcpDiscovery discovery,
    PluginMcpGenerationFactory generationFactory,
    ILogger logger,
    McpAdjustmentChain? adjustments = null) : IAsyncDisposable
{
    private readonly PluginMcpDiscovery _discovery =
        discovery ?? throw new ArgumentNullException(nameof(discovery));

    private readonly Lock _gate = new();

    private readonly PluginMcpGenerationFactory _generationFactory =
        generationFactory ?? throw new ArgumentNullException(nameof(generationFactory));

    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly List<Task> _retirementObservers = [];

    private readonly Channel<byte> _revisionSignals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    private readonly Dictionary<string, string> _adjustmentFingerprints = new(StringComparer.Ordinal);

    /// <summary>The shared empty forced set for plain differential publishes.</summary>
    private static readonly IReadOnlySet<string> NoForcedPlugins = new HashSet<string>(StringComparer.Ordinal);

    private IReadOnlyDictionary<PluginRegistration, IReadOnlyList<McpServerDefinition>> _contributions =
        new Dictionary<PluginRegistration, IReadOnlyList<McpServerDefinition>>();

    private Task? _disposal;
    private int _disposeState;

    private IReadOnlyDictionary<string, McpServerGeneration> _generations =
        new Dictionary<string, McpServerGeneration>(StringComparer.Ordinal);

    private RegistryRevision? _latestRevision;
    private long _nextRevision;
    private Task _refreshLoop = Task.CompletedTask;
    private int _startState;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (Interlocked.Exchange(ref _startState, 1) != 0) return;

        _refreshLoop = RunRefreshLoopAsync();
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (Volatile.Read(ref _startState) == 0)
            throw new InvalidOperationException("The plugin MCP coordinator has not been started.");

        var normalized = registrations
            .Distinct()
            .OrderBy(static registration => registration.PluginId, StringComparer.Ordinal)
            .ThenBy(static registration => registration.ExportName, StringComparer.Ordinal)
            .ToImmutableArray();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegistryRevision revision;
        RegistryRevision? superseded;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            superseded = _latestRevision;
            // A superseded revision that never applied keeps its forced plugins owed:
            // the successor inherits them, or a plain publish would differential-skip
            // the forced registrations back to their stale contributions and silently
            // drop the "re-run this plugin" semantics (the trigger/reload contract).
            // A revision that did apply has no debt — its fresh results are already
            // the contributions the successor seeds from.
            var effectiveForced = forcedPlugins;
            if (superseded is not null &&
                !superseded.Completion.Task.IsCompleted &&
                superseded.ForcedPlugins.Count > 0)
            {
                var inherited = new HashSet<string>(forcedPlugins, StringComparer.Ordinal);
                inherited.UnionWith(superseded.ForcedPlugins);
                effectiveForced = inherited;
            }

            revision = new RegistryRevision(
                Interlocked.Increment(ref _nextRevision),
                normalized,
                effectiveForced,
                completion);
            _latestRevision = revision;
        }

        superseded?.Completion.TrySetResult(false);
        if (!_revisionSignals.Writer.TryWrite(0) && Volatile.Read(ref _disposeState) != 0)
            completion.TrySetResult(false);

        return revision;
    }

    /// <summary>Internal test observability: completes when the most recently enqueued
    /// registry revision has been applied (or superseded by a later one). Capture it right
    /// after publishing to await that revision's effect instead of polling the published
    /// surface; a revision that already settled returns a completed task.</summary>
    internal Task RegistryReconciled
    {
        get
        {
            lock (_gate)
            {
                return _latestRevision?.Completion.Task ?? Task.CompletedTask;
            }
        }
    }

    internal IReadOnlyList<McpServerGeneration.McpServerLease> AcquireLeases()
    {
        McpServerGeneration[] snapshot;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeState) != 0) return [];

            snapshot = _generations.Values
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
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeState) != 0) return [];

            return _generations.Values
                .OrderBy(static generation => generation.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_gate)
        {
            if (_disposal is null)
            {
                Volatile.Write(ref _disposeState, 1);
                _disposal = DisposeCoreAsync();
            }

            task = _disposal;
        }

        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        _revisionSignals.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _refreshLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }

        McpServerGeneration[] active;
        RegistryRevision? pending;
        lock (_gate)
        {
            active = _generations.Values.ToArray();
            _generations = new Dictionary<string, McpServerGeneration>(StringComparer.Ordinal);
            _contributions = new Dictionary<PluginRegistration, IReadOnlyList<McpServerDefinition>>();
            pending = _latestRevision;
            _latestRevision = null;
        }

        pending?.Completion.TrySetResult(false);
        foreach (var generation in active) TrackRetirement(generation);

        Task[] retirements;
        lock (_gate)
        {
            retirements = _retirementObservers.ToArray();
        }

        await Task.WhenAll(retirements).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task RunRefreshLoopAsync()
    {
        try
        {
            await foreach (var signal in _revisionSignals.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (signal != 0) continue;
                while (_revisionSignals.Reader.TryRead(out _))
                {
                }

                RegistryRevision? revision;
                lock (_gate)
                {
                    revision = _latestRevision;
                }

                if (revision is null || revision.Completion.Task.IsCompleted) continue;

                var applied = false;
                try
                {
                    applied = await ReconcileRevisionAsync(revision, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
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
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> ReconcileRevisionAsync(
        RegistryRevision revision,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<PluginRegistration, IReadOnlyList<McpServerDefinition>> previousContributions;
        lock (_gate)
        {
            previousContributions = _contributions;
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
                var result = await _discovery(registration, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    candidateContributions[registration] = result.Definitions.ToArray();
                    continue;
                }

                _logger.LogWarning(
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
                _logger.LogWarning(
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
        // downstream adjusters and the merge see (ADR 0034). Drops are a property
        // of the composed view, never of the discovery results: the filtered set
        // feeds only this revision's merge and generations, while the persisted
        // contributions stay raw — so a later revision whose adjusters no longer
        // drop the server restores it without re-running discovery.
        var mergeContributions = candidateContributions;
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
                    mergeContributions = candidateContributions.ToDictionary(
                        static pair => pair.Key,
                        pair => (IReadOnlyList<McpServerDefinition>)pair.Value
                            .Where(definition => !dropped.Contains(definition.Id))
                            .ToArray());
                }
            }
        }

        if (!TryMergeDefinitions(mergeContributions.Values, out var definitions)) return false;

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

            _logger.LogWarning(
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
        lock (_gate)
        {
            previous = _generations;
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

                var generation = await _generationFactory(definition, cancellationToken).ConfigureAwait(false);
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
        lock (_gate)
        {
            if (!ReferenceEquals(_latestRevision, revision) || Volatile.Read(ref _disposeState) != 0)
            {
                retired = [];
            }
            else
            {
                var retained = next.Values.ToHashSet(ReferenceEqualityComparer.Instance);
                retired = previous.Values.Where(generation => !retained.Contains(generation)).ToArray();
                _generations = next;
                _contributions = candidateContributions;
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
                    _adjustmentFingerprints[generation.Id] = fingerprint;
                    continue;
                }

                if (_adjustmentFingerprints.TryGetValue(generation.Id, out var stored) &&
                    string.Equals(stored, fingerprint, StringComparison.Ordinal))
                    continue;
                _adjustmentFingerprints[generation.Id] = fingerprint;
                pendingRefresh.Add(generation);
            }

            foreach (var generation in pendingRefresh)
            {
                _logger.LogInformation(
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
        lock (_gate)
        {
            return Volatile.Read(ref _disposeState) == 0 && ReferenceEquals(_latestRevision, revision);
        }
    }

    private void TrackRetirement(McpServerGeneration generation)
    {
        var observer = ObserveRetirementAsync(generation);
        lock (_gate)
        {
            _retirementObservers.RemoveAll(static task => task.IsCompleted);
            _retirementObservers.Add(observer);
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
            _logger.LogError(
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
