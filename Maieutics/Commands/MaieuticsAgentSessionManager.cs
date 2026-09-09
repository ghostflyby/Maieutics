using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Persistence;

namespace Maieutics.Commands;

/// <summary>
///     Owns the executable's live sessions: a bounded registry of <see cref="AgentSession" />
///     instances keyed by identity, created eagerly (<see cref="StartNew" />, fork) or lazily
///     (addressing a stored session resumes it first — <see cref="Resolve" />). Runs already
///     started keep executing against the session they began on. One mutating run per session
///     is enforced by the session itself (invariant 4), so distinct sessions run concurrently.
///     The <see cref="IAgentSession" /> implementation is the <em>foreground</em> view: the
///     most recently activated session (start / resume / fork move it; per-session addressing
///     does not), kept for process-level surfaces — status, legacy singular endpoints, and
///     session-blind commands.
///     With persistence enabled, every session belongs to one fork family and each family owns
///     exactly one <c>families/&lt;family-id&gt;/history.db</c>. Fork heads live in their root
///     ancestor's family file and reference the shared prefix turns instead of copying them
///     (ADR 0009); <see cref="ResolveFamily" /> locates the owning family for any member.
///     Family databases are opened lazily, cached, and disposed with the manager. Recovery is
///     manual only: nothing is restored until a cell or request names a stored session.
/// </summary>
internal sealed class MaieuticsAgentSessionManager : IAgentSession, IDisposable
{
    /// <summary>Bounded live set: the registry keeps at most this many sessions, evicting the
    /// least recently used ones that can still be lazily re-resumed from storage (zero-turn
    /// and persistence-less sessions are never evicted, since their state exists nowhere
    /// else). Eviction only drops the registry entry — in-flight runs keep their session.</summary>
    internal const int LiveSessionCapacity = 8;

    private readonly IAgentRunProfileProvider profileProvider;
    private readonly IMaieuticsRuntimeConfiguration? runtimeConfiguration;
    private readonly string? familiesRoot;
    private readonly Func<AgentSessionId, SqliteTranscriptStore>? storeFactory;
    private readonly IAgentObjectStore? objectStore;
    private readonly IObjectReclaimer? reclaimer;
    private readonly string? viewSessionsRoot;
    private readonly string? objectsRoot;
    private readonly Lock gate = new();
    private readonly Dictionary<string, LiveSession> live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqliteTranscriptStore> stores = new(StringComparer.Ordinal);
    private IAgentSession foreground;
    private long foregroundVersion;

    public MaieuticsAgentSessionManager(
        IAgentRunProfileProvider profileProvider,
        string? familiesRoot,
        Func<AgentSessionId, SqliteTranscriptStore>? storeFactory,
        IAgentObjectStore? objectStore = null,
        IObjectReclaimer? reclaimer = null,
        string? viewSessionsRoot = null,
        string? objectsRoot = null,
        IMaieuticsRuntimeConfiguration? runtimeConfiguration = null)
    {
        this.profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        this.runtimeConfiguration = runtimeConfiguration;
        this.familiesRoot = familiesRoot;
        this.storeFactory = storeFactory;
        this.objectStore = objectStore;
        this.reclaimer = reclaimer;
        this.viewSessionsRoot = viewSessionsRoot;
        this.objectsRoot = objectsRoot;
        foreground = CreateLive(AgentSessionId.Create());
    }

    public bool PersistenceEnabled => storeFactory is not null;

    /// <summary>Gets how many sessions are currently live. Exposed for diagnostics.</summary>
    public int LiveCount
    {
        get
        {
            lock (gate)
            {
                return live.Count;
            }
        }
    }

    /// <summary>Gets a monotonically increasing version that changes whenever the foreground
    /// session moves, so callers can detect "did this command switch sessions".</summary>
    public long ForegroundVersion
    {
        get
        {
            lock (gate)
            {
                return foregroundVersion;
            }
        }
    }

    /// <inheritdoc />
    public AgentSessionId Id => Foreground.Id;

    /// <inheritdoc />
    public bool IsRunInProgress => Foreground.IsRunInProgress;

    /// <inheritdoc />
    public Task<IAgentRun> StartTurnAsync(AgentTurn turn, CancellationToken cancellationToken = default)
    {
        return Foreground.StartTurnAsync(turn, cancellationToken);
    }

    /// <inheritdoc />
    public AgentTranscript GetTranscriptSnapshot()
    {
        return Foreground.GetTranscriptSnapshot();
    }

    /// <summary>Whether the session currently has a live instance, without creating one.</summary>
    public bool IsLive(AgentSessionId sessionId)
    {
        lock (gate)
        {
            return live.ContainsKey(sessionId.Value.ToString("N"));
        }
    }

