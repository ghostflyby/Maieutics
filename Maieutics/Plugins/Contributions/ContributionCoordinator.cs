using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins.Contributions;

/// <summary>How a contribution kind receives registry deltas. The two shapes are set
/// per kind and must not be swapped: a RegistryWide kind derives the active set from
/// the delivered snapshot, so delivering it a subset would erase every other plugin's
/// sticky contribution; a PerPlugin kind receives targeted single-plugin passes.</summary>
internal enum ContributionDeliveryShape
{
    RegistryWide,

    PerPlugin
}

/// <summary>One registry-frame-sized input, assembled by the host under its own gate
/// and handed over as values: the full registration snapshot (both shapes), the forced
/// plugin set (reload-epoch drains ∪ the trigger), and the PerPlugin targets (export-set
/// diff ∪ forced ∪ kernel retry set, deduplicated). The coordinator never reads host
/// state and holds no callbacks — every value it consumes is in this record.</summary>
internal sealed record ContributionFrameInput(
    IReadOnlyList<PluginRegistration> Registrations,
    IReadOnlySet<string> ForcedPlugins,
    IReadOnlyList<string> PerPluginTargets)
{
    /// <summary>The shared empty forced set for plain (unforced) deliveries.</summary>
    public static IReadOnlySet<string> NoForcedPlugins { get; } =
        new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>One contribution kind's delivery endpoint: what the coordinator dispatches
/// to. A 期 the two adapters (MCP RegistryWide, Skills PerPlugin) implement this; the
/// B 期 kind contract absorbs the members that today sit directly on the adapter.</summary>
internal interface IContributionDelivery
{
    /// <summary>The canonical kind name — the kernel's manifest kind constant today
    /// ("McpDiscover" / "Skills"; the manifest kind catalog and the wire registration
    /// catalog coincidentally share the spelling, but they are two directories).</summary>
    string KindName { get; }

    ContributionDeliveryShape Shape { get; }

    /// <summary>The wire registration name this kind's export-set diff and computed
    /// invokes route through (formerly hardcoded at the frame diff, the discovery
    /// invoke, and the capability dispatch).</summary>
    string ExtensionPointName { get; }

    /// <summary>Whether the plugin currently holds any surface of this kind. Registry
    /// face checks read host state (registrations, descriptors) in the host's gate and
    /// consult this for the held-slot clause.</summary>
    bool HoldsFace(string pluginId);

    /// <summary>RegistryWide delivery: the frame's snapshot is the full active set —
    /// the adapter filters its own extension point and republishes. Called for every
    /// frame; a changed export set is not required. <paramref name="guardDisposed"/>
    /// preserves the host's historical asymmetry: the plain branch of a registry frame
    /// published unguarded (a concurrent generation switch surfaces), while seed,
    /// approval, trigger, and forced publishes skip a disposed coordinator as a typed
    /// non-event.</summary>
    void PublishFrame(ContributionFrameInput frame, bool guardDisposed);

    /// <summary>PerPlugin delivery: one targeted reconcile pass for the plugin. The
    /// implementation reads its own inputs (generation token, host state) — the
    /// coordinator only schedules. Exceptions follow the pass contract: expected
    /// failures are logged inside, cancellation propagates.</summary>
    Task ReconcileAsync(string pluginId, CancellationToken cancellationToken);
}

/// <summary>The kernel-side event differential engine shared by every contribution
/// kind (plugin-contribution framework §4.1). One instance per host manager; it owns
/// the per-plugin single-flight drain (the former skills reconcile flights, now keyed
/// by kind and plugin), the kernel retry set (never-succeeded-stays-new, for kinds
/// that use it), the reload-epoch forced set, and the export-set diff — each a 1:1
/// move of the former skills-side machinery, generalized across kinds.
/// <para>Lock boundaries are the host's, unchanged: state-mutating members
/// (<see cref="MarkReloadForce"/>, <see cref="DrainReloadForces"/>, <see cref="Reset"/>,
/// <see cref="SetRetryPending"/>, <see cref="RetrySnapshot"/>) and the export-set
/// helpers are called under the host manager's gate; the dispatch members
/// (<see cref="OnRegistryFrame"/>, <see cref="Seed"/>, <see cref="PublishRegistryWide"/>,
/// <see cref="ReconcilePlugins"/>) run outside it exactly where their predecessor
/// statements ran. The flight gate is never held across an await.</para></summary>
internal sealed class ContributionCoordinator
{
    private readonly ILogger logger;
    private readonly Lock flightGate = new();
    private readonly Dictionary<(string KindName, string PluginId), ReconcileFlight> flights =
        new(FlightKeyComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> retryPending = new(StringComparer.Ordinal);

    /// <summary>Reload-epoch baselines and the deferred forced rediscoveries (the
    /// former <c>hostReloadEpochs</c> / <c>pendingReloadForces</c>). Mutated only
    /// under the host gate.</summary>
    private readonly Dictionary<string, int> hostReloadEpochs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> pendingReloadForces = new(StringComparer.Ordinal);
    private IReadOnlyList<IContributionDelivery> deliveries = [];

    public ContributionCoordinator(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>One-time wiring of the delivery endpoints, after the coordinator and
    /// the adapters are constructed (the adapters need the coordinator for their pass
    /// bookkeeping, so the list lands after both exist). Not a runtime registry:
    /// attaches exactly once, before any event flows.</summary>
    public void Attach(IReadOnlyList<IContributionDelivery> deliveryList)
    {
        ArgumentNullException.ThrowIfNull(deliveryList);
        if (deliveries.Count > 0)
            throw new InvalidOperationException("The contribution coordinator's deliveries are already attached.");
        deliveries = deliveryList;
    }

    /// <summary>Registry-frame dispatch: RegistryWide kinds receive the full snapshot
    /// (no host-side export-set pre-filter — the registration-level diff happens inside
    /// the consumer engine), then PerPlugin targets schedule their passes. Order mirrors
    /// the former frame handler: publish first, then scheduling.</summary>
    public void OnRegistryFrame(ContributionFrameInput frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        PublishToRegistryWide(frame, guardDisposed: false);
        ScheduleTargets(frame.PerPluginTargets);
    }

    /// <summary>Start seeding: the initial snapshot goes to RegistryWide kinds, the
    /// seed set to the PerPlugin kinds — the former start sequence (republish, then
    /// progressive per-plugin seeding) in one input.</summary>
    public void Seed(ContributionFrameInput frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        PublishToRegistryWide(frame, guardDisposed: true);
        ScheduleTargets(frame.PerPluginTargets);
    }

    /// <summary>The RegistryWide half of the approval-transition and trigger events,
    /// for callers that interleave actions (the adjustment-snapshot update) between the
    /// two halves exactly where the former code did. Full snapshot, frame-level, always.</summary>
    public void PublishRegistryWide(ContributionFrameInput frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        PublishToRegistryWide(frame, guardDisposed: true);
    }

    /// <summary>Schedules one targeted pass per listed plugin for the named PerPlugin
    /// kind (the approval-transition and trigger halves; passes coalesce per plugin).</summary>
    public void ReconcilePlugins(string kindName, IReadOnlyList<string> pluginIds)
    {
        ArgumentNullException.ThrowIfNull(pluginIds);
        foreach (var delivery in deliveries)
        {
            if (delivery.Shape != ContributionDeliveryShape.PerPlugin ||
                !string.Equals(delivery.KindName, kindName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var pluginId in pluginIds)
                Schedule(delivery, kindName, pluginId);
        }
    }

    /// <summary>Defers one plugin's forced rediscovery until a registry frame proves
    /// the reload completed (called under the host gate, before the reload frames ship,
    /// inside the reconcile serialization). A second mark before any frame raises the
    /// requirement, so back-to-back reloads force once, after the newest text is live.</summary>
    public void MarkReloadForce(string pluginId)
    {
        pendingReloadForces[pluginId] = Math.Max(
            pendingReloadForces.GetValueOrDefault(pluginId) + 1,
            hostReloadEpochs.GetValueOrDefault(pluginId) + 1);
    }

    /// <summary>Drains the reload forces a registry frame satisfies and refreshes the
    /// epoch baselines (called under the host gate). A frame reporting no epochs — a
    /// host that predates the field — drains every mark: the next frame is the best
    /// completion signal it can offer. An epoch that went backwards means a fresh host
    /// process (its epochs restart at zero) whose workers are all fresh, so the mark
    /// drains as well. Returns the drained ids.</summary>
    public List<string> DrainReloadForces(IReadOnlyDictionary<string, int>? epochs)
    {
        if (epochs is null)
        {
            var legacyDrained = new List<string>(pendingReloadForces.Keys);
            pendingReloadForces.Clear();
            return legacyDrained;
        }

        var drained = new List<string>();
        foreach (var (pluginId, required) in pendingReloadForces)
        {
            var observed = epochs.GetValueOrDefault(pluginId);
            if (observed >= required || observed < hostReloadEpochs.GetValueOrDefault(pluginId))
                drained.Add(pluginId);
        }

        foreach (var pluginId in drained) pendingReloadForces.Remove(pluginId);
        foreach (var (pluginId, epoch) in epochs) hostReloadEpochs[pluginId] = epoch;
        return drained;
    }

    /// <summary>Generational reset (host restart): the epoch bookkeeping and the retry
    /// sets belong to one host generation. Flight state survives, exactly like the
    /// former field clears (in-flight passes die with their generation token).</summary>
    public void Reset()
    {
        hostReloadEpochs.Clear();
        pendingReloadForces.Clear();
        retryPending.Clear();
    }

    /// <summary>Records one pass outcome for the kernel retry set (called by the
    /// delivery under the host gate): a pass that left at least one source failed is
    /// retried by the next registry frame; the first fully successful pass clears the
    /// flag.</summary>
    public void SetRetryPending(string kindName, string pluginId, bool pending)
    {
        if (!retryPending.TryGetValue(kindName, out var set))
            retryPending[kindName] = set = new HashSet<string>(StringComparer.Ordinal);
        if (pending) set.Add(pluginId);
        else set.Remove(pluginId);
    }

    /// <summary>A snapshot of the plugins awaiting a kernel retry for the kind (called
    /// under the host gate, where the former retry set fed the frame union).</summary>
    public IReadOnlyCollection<string> RetrySnapshot(string kindName)
    {
        return retryPending.TryGetValue(kindName, out var set)
            ? set.ToArray()
            : [];
    }

    /// <summary>The per-plugin grouping of one extension point's live exports
    /// (registration triples grouped by plugin id). Called under the host gate with the
    /// caller's own registration list.</summary>
    public static Dictionary<string, HashSet<string>> ExportSets(
        IReadOnlyList<PluginRegistration> registrations,
        string extensionPointName)
    {
        var sets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            if (registration.ExtensionPoint != extensionPointName) continue;
            if (!sets.TryGetValue(registration.PluginId, out var exports))
                sets[registration.PluginId] = exports = new HashSet<string>(StringComparer.Ordinal);
            exports.Add(registration.ExportName);
        }

        return sets;
    }

    /// <summary>The plugins whose export set changed between two registration
    /// rebuilds: joined, left, or re-registered with a different export set. Plugins
    /// only in the before map reconciled through their disappearance (their descriptor
    /// and approval state decide inside the pass what that means).</summary>
    public static List<string> DiffExportSets(
        Dictionary<string, HashSet<string>> before,
        Dictionary<string, HashSet<string>> after)
    {
        var changed = new List<string>();
        foreach (var (pluginId, exports) in after)
        {
            if (before.TryGetValue(pluginId, out var previous) && previous.SetEquals(exports)) continue;
            changed.Add(pluginId);
        }

        foreach (var pluginId in before.Keys)
        {
            if (!after.ContainsKey(pluginId)) changed.Add(pluginId);
        }

        return changed;
    }

    private void PublishToRegistryWide(ContributionFrameInput frame, bool guardDisposed)
    {
        foreach (var delivery in deliveries)
        {
            if (delivery.Shape == ContributionDeliveryShape.RegistryWide)
                delivery.PublishFrame(frame, guardDisposed);
        }
    }

    /// <summary>The PerPlugin route of a frame: the targets already carry the frame's
    /// union (export-set diff ∪ forced ∪ retry, deduplicated under the host gate), so
    /// every PerPlugin kind schedules the same list — A 期 has exactly one PerPlugin
    /// kind; per-kind target routing generalizes with the B 期 kind contract.</summary>
    private void ScheduleTargets(IReadOnlyList<string> pluginIds)
    {
        foreach (var delivery in deliveries)
        {
            if (delivery.Shape != ContributionDeliveryShape.PerPlugin) continue;
            foreach (var pluginId in pluginIds)
                Schedule(delivery, delivery.KindName, pluginId);
        }
    }

    /// <summary>Schedules one targeted pass for a single plugin: passes for one plugin
    /// run one at a time while different plugins reconcile in parallel. Events arriving
    /// for a plugin while its pass runs set that flight's pending flag and the running
    /// pass drains it, so passes coalesce per plugin instead of queueing an unbounded
    /// backlog behind a slow source. The flight map mutates only under
    /// <see cref="flightGate"/>, which is never held across an await.</summary>
    private void Schedule(IContributionDelivery delivery, string kindName, string pluginId)
    {
        lock (flightGate)
        {
            if (!flights.TryGetValue((kindName, pluginId), out var flight))
                flights[(kindName, pluginId)] = flight = new ReconcileFlight();
            if (flight.Running)
            {
                flight.Pending = true;
                return;
            }

            flight.Running = true;
        }

        _ = Task.Run(() => RunPassAsync(delivery, kindName, pluginId));
    }

    /// <summary>Drains one plugin's pending reconcile events around its passes: the
    /// pending flag is cleared before each pass (the pass reads fresh state, so an
    /// event that arrived before it started is covered), and a flag set during a pass
    /// schedules exactly one follow-up. An event landing between the drain check and
    /// the running reset has no runner anymore — the finally re-arms so it is not lost.</summary>
    private async Task RunPassAsync(IContributionDelivery delivery, string kindName, string pluginId)
    {
        try
        {
            while (true)
            {
                lock (flightGate)
                {
                    if (flights.TryGetValue((kindName, pluginId), out var drain))
                        drain.Pending = false;
                }

                await delivery.ReconcileAsync(pluginId, CancellationToken.None).ConfigureAwait(false);
                lock (flightGate)
                {
                    if (flights.TryGetValue((kindName, pluginId), out var flight) && flight.Pending) continue;
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The manager's lifetime ended mid-reconcile: the shutdown path.
        }
        catch (ObjectDisposedException)
        {
            // A late event raced the lifetime disposal; the generation is gone.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Reconciling plugin '{PluginId}' {Kind} contributions failed; its last contribution stays active.",
                pluginId,
                kindName);
        }
        finally
        {
            var rearm = false;
            lock (flightGate)
            {
                if (flights.TryGetValue((kindName, pluginId), out var flight))
                {
                    flight.Running = false;
                    if (flight.Pending)
                    {
                        flight.Pending = false;
                        rearm = true;
                    }
                    else
                    {
                        flights.Remove((kindName, pluginId));
                    }
                }
            }

            if (rearm) Schedule(delivery, kindName, pluginId);
        }
    }

    /// <summary>The per-plugin in-flight state guarded by <see cref="flightGate"/>:
    /// whether a pass is running and whether a later event is waiting on it.</summary>
    private sealed class ReconcileFlight
    {
        public bool Running;

        public bool Pending;
    }

    private sealed class FlightKeyComparer : IEqualityComparer<(string KindName, string PluginId)>
    {
        public static readonly FlightKeyComparer Ordinal = new();

        public bool Equals((string KindName, string PluginId) left, (string KindName, string PluginId) right) =>
            string.Equals(left.KindName, right.KindName, StringComparison.Ordinal) &&
            string.Equals(left.PluginId, right.PluginId, StringComparison.Ordinal);

        public int GetHashCode((string KindName, string PluginId) key) =>
            HashCode.Combine(
                key.KindName.GetHashCode(StringComparison.Ordinal),
                key.PluginId.GetHashCode(StringComparison.Ordinal));
    }
}
