using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Maieutics.Skills;

/// <summary>The published catalog state: the active (non-inert) skills merged across
/// sources, and the human-readable diagnostics collected on the way. Immutable; swapped
/// atomically by <see cref="SkillCatalog" />.</summary>
internal sealed record SkillCatalogSnapshot(
    ImmutableArray<SkillDescriptor> Skills,
    ImmutableArray<string> Diagnostics)
{
    internal static readonly SkillCatalogSnapshot Empty = new([], []);

    internal int Count(SkillSource source)
    {
        var count = 0;
        foreach (var skill in Skills)
            if (skill.Source == source)
                count++;
        return count;
    }
}

/// <summary>
///     The kernel's merged skill catalog (ADR 0039 stage 1). Owns one filesystem watcher per
///     declared root; each root's events feed a per-root signal channel drained by one pump
///     loop that debounces, collects the event paths, and applies a differential update: a
///     path inside a known skill directory revalidates exactly that directory, every other
///     path rewalks only its own subtree and is diffed against the known skill directories
///     under that region. The initial scan streams the same walk and commits per discovered
///     skill directory, so the catalog goes live progressively. Sources merge by precedence
///     (workspace outranks user); a same-name skill from a lower source is shadowed with a
///     diagnostic. Discovery failures are diagnostics, never load failures. Roots are fixed
///     at start (the same lifetime semantics as the workspace home) and the snapshot is
///     recomputed on watched changes, so consumers that read <see cref="Current" /> per run
///     see changes at their next run boundary.
/// </summary>
internal sealed class SkillCatalog : IAsyncDisposable
{
    /// <summary>The settle window a watched root waits after the last event before
    /// rescanning, collapsing a write burst into one scan.</summary>
    internal static readonly TimeSpan RescanDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>The total budget disposal waits for in-flight rescan pumps before
    /// abandoning them (the rescan walk is synchronous and cannot be cancelled).</summary>
    internal static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(5);

    /// <summary>The most event paths one debounce window accumulates before the batch gives
    /// up on differencing and escalates to one full-root differential walk.</summary>
    private const int MaximumPendingEventPaths = 4096;

    private sealed record DeclaredRoot(SkillSource Source, string RootDirectory);

    /// <summary>One root's watcher feed: the capacity-one rescan signal plus the set of
    /// event paths accumulated between signals. Paths are never dropped silently — a set
    /// that overflows escalates to one full-root differential rebuild, so a region is never
    /// lost to backpressure.</summary>
    private sealed class RootWatchState
    {
        private readonly Lock gate = new();
        private readonly HashSet<string> pendingPaths = new(StringComparer.Ordinal);
        private bool fullRescanPending;

        internal Channel<bool> Signals { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        /// <summary>Records one watched path (already both halves of a rename at the call
        /// site) and arms the pump. Recording is what carries information; the signal is
        /// only a doorbell and may coalesce freely.</summary>
        internal void Record(string fullPath)
        {
            lock (gate)
            {
                if (pendingPaths.Count >= MaximumPendingEventPaths)
                    fullRescanPending = true;
                else
                    pendingPaths.Add(fullPath);
            }

            Signals.Writer.TryWrite(true);
        }

        /// <summary>Escalates to one full-root differential rebuild (watcher error
        /// recovery, or an overflowed pending set) and arms the pump.</summary>
        internal void RecordFullRescan()
        {
            lock (gate)
            {
                fullRescanPending = true;
            }

            Signals.Writer.TryWrite(true);
        }

        /// <summary>Takes the accumulated batch: deterministically ordered paths and
        /// whether the batch must fall back to a full-root diff. Clears the state so the
        /// next window starts empty; paths recorded after the take arm the next batch.</summary>
        internal (string[] Paths, bool FullRescan) TakeBatch()
        {
            lock (gate)
            {
                string[] paths = [.. pendingPaths.OrderBy(static path => path, StringComparer.Ordinal)];
                var fullRescan = fullRescanPending;
                pendingPaths.Clear();
                fullRescanPending = false;
                return (paths, fullRescan);
            }
        }
    }

