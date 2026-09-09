using Maieutics.Agent;
using Maieutics.Persistence;

namespace Maieutics.Commands;

/// <summary>
///     Owns the executable's active <see cref="IAgentSession" /> and implements the interface by
///     delegation, so notebook control cells can replace the session without re-resolving the
///     kernel application. Runs already started keep executing against the session they began
///     on; a swap only affects later turns.
///     With persistence enabled, every session belongs to one fork family and each family owns
///     exactly one <c>families/&lt;family-id&gt;/history.db</c>, keyed by the family root's
///     session id. Fork heads live in their root ancestor's family file and reference the
///     shared prefix turns instead of copying them (ADR 0009); <see cref="ResolveFamily" />
///     locates the owning family for any member. Family databases are opened lazily, cached,
///     and disposed with the manager. Recovery is manual only: nothing is restored until a
///     <c>%session resume</c> cell names a stored session.
/// </summary>
internal sealed class MaieuticsAgentSessionManager : IAgentSession, IDisposable
{
    private readonly IAgentRunProfileProvider profileProvider;
    private readonly string? familiesRoot;
    private readonly Func<AgentSessionId, SqliteTranscriptStore>? storeFactory;
    private readonly IAgentObjectStore? objectStore;
    private readonly IObjectReclaimer? reclaimer;
    private readonly string? viewSessionsRoot;
    private readonly string? objectsRoot;
    private readonly Lock gate = new();
    private readonly Dictionary<string, SqliteTranscriptStore> stores = new(StringComparer.Ordinal);
    private IAgentSession current;

    public MaieuticsAgentSessionManager(
        IAgentRunProfileProvider profileProvider,
        string? familiesRoot,
        Func<AgentSessionId, SqliteTranscriptStore>? storeFactory,
        IAgentObjectStore? objectStore = null,
        IObjectReclaimer? reclaimer = null,
        string? viewSessionsRoot = null,
        string? objectsRoot = null)
    {
        this.profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        this.familiesRoot = familiesRoot;
        this.storeFactory = storeFactory;
        this.objectStore = objectStore;
        this.reclaimer = reclaimer;
        this.viewSessionsRoot = viewSessionsRoot;
        this.objectsRoot = objectsRoot;
        current = new AgentSession(
            profileProvider,
            transcriptStore: OpenStore(AgentSessionId.Create()),
            objectStore: objectStore);
    }

    public bool PersistenceEnabled => storeFactory is not null;

    public AgentSessionId Id => Current.Id;

    public Task<IAgentRun> StartTurnAsync(AgentTurn turn, CancellationToken cancellationToken = default)
    {
        return Current.StartTurnAsync(turn, cancellationToken);
    }

    public AgentTranscript GetTranscriptSnapshot()
    {
        return Current.GetTranscriptSnapshot();
    }

    /// <summary>Lists the stored sessions across all family databases, most recently active
    /// first. Sessions that never committed a turn have no row and are not listed, except
    /// sessions that were explicitly renamed: naming one creates a zero-turn row so the
    /// name survives.</summary>
    public IReadOnlyList<AgentSessionDescriptor> ListStoredSessions()
    {
        if (storeFactory is null || familiesRoot is null || !Directory.Exists(familiesRoot)) return [];

        var descriptors = new List<AgentSessionDescriptor>();
        foreach (var directory in Directory.EnumerateDirectories(familiesRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var familyId)) continue;

            descriptors.AddRange(RequiredStore(new AgentSessionId(familyId)).ListSessions());
        }

