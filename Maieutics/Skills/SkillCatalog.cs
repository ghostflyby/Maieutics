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
///     loop that debounces, rescans its root, and republishes the merged snapshot. Sources
///     merge by precedence (workspace outranks user); a same-name skill from a lower source
///     is shadowed with a diagnostic. Discovery failures are diagnostics, never load
///     failures. Roots are fixed at start (the same lifetime semantics as the workspace
///     home) and the snapshot is recomputed on watched changes, so consumers that read
///     <see cref="Current" /> per run see changes at their next run boundary.
/// </summary>
internal sealed class SkillCatalog : IAsyncDisposable
{
    /// <summary>The settle window a watched root waits after the last event before
    /// rescanning, collapsing a write burst into one scan.</summary>
    internal static readonly TimeSpan RescanDebounce = TimeSpan.FromMilliseconds(400);

    private sealed record DeclaredRoot(SkillSource Source, string RootDirectory);

    private readonly ImmutableArray<DeclaredRoot> roots;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SkillCatalog> logger;
    private readonly Lock gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<DeclaredRoot, IReadOnlyList<SkillDescriptor>> rootResults = new();
    private readonly List<Task> pumps = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private volatile SkillCatalogSnapshot current = SkillCatalogSnapshot.Empty;
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

    public async ValueTask DisposeAsync()
    {
        Task[] pumpTasks;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;

            // Watchers stop first so no new signals can arrive; the lifetime cancellation
            // then unwinds the pumps and the awaited completions are observed afterwards.
            foreach (var watcher in watchers) watcher.Dispose();
            pumpTasks = [.. pumps];
        }

        lifetime.Cancel();
        try
        {
            await Task.WhenAll(pumpTasks).ConfigureAwait(false);
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
                rootResults[root] = [];
                continue;
            }

            rootResults[root] = SkillDirectoryDiscovery.Discover(root.RootDirectory, root.Source);

            var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });
            var watcher = new FileSystemWatcher(root.RootDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                               NotifyFilters.DirectoryName | NotifyFilters.Size
            };
            void Signal(object? sender, FileSystemEventArgs args) => signals.Writer.TryWrite(true);
            watcher.Changed += Signal;
            watcher.Created += Signal;
            watcher.Deleted += Signal;
            watcher.Renamed += Signal;
            watcher.Error += (_, args) => logger.LogWarning(
                args.GetException(),
                "The skill root watcher for '{Root}' failed; the last catalog stays active.",
                root.RootDirectory);
            watcher.EnableRaisingEvents = true;

            lock (gate)
            {
                if (disposed)
                {
                    watcher.Dispose();
                    break;
                }

                watchers.Add(watcher);
                pumps.Add(RunRootPumpAsync(root, signals));
            }
        }

        RebuildSnapshot();

        // Surface the initial state once; watcher-driven rebuilds log their own line.
        if (current.Diagnostics.Length > 0)
        {
            logger.LogInformation(
                "Skill catalog started with {Count} skills and {Diagnostics} diagnostics.",
                current.Skills.Length,
                current.Diagnostics.Length);
        }
    }

    private async Task RunRootPumpAsync(DeclaredRoot root, Channel<bool> signals)
    {
        try
        {
            await foreach (var signal in signals.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                _ = signal;
                try
                {
                    // Drain everything already pending, then let the tree settle so one
                    // write burst costs at most one scan (events landing during the settle
                    // window re-arm the loop). The rescan itself runs outside the gate; only
                    // the result store and snapshot rebuild are serialized.
                    while (signals.Reader.TryRead(out _))
                    {
                    }

                    await Task.Delay(RescanDebounce, timeProvider, lifetime.Token).ConfigureAwait(false);
                    var discovered = SkillDirectoryDiscovery.Discover(root.RootDirectory, root.Source);
                    lock (gate)
                    {
                        if (disposed) return;
                        rootResults[root] = discovered;
                        RebuildSnapshot();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A rescan failure (unreadable directory, transient IO) must not kill
                    // the pump: the previous snapshot stays active and the next watched
                    // change retries.
                    logger.LogDebug(
                        exception,
                        "Rescanning the skill root '{Root}' failed; the last catalog stays active.",
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

    private void RebuildSnapshot()
    {
        var merged = new Dictionary<string, SkillDescriptor>(StringComparer.Ordinal);
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        foreach (var root in roots)
        {
            if (!rootResults.TryGetValue(root, out var descriptors)) continue;
            foreach (var descriptor in descriptors)
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
    }
}