    private readonly ImmutableArray<DeclaredRoot> roots;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SkillCatalog> logger;
    private readonly Lock gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SkillDirectoryVisitCounter directoryVisits = new();
    private readonly Dictionary<DeclaredRoot, Dictionary<string, SkillDescriptor>> rootSkills = new();
    private readonly Dictionary<string, IReadOnlyList<SkillDescriptor>> pluginContributions = new(StringComparer.Ordinal);
    private readonly List<Task> pumps = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private volatile SkillCatalogSnapshot current = SkillCatalogSnapshot.Empty;
    private int rebuildCount;
    private bool disposed;

    private SkillCatalog(
        IEnumerable<(SkillSource Source, string RootDirectory)> roots,
        TimeProvider timeProvider,
        ILogger<SkillCatalog> logger)
    {
        // Deduplicate by resolved path (first declaration wins) and order by precedence so
        // the merge walks sources in the order shadows are resolved.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<DeclaredRoot>();
        foreach (var (source, rootDirectory) in roots)
        {
            var resolved = Path.GetFullPath(rootDirectory);
            if (!seen.Add(resolved)) continue;
            ordered.Add(new DeclaredRoot(source, resolved));
        }

        this.roots = ordered
            .OrderBy(static root => root.Source)
            .ThenBy(static root => root.RootDirectory, StringComparer.Ordinal)
            .ToImmutableArray();
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>The composition-root factory: creates the root directories, runs the initial
    /// scan, and starts the watcher pumps. Constructors stay side-effect free.</summary>
    public static SkillCatalog Create(
        IEnumerable<(SkillSource Source, string RootDirectory)> roots,
        TimeProvider timeProvider,
        ILogger<SkillCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var catalog = new SkillCatalog(roots, timeProvider, logger);
        catalog.Start();
        return catalog;
    }

    /// <summary>The latest merged snapshot. Cheap to read; freshness is per-run by
    /// construction (ADR 0039 decision 5).</summary>
    internal SkillCatalogSnapshot Current => current;

    /// <summary>How many times the merged snapshot has been rebuilt — the observable the
    /// debounce tests assert on (a write burst must collapse to few rebuilds).</summary>
    internal int RebuildCount
    {
        get
        {
            lock (gate)
            {
                return rebuildCount;
            }
        }
    }

    /// <summary>How many directories this catalog's discovery walks have enumerated — the
    /// observable the differential tests assert on (a change inside one known skill
    /// directory must touch a handful of directories, not the tree).</summary>
    internal long DirectoryVisits => directoryVisits.Count;

    /// <summary>Replaces one plugin's skill contribution (ADR 0039 stages 2-3): each
    /// plugin owns exactly one slot — declarative roots, generator output, and (stage 3)
    /// published entries merge into it before this call. Plugin sources sit below both
    /// filesystem sources in the merge precedence, so a same-name workspace or user skill
    /// shadows the plugin's. Contributions refresh on plugin reconcile events (load,
    /// reload, approval, trigger), not on their own file watchers.</summary>
    internal void UpdatePluginContribution(string pluginId, IReadOnlyList<SkillDescriptor> descriptors)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentNullException.ThrowIfNull(descriptors);
        lock (gate)
        {
            if (disposed) return;
            pluginContributions[pluginId] = descriptors;
            RebuildSnapshot();
        }
    }