    /// <summary>Resolves one live session, lazily resuming a stored one. Addressing a session
    /// never moves the foreground. Unknown sessions (or persistence disabled with no live
    /// match) fail typed.</summary>
    public IAgentSession Resolve(AgentSessionId sessionId)
    {
        var key = sessionId.Value.ToString("N");
        lock (gate)
        {
            if (live.TryGetValue(key, out var existing))
            {
                existing.Touch();
                return existing.Session;
            }
        }

        if (storeFactory is null)
        {
            throw new AgentSessionNotFoundException(sessionId);
        }

        // Resume outside the gate: opens family databases and loads transcripts.
        var familyId = ResolveFamily(sessionId) ?? throw new AgentSessionNotFoundException(sessionId);
        var wrapper = CreateWrapper();
        var restored = AgentSession.Resume(
            ProviderFor(wrapper),
            RequiredStore(familyId),
            sessionId,
            objectStore: objectStore);

        lock (gate)
        {
            if (live.TryGetValue(key, out var raced))
            {
                raced.Touch();
                return raced.Session;
            }

            var entry = new LiveSession(restored, wrapper);
            live[key] = entry;
            entry.Touch();
        }

        EnforceLiveCapacity(sessionId);
        return restored;
    }

    /// <summary>Replaces the foreground session with the stored session, lazily resuming it.
    /// Resuming the already-foreground identity is a no-op; in-flight runs continue against
    /// the session they started on.</summary>
    /// <exception cref="ArgumentException">Transcript persistence is disabled.</exception>
    /// <exception cref="AgentSessionNotFoundException">No family database holds the session.</exception>
    public AgentSessionId Resume(AgentSessionId sessionId)
    {
        if (storeFactory is null)
        {
            lock (gate)
            {
                if (live.TryGetValue(sessionId.Value.ToString("N"), out var existing))
                {
                    MoveForegroundLocked(existing.Session);
                    return sessionId;
                }
            }

            throw new ArgumentException(
                "Transcript persistence is disabled; enable Maieutics:Agent:Persistence:Enabled to resume sessions.");
        }

        MoveForeground(Resolve(sessionId));
        return sessionId;
    }

    /// <summary>Replaces the foreground session with a fresh one. The previous session stays
    /// live until evicted; its stored history remains.</summary>
    public AgentSessionId StartNew()
    {
        var sessionId = AgentSessionId.Create();
        var wrapper = CreateWrapper();
        var session = new AgentSession(
            ProviderFor(wrapper),
            transcriptStore: OpenStore(sessionId),
            objectStore: objectStore,
            sessionId: sessionId);
        lock (gate)
        {
            live[sessionId.Value.ToString("N")] = new LiveSession(session, wrapper);
            MoveForegroundLocked(session);
        }

        EnforceLiveCapacity(sessionId);
        return sessionId;
    }

    /// <summary>Forks a stored session: creates a new head in the source's root family database
    /// whose history is the source's first <paramref name="forkPointSeq" /> turns, and makes the
    /// fork the foreground session. The source session keeps its stored history; the fork starts
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
        var wrapper = CreateWrapper();
        var forked = AgentSession.Fork(
            ProviderFor(wrapper),
            store,
            sourceId,
            forkId,
            forkPointSeq,
            objectStore: objectStore);
        lock (gate)
        {
            live[forkId.Value.ToString("N")] = new LiveSession(forked, wrapper);
            MoveForegroundLocked(forked);
        }