        return descriptors
            .OrderByDescending(session => session.LastActivityAt)
            .ToArray();
    }

    /// <summary>Replaces the active session with the stored session. Resuming the already active
    /// identity is a no-op; in-flight runs continue against the session they started on.</summary>
    /// <exception cref="ArgumentException">Transcript persistence is disabled.</exception>
    /// <exception cref="AgentSessionNotFoundException">No family database holds the session.</exception>
    public AgentSessionId Resume(AgentSessionId sessionId)
    {
        if (storeFactory is null)
        {
            throw new ArgumentException(
                "Transcript persistence is disabled; enable Maieutics:Agent:Persistence:Enabled to resume sessions.");
        }

        if (sessionId == Id) return sessionId;

        var familyId = ResolveFamily(sessionId) ?? throw new AgentSessionNotFoundException(sessionId);
        var restored = AgentSession.Resume(profileProvider, RequiredStore(familyId), sessionId, objectStore: objectStore);
        lock (gate)
        {
            current = restored;
        }

        return sessionId;
    }

    /// <summary>Forks a stored session: creates a new head in the source's root family database
    /// whose history is the source's first <paramref name="forkPointSeq" /> turns, and makes the
    /// fork the active session. The source session keeps its stored history; the fork starts
    /// empty above the fork point, so its next turn continues from there (ADR 0009 — existing
    /// turns are referenced, never copied).</summary>
    /// <returns>The new fork's session id.</returns>
    /// <param name="sourceId">The stored session to fork from.</param>
    /// <param name="forkPointSeq">How many of the source's committed turns the fork keeps;
    /// zero starts the fork from an empty history.</param>
    /// <param name="title">An explicit title; when omitted one is derived from the source's
    /// title or preview plus the branch point.</param>
    /// <exception cref="ArgumentException">Transcript persistence is disabled.</exception>
    /// <exception cref="AgentSessionNotFoundException">No family database holds the source.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The fork point is negative or beyond the source's committed turns.</exception>
    public AgentSessionId Fork(AgentSessionId sourceId, int forkPointSeq, string? title = null)
    {
        if (storeFactory is null)
        {
            throw new ArgumentException(
                "Transcript persistence is disabled; enable Maieutics:Agent:Persistence:Enabled to fork sessions.");
        }

        var familyId = ResolveFamily(sourceId) ?? throw new AgentSessionNotFoundException(sourceId);
        var store = RequiredStore(familyId);
        // Validate the fork point against the source's visible history BEFORE
        // writing the head row, so a rejected fork leaves no phantom session.
        var sourceTranscript = store.LoadTranscript(sourceId) ??
                               throw new AgentSessionNotFoundException(sourceId);
        ArgumentOutOfRangeException.ThrowIfNegative(forkPointSeq);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(forkPointSeq, sourceTranscript.Turns.Length);

        var forkId = AgentSessionId.Create();
        var effectiveTitle = title ?? ForkTitle(store, sourceId, forkPointSeq);
        store.CreateForkSession(forkId, sourceId, forkPointSeq, effectiveTitle);
        var forked = AgentSession.Fork(
            profileProvider,
            store,
            sourceId,
            forkId,
            forkPointSeq,
            objectStore: objectStore);
        lock (gate)
        {
            current = forked;
        }

        return forkId;
    }

    /// <summary>Loads one stored session's committed transcript across family databases, or
    /// <see langword="null" /> when the session has no stored row. Fork heads load their
    /// ancestor prefix plus their own turns.</summary>
    public AgentTranscript? LoadStoredTranscript(AgentSessionId sessionId)
    {
        if (storeFactory is null) return null;

        var familyId = ResolveFamily(sessionId);
        if (familyId is not { } resolved) return null;

        return RequiredStore(resolved).LoadTranscript(sessionId);
    }

    /// <summary>Derives the fork's display title: the source's title or preview, collapsed to
    /// one line and capped, plus the branch point. A source with neither stays untitled.</summary>
    private static string? ForkTitle(
        SqliteTranscriptStore store,
        AgentSessionId sourceId,
        int forkPointSeq)
    {
        var source = store.ListSessions().FirstOrDefault(session => session.Id == sourceId);
        var display = source?.Title ?? source?.Preview;
        if (string.IsNullOrWhiteSpace(display)) return null;

        var oneLine = string.Join(' ', display.Split(new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var suffix = $" · branch @ turn {forkPointSeq + 1}";
        var budget = SqliteTranscriptStore.MaxDisplayTextLength - suffix.Length;
        var body = oneLine.Length <= budget ? oneLine : oneLine[..budget] + "…";
        return body + suffix;
    }

    /// <summary>Sets or clears one stored session's title. Renaming works for any stored
    /// session, active or not; an uncommitted session gets its row created (zero turns) so the
    /// name survives and the session is listed. Whitespace-only titles clear; titles are
    /// trimmed and capped at 200 characters.</summary>
    /// <returns>The stored (normalized) title.</returns>
    /// <exception cref="ArgumentException">Transcript persistence is disabled or the title
    /// exceeds the length cap.</exception>
    public string? SetTitle(AgentSessionId sessionId, string? title)
    {
        if (storeFactory is null)
        {
            throw new ArgumentException(
                "Transcript persistence is disabled; enable Maieutics:Agent:Persistence:Enabled to rename sessions.");
        }

        string? normalized;
        if (string.IsNullOrWhiteSpace(title))
        {
            normalized = null;
        }
        else
        {
            normalized = title.Trim();
            if (normalized.Length > SqliteTranscriptStore.MaxDisplayTextLength)
            {
                throw new ArgumentException(
                    $"The session title must be at most {SqliteTranscriptStore.MaxDisplayTextLength} characters.");
            }
        }

        // A session with no family database yet is its own family root; naming it creates the
        // database and its zero-turn row.
        var familyId = ResolveFamily(sessionId) ?? sessionId;
        RequiredStore(familyId).SetTitle(sessionId, normalized);
        return normalized;
    }

    /// <summary>Finds one stored session's descriptor across family databases, or
    /// <see langword="null" /> when the session has no stored row.</summary>
    public AgentSessionDescriptor? FindDescriptor(AgentSessionId sessionId)
    {
        if (storeFactory is null || familiesRoot is null || !Directory.Exists(familiesRoot)) return null;

        var familyId = ResolveFamily(sessionId);
        if (familyId is not { } resolved) return null;

        return RequiredStore(resolved).ListSessions()
            .FirstOrDefault(session => session.Id == sessionId);
    }

    /// <summary>Replaces the active session with a fresh one in its own family. The previous
    /// session's stored history remains.</summary>
    public AgentSessionId StartNew()
    {
        var sessionId = AgentSessionId.Create();
        lock (gate)
        {
            current = new AgentSession(
                profileProvider,
                transcriptStore: OpenStore(sessionId),
                objectStore: objectStore);
            return sessionId;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var store in stores.Values)
            {
                if (store is IDisposable disposable) disposable.Dispose();
            }

            stores.Clear();
        }
    }

    /// <summary>Maintenance pass: deletes stored objects that no committed turn references and
    /// that were last written before the grace cutoff. Returns the number removed.</summary>
    /// <exception cref="ArgumentException">Transcript persistence is disabled.</exception>
    public int PruneObjects(TimeSpan gracePeriod)
    {
        if (reclaimer is null || storeFactory is null)
            throw new ArgumentException(
                "Transcript persistence is disabled; enable Maieutics:Agent:Persistence:Enabled to reclaim objects.");

        var live = new HashSet<string>(StringComparer.Ordinal);
        if (familiesRoot is not null && Directory.Exists(familiesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(familiesRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var familyId)) continue;

                live.UnionWith(RequiredStore(new AgentSessionId(familyId)).GetReferencedObjectIds());
            }
        }

        return reclaimer.DeleteExcept(live, DateTimeOffset.UtcNow - gracePeriod);
    }

    /// <summary>Maintenance pass: rebuilds the derived inspection view (one relative link per
    /// referenced object per session) from the canonical databases. Best effort — platforms
    /// without symlink support simply keep an empty view. Returns the number of live links.</summary>
    /// <exception cref="ArgumentException">Transcript persistence is disabled.</exception>
    public int RepairObjectView()
    {
        if (storeFactory is null || familiesRoot is null || viewSessionsRoot is null || objectsRoot is null)
            throw new ArgumentException(
                "Transcript persistence is disabled; enable Maieutics:Agent:Persistence:Enabled to build the object view.");

        var familyStores = new List<SqliteTranscriptStore>();
        if (Directory.Exists(familiesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(familiesRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var familyId)) continue;

                familyStores.Add(RequiredStore(new AgentSessionId(familyId)));
            }
        }

        return AgentObjectView.Repair(viewSessionsRoot, objectsRoot, familyStores);
    }

    private SqliteTranscriptStore? OpenStore(AgentSessionId familyId)
    {
        if (storeFactory is null) return null;

        lock (gate)
        {
            if (stores.TryGetValue(familyId.Value.ToString("N"), out var existing)) return existing;

            var store = storeFactory(familyId);
            stores[familyId.Value.ToString("N")] = store;
            return store;
        }
    }

    private SqliteTranscriptStore RequiredStore(AgentSessionId familyId)
    {
        var store = OpenStore(familyId);
        return store ?? throw new InvalidOperationException("The transcript store factory is not configured.");
    }

    /// <summary>Locates the family that owns a session. Today every session is its own family
    /// root, so the direct <c>families/&lt;id&gt;</c> directory almost always answers; the scan
    /// covers the future fork case where children live in their ancestor's family file.</summary>
    private AgentSessionId? ResolveFamily(AgentSessionId sessionId)
    {
        if (familiesRoot is null) return null;
        if (File.Exists(SqliteTranscriptStore.FamilyDatabasePath(familiesRoot, sessionId))) return sessionId;

        if (Directory.Exists(familiesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(familiesRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var familyId)) continue;

                if (RequiredStore(new AgentSessionId(familyId)).LoadTranscript(sessionId) is not null)
                {
                    return new AgentSessionId(familyId);
                }
            }
        }

        return null;
    }

    private IAgentSession Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }
}