    /// <summary>Clears one plugin's contribution (approval revoked, plugin removed, or
    /// host shutdown).</summary>
    internal void RemovePluginContribution(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        lock (gate)
        {
            if (disposed || !pluginContributions.Remove(pluginId)) return;
            RebuildSnapshot();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pumpTasks;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;

            // Watchers stop first so no new signals can arrive; the lifetime cancellation
            // then unwinds the pumps, and the awaited completions are observed afterwards.
            foreach (var watcher in watchers) watcher.Dispose();
            pumpTasks = [.. pumps];
        }

        lifetime.Cancel();
        try
        {
            // A pump that already entered its (synchronous, cancellation-blind) rescan walk
            // cannot be interrupted, so the shutdown wait carries a total budget instead of
            // blocking host teardown on a slow or hung tree. An abandoned pump is safe: its
            // post-walk commit exits on the disposed check.
            var completion = Task.WhenAll(pumpTasks);
            var bounded = await Task.WhenAny(
                completion,
                Task.Delay(ShutdownBudget, timeProvider)).ConfigureAwait(false);
            if (!ReferenceEquals(bounded, completion) && !completion.IsCompleted)
                logger.LogWarning(
                    "Skill catalog disposal abandoned {Count} rescan pump(s) after the shutdown budget.",
                    pumpTasks.Length);
            await completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "A skill root pump ended with an exception during shutdown.");
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private void Start()
    {
        foreach (var root in roots)
        {
            try
            {
                Directory.CreateDirectory(root.RootDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    exception,
                    "The skill root '{Root}' cannot be created; the source stays disabled.",
                    root.RootDirectory);
                lock (gate)
                {
                    rootSkills[root] = NewSkillMap();
                }
                continue;
            }

            // Register the (empty) per-root map before scanning so every snapshot rebuild —
            // a pump of an earlier root, or the progressive commits below — observes one
            // consistent root set.
            lock (gate)
            {
                if (disposed) return;
                rootSkills[root] = NewSkillMap();
            }

            // The initial scan streams the walk and commits each discovered skill directory
            // as one atomic update-plus-rebuild, so the catalog goes live skill by skill.
            // The walk itself runs outside the gate (pumps of earlier roots are already
            // live); only the per-directory commit is serialized.
            foreach (var descriptor in SkillDirectoryDiscovery.EnumerateSubtree(
                         root.RootDirectory, root.RootDirectory, root.Source, directoryVisits))
            {
                if (SkillDirectoryOf(descriptor) is not { } skillDirectory) continue;
                lock (gate)
                {
                    if (disposed) return;
                    ApplyChanges(root, [(skillDirectory, descriptor)]);
                }
            }

            var state = new RootWatchState();
            var watcher = new FileSystemWatcher(root.RootDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                               NotifyFilters.DirectoryName | NotifyFilters.Size
            };
            watcher.Changed += (_, args) => state.Record(args.FullPath);
            watcher.Created += (_, args) => state.Record(args.FullPath);
            watcher.Deleted += (_, args) => state.Record(args.FullPath);
            // A rename is a deletion and a creation as far as the differential is
            // concerned: both halves are recorded so the old region is unwound and the new
            // one discovered.
            watcher.Renamed += (_, args) =>
            {
                state.Record(args.OldFullPath);
                state.Record(args.FullPath);
            };
            // An internal-buffer overflow means events were LOST — the pending set cannot
            // know which regions changed, so one debounced full-root differential walk
            // fully recovers.
            watcher.Error += (_, args) =>
            {
                logger.LogWarning(
                    args.GetException(),
                    "The skill root watcher for '{Root}' failed; scheduling a recovery rescan.",
                    root.RootDirectory);
                state.RecordFullRescan();
            };
            watcher.EnableRaisingEvents = true;

            lock (gate)
            {
                if (disposed)
                {
                    watcher.Dispose();
                    break;
                }

                watchers.Add(watcher);
                pumps.Add(RunRootPumpAsync(root, state));
            }
        }

        // The final rebuild is serialized against pump rebuilds: a pump that fired during
        // startup must not observe a half-committed root map, and this rebuild must
        // not overwrite a newer pump-published snapshot with stale inputs.
        lock (gate)
        {
            if (disposed) return;
            RebuildSnapshot();
        }
    }

    private static Dictionary<string, SkillDescriptor> NewSkillMap() =>
        new(StringComparer.Ordinal);

    private async Task RunRootPumpAsync(DeclaredRoot root, RootWatchState state)
    {
        try
        {
            await foreach (var signal in state.Signals.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                _ = signal;
                try
                {
                    // Drain everything already pending, then let the tree settle so one
                    // write burst costs at most one differential pass (events landing
                    // during the settle window join this batch; later ones re-arm the
                    // loop). The differential walk runs outside the gate — it is
                    // synchronous IO — and only the per-batch map mutation plus snapshot
                    // rebuild are serialized, so one batch becomes visible atomically.
                    while (state.Signals.Reader.TryRead(out _))
                    {
                    }

                    await Task.Delay(RescanDebounce, timeProvider, lifetime.Token).ConfigureAwait(false);
                    var (paths, fullRescan) = state.TakeBatch();
                    if (paths.Length == 0 && !fullRescan) continue;

                    // The working set of believed-present skill directories evolves with
                    // the batch, so a path recorded after another already claimed or
                    // released its region is diffed against the batch's own intermediate
                    // state, never a stale snapshot of it.
                    Dictionary<string, SkillDescriptor> known;
                    lock (gate)
                    {
                        if (disposed) return;
                        known = rootSkills[root];
                    }

                    var workingKnown = new HashSet<string>(known.Keys, StringComparer.Ordinal);
                    var plan = new List<(string SkillDirectory, SkillDescriptor? Descriptor)>();
                    if (fullRescan)
                    {
                        BuildRegionPlan(root, root.RootDirectory, workingKnown, plan);
                    }
                    else
                    {
                        foreach (var path in paths)
                            BuildPathPlan(root, path, workingKnown, plan);
                    }

                    lock (gate)
                    {
                        if (disposed) return;
                        ApplyChanges(root, plan);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A differential pass failure (unreadable directory, transient IO) must
                    // not kill the pump: the previous snapshot stays active and the next
                    // watched change retries.
                    logger.LogDebug(
                        exception,
                        "Diffing a watched change in the skill root '{Root}' failed; the last catalog stays active.",
                        root.RootDirectory);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The owner cancelled the pump: the normal shutdown path.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The skill root pump for '{Root}' stopped unexpectedly.", root.RootDirectory);
        }
    }

    /// <summary>Turns one watched path into plan entries. A path inside a known skill
    /// directory can only change that one skill (its subtree is resources), so exactly that
    /// directory is revalidated; when its <c>SKILL.md</c> is gone the claim is released and
    /// the directory itself becomes the region to rewalk (skills below it were resources a
    /// moment ago). Any other path becomes its own region root — the path itself when it is
    /// (or was) a directory, the containing directory when it is a live file — and is
    /// diffed against everything known under that region.</summary>
    private void BuildPathPlan(
        DeclaredRoot root,
        string fullPath,
        HashSet<string> workingKnown,
        List<(string SkillDirectory, SkillDescriptor? Descriptor)> plan)
    {
        var path = Path.GetFullPath(fullPath);
        var containing = workingKnown.FirstOrDefault(candidate =>
            string.Equals(path, candidate, StringComparison.Ordinal) ||
            SkillDirectoryDiscovery.IsWithinRoot(candidate, path));
        if (containing is { } skillDirectory)
        {
            if (SkillDirectoryDiscovery.RediscoverSkillDirectory(
                    skillDirectory, root.RootDirectory, root.Source, directoryVisits) is { } fresh)
            {
                plan.Add((skillDirectory, fresh));
                return;
            }

            BuildRegionPlan(root, skillDirectory, workingKnown, plan);
            return;
        }

        // A deleted path is treated as its own region: walking a missing region yields an
        // empty set, which removes exactly the known skills under it and touches nothing
        // else — correct for a deleted directory, a no-op for a deleted uninteresting file.
        var region = Directory.Exists(path)
            ? path
            : File.Exists(path)
                ? Path.GetDirectoryName(path)
                : path;
        BuildRegionPlan(root, region ?? root.RootDirectory, workingKnown, plan);
    }

    /// <summary>Differential rebuild of one region: walks only the region's subtree, then
    /// adds, removes, and replaces the root's skill directories under the region. A new
    /// boundary <c>SKILL.md</c> above existing skills preempts them (the walk never
    /// descends into a claimed directory, so the deeper old skills leave the map); removing
    /// one re-exposes the skills below it.</summary>
    private void BuildRegionPlan(
        DeclaredRoot root,
        string regionRoot,
        HashSet<string> workingKnown,
        List<(string SkillDirectory, SkillDescriptor? Descriptor)> plan)
    {
        var regionFullName = Path.GetFullPath(regionRoot);
        var fresh = new Dictionary<string, SkillDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in SkillDirectoryDiscovery.EnumerateSubtree(
                     regionFullName, root.RootDirectory, root.Source, directoryVisits))
            if (SkillDirectoryOf(descriptor) is { } skillDirectory)
                fresh[skillDirectory] = descriptor;

        List<string>? released = null;
        foreach (var known in workingKnown)
        {
            if (!string.Equals(known, regionFullName, StringComparison.Ordinal) &&
                !SkillDirectoryDiscovery.IsWithinRoot(regionFullName, known)) continue;
            if (fresh.ContainsKey(known)) continue;
            (released ??= []).Add(known);
            plan.Add((known, null));
        }

        foreach (var (skillDirectory, descriptor) in fresh)
        {
            plan.Add((skillDirectory, descriptor));
            workingKnown.Add(skillDirectory);
        }

        if (released is not null)
            foreach (var skillDirectory in released)
                workingKnown.Remove(skillDirectory);
    }

    /// <summary>Applies one batch of per-directory changes (null descriptor = the skill is
    /// gone) to the root's map and rebuilds the merged snapshot. Gate held by the caller:
    /// one batch is atomic — consumers never observe a half-applied diff. Additions honor
    /// the per-root skill bound; replacements and removals always apply.</summary>
    private void ApplyChanges(DeclaredRoot root, IReadOnlyList<(string SkillDirectory, SkillDescriptor? Descriptor)> changes)
    {
        if (!rootSkills.TryGetValue(root, out var known)) return;
        foreach (var (skillDirectory, descriptor) in changes)
        {
            if (descriptor is null)
            {
                known.Remove(skillDirectory);
                continue;
            }

            if (known.TryGetValue(skillDirectory, out var existing) && existing == descriptor) continue;
            if (!known.ContainsKey(skillDirectory) &&
                known.Count >= SkillDirectoryDiscovery.MaximumSkillsPerRoot) continue;
            known[skillDirectory] = descriptor;
        }

        RebuildSnapshot();
    }

    /// <summary>The skill directory a filesystem descriptor was discovered in — the per-root
    /// map's key. Normalized the same way for initial, revalidation, and region walks so
    /// the three index one tree identically.</summary>
    private static string? SkillDirectoryOf(SkillDescriptor descriptor)
    {
        if (descriptor.BodyPath is not { } bodyPath) return null;
        var directory = Path.GetDirectoryName(bodyPath);
        return directory is null ? null : Path.GetFullPath(directory);
    }

    private void RebuildSnapshot()
    {
        var merged = new Dictionary<string, SkillDescriptor>(StringComparer.Ordinal);
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        foreach (var root in roots)
        {
            if (!rootSkills.TryGetValue(root, out var known)) continue;

            // Deterministic in-root order: the same-name winner is the first entry in
            // ordinal skill-directory order (equivalent to the former ordinal body-path
            // order), independent of map insertion or filesystem enumeration order.
            foreach (var (_, descriptor) in known.OrderBy(
                         static entry => entry.Key, StringComparer.Ordinal))
            {
                if (descriptor.Diagnostic is { } diagnostic)
                {
                    diagnostics.Add($"[{root.Source}] {descriptor.Name}: {diagnostic}");
                    continue;
                }

                if (merged.TryGetValue(descriptor.Name, out var winner))
                {
                    diagnostics.Add(
                        $"[{descriptor.Source}] '{descriptor.Name}' is shadowed by the {winner.Source} skill of the same name.");
                    continue;
                }

                merged.Add(descriptor.Name, descriptor);
            }
        }

        // Plugin contributions merge after every filesystem source: a plugin skill never
        // shadows workspace or user skills (ADR 0039 decision 1), and within a plugin
        // contribution the caller has already ordered its own production modes. Plugins
        // iterate in id order so shadowing between plugins is deterministic.
        foreach (var (_, descriptors) in pluginContributions.OrderBy(
                     static pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var descriptor in descriptors)
            {
                if (descriptor.Diagnostic is { } diagnostic)
                {
                    diagnostics.Add($"[plugin] {descriptor.Name}: {diagnostic}");
                    continue;
                }

                if (merged.TryGetValue(descriptor.Name, out var winner))
                {
                    diagnostics.Add(
                        $"[{descriptor.Source}] '{descriptor.Name}' is shadowed by the {winner.Source} skill of the same name.");
                    continue;
                }

                merged.Add(descriptor.Name, descriptor);
            }
        }

        rebuildCount++;
        var skills = merged.Values
            .OrderBy(static skill => skill.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        current = new SkillCatalogSnapshot(skills, diagnostics.ToImmutable());
        logger.LogInformation(
            "Skill catalog: {Count} skills ({Workspace} workspace, {User} user), {Diagnostics} diagnostics.",
            skills.Length,
            current.Count(SkillSource.Workspace),
            current.Count(SkillSource.User),
            diagnostics.Count);
        if (diagnostics.Count > 0)
        {
            // The operator's only trace of why untrusted content did not load; debug level
            // because rebuilds fire on every watched change.
            logger.LogDebug(
                "Skill catalog diagnostics: {Diagnostics}",
                string.Join(" | ", diagnostics));
        }
    }
}