        EnforceLiveCapacity(forkId);
        return forkId;
    }

    /// <summary>Sets the addressed session's model-profile override (a configured profile
    /// id), so its next turn runs on that profile regardless of the process selection.
    /// Clears the override when <paramref name="profileId" /> is <see langword="null" />.
    /// A stored-but-not-live session is lazily resumed first.</summary>
    /// <exception cref="ArgumentException">No runtime configuration is available or the
    /// profile is unknown.</exception>
    public void SetProfileOverride(AgentSessionId sessionId, string? profileId)
    {
        var wrapper = RequireWrapper(sessionId);
        if (profileId is null)
        {
            wrapper.SetOverride(null);
            return;
        }

        var selection = runtimeConfiguration!.GetModelProfileSelection();
        var match = selection.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new ArgumentException($"No model profile matches '{profileId}'.");
        }

        wrapper.SetOverride(match.Id);
    }

    /// <summary>Gets the addressed session's profile override, or
    /// <see langword="null" /> when it follows the process selection. A
    /// stored-but-not-live session is lazily resumed first.</summary>
    public string? GetProfileOverride(AgentSessionId sessionId)
    {
        return RequireWrapper(sessionId).Override;
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

    /// <summary>Reads the foreground session's identity and turn count atomically so the
    /// compatibility alias cannot mix two foregrounds.</summary>
    public (AgentSessionId Id, long Turns) ForegroundInfo
    {
        get
        {
            lock (gate)
            {
                return (foreground.Id, foreground.GetTranscriptSnapshot().Turns.Length);
            }
        }
    }

    private IAgentSession Foreground
    {
        get
        {
            lock (gate)
            {
                return foreground;
            }
        }
    }

    private void MoveForeground(IAgentSession session)
    {
        lock (gate)
        {
            MoveForegroundLocked(session);
        }
    }

    private void MoveForegroundLocked(IAgentSession session)
    {
        foreground = session;
        foregroundVersion++;
    }

    /// <summary>Creates the per-session profile wrapper, or returns
    /// <see langword="null" /> when no runtime configuration backs per-session overrides.</summary>
    private MaieuticsSessionProfileProvider? CreateWrapper()
    {
        return runtimeConfiguration is null ? null : new MaieuticsSessionProfileProvider(runtimeConfiguration);
    }

    private IAgentRunProfileProvider ProviderFor(MaieuticsSessionProfileProvider? wrapper)
    {
        return wrapper ?? profileProvider;
    }

    /// <summary>Creates and registers a fresh live session (its own family database), making
    /// it the foreground. Used for the boot session.</summary>
    private IAgentSession CreateLive(AgentSessionId sessionId)
    {
        var wrapper = CreateWrapper();
        var session = new AgentSession(
            wrapper is null ? profileProvider : ProviderFor(wrapper),
            transcriptStore: OpenStore(sessionId),
            objectStore: objectStore,
            sessionId: sessionId);
        lock (gate)
        {
            live[sessionId.Value.ToString("N")] = new LiveSession(session, wrapper);
            MoveForegroundLocked(session);
        }

        return session;
    }

    /// <summary>Requires the addressed session's profile wrapper, lazily resuming a stored
    /// session so addressed <c>%model</c> commands work on a freshly reopened notebook.</summary>
    private MaieuticsSessionProfileProvider RequireWrapper(AgentSessionId sessionId)
    {
        if (runtimeConfiguration is null)
        {
            throw new ArgumentException("Model profile selection is not available in this host.");
        }

        Resolve(sessionId);

        lock (gate)
        {
            if (live.TryGetValue(sessionId.Value.ToString("N"), out var entry) && entry.Profile is { } profile)
            {
                return profile;
            }
        }

        throw new ArgumentException("The session's profile provider is not configured.");
    }

    /// <summary>Keeps the live set bounded, evicting the least recently used sessions that
    /// can still be lazily re-resumed from storage. Eviction drops the session's profile
    /// override with it (overrides are not durable; a re-resume follows the process
    /// selection). A session with a run in flight is never
    /// evicted (evicting it would let the next addressed turn build a second instance for
    /// the same identity and break the per-instance single-run gate), the newly activated
    /// session is protected, and a candidate that was touched after candidacy is skipped.
    /// Runs outside the gate because resumability checks open family databases.</summary>
    private void EnforceLiveCapacity(AgentSessionId? protectedId = null)
    {
        List<(string Key, long Touched, AgentSessionId Id)> candidates;
        lock (gate)
        {
            if (live.Count <= LiveSessionCapacity) return;

            candidates = live.Values
                .Where(entry =>
                    !ReferenceEquals(entry.Session, foreground) &&
                    !entry.Session.IsRunInProgress &&
                    (protectedId is null || entry.Session.Id != protectedId.Value))
                .OrderBy(entry => entry.Touched)
                .Select(entry => (entry.Session.Id.Value.ToString("N"), entry.Touched, entry.Session.Id))
                .ToList();
        }

        foreach (var (key, touched, id) in candidates)
        {
            if (LoadStoredTranscript(id) is null) continue; // never lose unresumable state

            lock (gate)
            {
                if (live.Count <= LiveSessionCapacity) return;
                // Still the stale entry we evaluated: not foreground or protected,
                // not touched since candidacy, and with no run in flight.
                if (live.TryGetValue(key, out var entry) &&
                    entry.Touched == touched &&
                    !ReferenceEquals(entry.Session, foreground) &&
                    !entry.Session.IsRunInProgress &&
                    (protectedId is null || entry.Session.Id != protectedId.Value))
                {
                    live.Remove(key);
                }
            }
        }
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

    /// <summary>Locates the family that owns a session: the direct
    /// <c>families/&lt;id&gt;</c> directory when the session is a family root, otherwise a
    /// scan that covers fork members living in their ancestor's family file.</summary>
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

    private sealed class LiveSession(IAgentSession session, MaieuticsSessionProfileProvider? profile)
    {
        public IAgentSession Session { get; } = session;

        public MaieuticsSessionProfileProvider? Profile { get; } = profile;

        public long Touched { get; private set; } = Environment.TickCount64;

        public void Touch()
        {
            Touched = Environment.TickCount64;
        }
    }
}
