using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Persistence;
using Maieutics.Skills;
using Microsoft.Extensions.Logging;

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
internal sealed class MaieuticsAgentSessionManager : IAgentSession, IDisposable, IAsyncDisposable
{
    /// <summary>Bounded live set: the registry keeps at most this many sessions, evicting the
    /// least recently used ones that can still be lazily re-resumed from storage (zero-turn
    /// and persistence-less sessions are never evicted, since their state exists nowhere
    /// else). Eviction only drops the registry entry — in-flight runs keep their session.</summary>
    internal const int LiveSessionCapacity = 8;

    private readonly IAgentRunProfileProvider _profileProvider;
    private readonly IMaieuticsRuntimeConfiguration? _runtimeConfiguration;
    private readonly string? _familiesRoot;
    private readonly Func<AgentSessionId, SqliteTranscriptStore>? _storeFactory;
    private readonly IAgentObjectStore? _objectStore;
    private readonly IObjectReclaimer? _reclaimer;
    private readonly string? _viewSessionsRoot;
    private readonly string? _objectsRoot;
    private readonly ILogger<MaieuticsAgentSessionManager> _logger;
    private readonly AgentSubagentOptions? _subagents;
    private readonly SkillCatalog? _skillCatalog;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LiveSession> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqliteTranscriptStore> _stores = new(StringComparer.Ordinal);
    private IAgentSession _foreground;
    private readonly Dictionary<AgentSessionId, SessionTitleState> _volatileTitles = [];

    public MaieuticsAgentSessionManager(
        IAgentRunProfileProvider profileProvider,
        string? familiesRoot,
        Func<AgentSessionId, SqliteTranscriptStore>? storeFactory,
        ILogger<MaieuticsAgentSessionManager> logger,
        IAgentObjectStore? objectStore = null,
        IObjectReclaimer? reclaimer = null,
        string? viewSessionsRoot = null,
        string? objectsRoot = null,
        IMaieuticsRuntimeConfiguration? runtimeConfiguration = null,
        AgentSubagentOptions? subagents = null,
        SkillCatalog? skillCatalog = null,
        string titleMode = "manual")
    {
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        _runtimeConfiguration = runtimeConfiguration;
        _familiesRoot = familiesRoot;
        _storeFactory = storeFactory;
        _objectStore = objectStore;
        _reclaimer = reclaimer;
        _viewSessionsRoot = viewSessionsRoot;
        _objectsRoot = objectsRoot;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _subagents = subagents;
        _skillCatalog = skillCatalog;
        Titles = new SessionTitleService(this, titleMode, logger);
        _foreground = CreateLive(AgentSessionId.Create());
    }

    internal SessionTitleService Titles { get; }

    public bool PersistenceEnabled => _storeFactory is not null;

    /// <summary>Gets how many sessions are currently live. Exposed for diagnostics.</summary>
    public int LiveCount
    {
        get
        {
            lock (_gate)
            {
                return _live.Count;
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
        lock (_gate)
        {
            return _live.ContainsKey(sessionId.Value.ToString("N"));
        }
    }

    /// <summary>Enumerates the identities of every currently live session. Used by the task
    /// plane's catalog, which lists live subagent runs across all sessions of the process
    /// (ADR 0028 decision 5).</summary>
    internal AgentSessionId[] ListLiveSessionIds()
    {
        lock (_gate)
        {
            return [.. _live.Keys.Select(static key => new AgentSessionId(Guid.ParseExact(key, "N")))];
        }
    }

    /// <summary>Resolves one live session, lazily resuming a stored one. Addressing a session
    /// never moves the foreground. Unknown sessions (or persistence disabled with no live
    /// match) fail typed.</summary>
    public IAgentSession Resolve(AgentSessionId sessionId)
    {
        var key = sessionId.Value.ToString("N");
        lock (_gate)
        {
            if (_live.TryGetValue(key, out var existing))
            {
                existing.Touch();
                return existing.Session;
            }
        }

        if (_storeFactory is null)
        {
            throw new AgentSessionNotFoundException(sessionId);
        }

        // Resume outside the gate: opens family databases and loads transcripts.
        var familyId = ResolveFamily(sessionId) ?? throw new AgentSessionNotFoundException(sessionId);
        var wrapper = CreateWrapper();
        IAgentSession restored = AgentSession.Resume(
            ProviderFor(wrapper, sessionId),
            RequiredStore(familyId),
            sessionId,
            objectStore: _objectStore);

        restored = new TitledAgentSession(restored, this);
        lock (_gate)
        {
            if (_live.TryGetValue(key, out var raced))
            {
                raced.Touch();
                return raced.Session;
            }

            var entry = new LiveSession(restored, wrapper);
            _live[key] = entry;
            entry.Touch();
        }

        _logger.LogDebug("Resolved session {SessionId} from stored history.", sessionId);
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
        if (_storeFactory is null)
        {
            lock (_gate)
            {
                if (_live.TryGetValue(sessionId.Value.ToString("N"), out var existing))
                {
                    MoveForegroundLocked(existing.Session);
                    _logger.LogDebug("Resumed session {SessionId} as the foreground.", sessionId);
                    return sessionId;
                }
            }

            throw new ArgumentException(
                "Transcript persistence is disabled; enable Maieutics:Agent:Persistence:Enabled to resume sessions.");
        }

        MoveForeground(Resolve(sessionId));
        _logger.LogDebug("Resumed session {SessionId} as the foreground.", sessionId);
        return sessionId;
    }

    /// <summary>Replaces the foreground session with a fresh one. The previous session stays
    /// live until evicted; its stored history remains.</summary>
    public AgentSessionId StartNew()
    {
        var sessionId = AgentSessionId.Create();
        var wrapper = CreateWrapper();
        IAgentSession session = new AgentSession(
            ProviderFor(wrapper, sessionId),
            transcriptStore: OpenStore(sessionId),
            objectStore: _objectStore,
            sessionId: sessionId);
        session = new TitledAgentSession(session, this);
        lock (_gate)
        {
            _live[sessionId.Value.ToString("N")] = new LiveSession(session, wrapper);
            MoveForegroundLocked(session);
        }

        _logger.LogDebug("Created session {SessionId}.", sessionId);
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
        if (_storeFactory is null)
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
        store.CreateForkSession(forkId, sourceId, forkPointSeq, effectiveTitle, manualTitle: title is not null);
        var wrapper = CreateWrapper();
        IAgentSession forked = AgentSession.Fork(
            ProviderFor(wrapper, forkId),
            store,
            sourceId,
            forkId,
            forkPointSeq,
            objectStore: _objectStore);
        var inherited = GetTitleState(sourceId);
        var forkTitle = store.GetTitleState(forkId);
        store.TrySetTitleState(forkId, forkTitle, forkTitle with { Mode = Titles.Mode(inherited),
            Source = title is null ? "derived" : "manual", Initial = title is null ? "eligible" : "settled" });
        forked = new TitledAgentSession(forked, this);
        lock (_gate)
        {
            _live[forkId.Value.ToString("N")] = new LiveSession(forked, wrapper);
            MoveForegroundLocked(forked);
        }

        _logger.LogDebug(
            "Forked session {SourceSessionId} to {SessionId} at turn {ForkPoint}.",
            sourceId,
            forkId,
            forkPointSeq);
        EnforceLiveCapacity(forkId);
        return forkId;
    }

    /// <summary>Sets the addressed session's model-profile override, so its next turn runs on
    /// that profile regardless of the process selection. The id resolves exactly like the
    /// process-level selection: a configured profile id, a model name matching exactly one
    /// configured profile, or an automatic <c>@source/model</c> selector (registered at resolve
    /// time, so it keeps resolving after the discovery cache expires). Clears the override when
    /// <paramref name="profileId" /> is <see langword="null" />. A stored-but-not-live session is
    /// lazily resumed first.</summary>
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

        if (_runtimeConfiguration is not { } runtime)
            throw new ArgumentException("Model profiles are not available in this host.");

        wrapper.SetOverride(runtime.PrepareProfileOverride(profileId));
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
        if (_storeFactory is null || _familiesRoot is null || !Directory.Exists(_familiesRoot)) return [];

        var descriptors = new List<AgentSessionDescriptor>();
        foreach (var directory in Directory.EnumerateDirectories(_familiesRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var familyId)) continue;

            descriptors.AddRange(RequiredStore(new AgentSessionId(familyId)).ListSessions());
        }

        return
        [
            .. descriptors
                .OrderByDescending(session => session.LastActivityAt),
        ];
    }

    /// <summary>Sets or clears a manual title. With persistence, an uncommitted session gets
    /// a zero-turn row; otherwise metadata lives with the live session. Whitespace-only
    /// titles release the lock and reset initial eligibility; manual titles are capped at 200 characters.</summary>
    /// <returns>The stored (normalized) title.</returns>
    /// <exception cref="ArgumentException">The title exceeds the length cap.</exception>
    public string? SetTitle(AgentSessionId sessionId, string? title)
    {
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

        if (_storeFactory is null)
        {
            _ = Resolve(sessionId);
            lock (_gate)
            {
                var before = _volatileTitles.GetValueOrDefault(sessionId) ?? new SessionTitleState(null, false, 0);
                _volatileTitles[sessionId] = before with { Title = normalized, Manual = normalized is not null,
                    Revision = before.Revision + 1, ControlRevision = before.ControlRevision + 1,
                    Initial = normalized is null ? "eligible" : "settled", Source = normalized is null ? "preview" : "manual" };
            }
            Titles.Changed(sessionId);
            return normalized;
        }

        // A session with no family database yet is its own family root; naming it creates the
        // database and its zero-turn row.
        var familyId = ResolveFamily(sessionId) ?? sessionId;
        RequiredStore(familyId).SetTitle(sessionId, normalized);
        Titles.Changed(sessionId);
        return normalized;
    }

    /// <summary>Gets metadata for a live or stored session without changing its transcript.</summary>
    internal SessionTitleState GetTitleState(AgentSessionId sessionId)
    {
        if (_storeFactory is not null)
        {
            var family = ResolveFamily(sessionId);
            return family is { } id ? RequiredStore(id).GetTitleState(sessionId) : new SessionTitleState(null, false, 0);
        }
        lock (_gate) return _volatileTitles.GetValueOrDefault(sessionId) ?? new SessionTitleState(null, false, 0);
    }

    /// <summary>Atomically applies metadata without changing transcript state.</summary>
    internal bool TrySetTitleState(AgentSessionId id, SessionTitleState expected, SessionTitleState next)
    {
        bool changed;
        if (_storeFactory is not null)
        {
            var family = ResolveFamily(id) ?? id;
            var store = RequiredStore(family);
            changed = store.TrySetTitleState(id, expected, next);
        }
        else
        {
            lock (_gate)
            {
                var current = _volatileTitles.GetValueOrDefault(id) ?? new SessionTitleState(null, false, 0);
                changed = current == expected;
                if (changed) _volatileTitles[id] = next;
            }
        }
        if (changed) Titles.Changed(id);
        return changed;
    }

    /// <summary>Gets the concrete runtime for the product's subagent composition.</summary>
    internal AgentSession? RuntimeSession(AgentSessionId id) => Resolve(id) switch
    {
        TitledAgentSession managed => managed.Inner as AgentSession,
        AgentSession raw => raw,
        _ => null
    };

    /// <summary>Acquires the addressed session's selected model for a metadata-only request.</summary>
    internal Task<IAgentRunProfileLease> AcquireTitleProfileAsync(AgentSessionId sessionId, CancellationToken cancellationToken)
    {
        _ = Resolve(sessionId);
        return SelectTitleProfileProvider(sessionId).AcquireAsync(cancellationToken);
    }

    /// <summary>Selects the profile after resolution. The resolved entry may have been
    /// LRU-evicted before this gate is entered; eviction drops its override, so use the default.</summary>
    internal IAgentRunProfileProvider SelectTitleProfileProvider(AgentSessionId sessionId)
    {
        lock (_gate)
            return _live.TryGetValue(sessionId.Value.ToString("N"), out var entry)
                ? entry.Profile ?? _profileProvider
                : _profileProvider;
    }

    /// <summary>Finds one stored session's descriptor across family databases, or
    /// <see langword="null" /> when the session has no stored row.</summary>
    public AgentSessionDescriptor? FindDescriptor(AgentSessionId sessionId)
    {
        if (_storeFactory is null || _familiesRoot is null || !Directory.Exists(_familiesRoot)) return null;

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
        if (_storeFactory is null) return null;

        var familyId = ResolveFamily(sessionId);
        if (familyId is not { } resolved) return null;

        return RequiredStore(resolved).LoadTranscript(sessionId);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await Titles.DisposeAsync().ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var store in _stores.Values)
            {
                if (store is IDisposable disposable) disposable.Dispose();
            }

            _stores.Clear();
        }
    }

    /// <summary>Ingests bytes into the content-addressed object store (the attachment upload
    /// path). Identical bytes deduplicate by address. Null when this host has no object store
    /// (Agent persistence disabled), which the frontend surfaces as a typed failure.</summary>
    public AgentObjectDescriptor? IngestObject(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return _objectStore?.Ingest(content);
    }

    /// <summary>Describes a published object without opening it; null when the store is absent
    /// or the address is not present. The turn build path resolves attachment markers against
    /// this so a pruned or never-ingested address fails typed instead of entering a run.</summary>
    public AgentObjectDescriptor? DescribeObject(string sha256)
    {
        return _objectStore is ObjectStore store ? store.Describe(sha256) : null;
    }

    /// <summary>Maintenance pass: deletes stored objects that no committed turn references and
    /// that were last written before the grace cutoff. Returns the number removed.</summary>
    /// <exception cref="ArgumentException">Transcript persistence is disabled.</exception>
    public int PruneObjects(TimeSpan gracePeriod)
    {
        if (_reclaimer is null || _storeFactory is null)
            throw new ArgumentException(
                "Transcript persistence is disabled; enable Maieutics:Agent:Persistence:Enabled to reclaim objects.");

        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        if (_familiesRoot is not null && Directory.Exists(_familiesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(_familiesRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var familyId)) continue;

                liveIds.UnionWith(RequiredStore(new AgentSessionId(familyId)).GetReferencedObjectIds());
            }
        }

        return _reclaimer.DeleteExcept(liveIds, DateTimeOffset.UtcNow - gracePeriod);
    }

    /// <summary>Maintenance pass: rebuilds the derived inspection view (one relative link per
    /// referenced object per session) from the canonical databases. Best effort — platforms
    /// without symlink support simply keep an empty view. Returns the number of live links.</summary>
    /// <exception cref="ArgumentException">Transcript persistence is disabled.</exception>
    public int RepairObjectView()
    {
        if (_storeFactory is null || _familiesRoot is null || _viewSessionsRoot is null || _objectsRoot is null)
            throw new ArgumentException(
                "Transcript persistence is disabled; enable Maieutics:Agent:Persistence:Enabled to build the object view.");

        var familyStores = new List<SqliteTranscriptStore>();
        if (Directory.Exists(_familiesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(_familiesRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var familyId)) continue;

                familyStores.Add(RequiredStore(new AgentSessionId(familyId)));
            }
        }

        return AgentObjectView.Repair(_viewSessionsRoot, _objectsRoot, familyStores);
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

        var oneLine = string.Join(' ', display.Split(['\r', '\n'],
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
            lock (_gate)
            {
                return (_foreground.Id, _foreground.GetTranscriptSnapshot().Turns.Length);
            }
        }
    }

    private IAgentSession Foreground
    {
        get
        {
            lock (_gate)
            {
                return _foreground;
            }
        }
    }

    private void MoveForeground(IAgentSession session)
    {
        lock (_gate)
        {
            MoveForegroundLocked(session);
        }
    }

    private void MoveForegroundLocked(IAgentSession session)
    {
        _foreground = session;
    }

    /// <summary>Creates the per-session profile wrapper, or returns
    /// <see langword="null" /> when no runtime configuration backs per-session overrides.</summary>
    private MaieuticsSessionProfileProvider? CreateWrapper()
    {
        return _runtimeConfiguration is null ? null : new MaieuticsSessionProfileProvider(_runtimeConfiguration);
    }

    private IAgentRunProfileProvider ProviderFor(MaieuticsSessionProfileProvider? wrapper, AgentSessionId sessionId)
    {
        var inner = wrapper ?? _profileProvider;
        var composed = _subagents is null ? inner : new SubagentsProfileProvider(inner, _subagents);
        // The skills decorator is outermost so both record rewrites (`with`) compose: the
        // catalog rides every lease regardless of the per-session override path.
        var skilled = _skillCatalog is null ? composed : new SkillsProfileProvider(composed, _skillCatalog);
        return new TitleProfileProvider(skilled, Titles, sessionId);
    }

    /// <summary>Decorates every lease with the composition-root subagent configuration while
    /// keeping the profile's own client, tools, and per-profile limits. When the configuration
    /// is disabled (null) the profile passes through untouched.</summary>
    private sealed class SubagentsProfileProvider(
        IAgentRunProfileProvider inner,
        AgentSubagentOptions subagents) : IAgentRunProfileProvider
    {
        public async Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            var lease = await inner.AcquireAsync(cancellationToken).ConfigureAwait(false) ??
                        throw new InvalidOperationException("The Agent run profile provider returned a null lease.");
            var profile = lease.Profile;
            var adjusted = new AgentRunProfile(
                profile.ChatClient,
                profile.Options with { Subagents = subagents },
                profile.ModelIdentity,
                profile.Capabilities,
                profile.HostedCapabilities,
                profile.Tools,
                profile.HostedTools);
            return new Lease(lease, adjusted);
        }

        private sealed class Lease(IAgentRunProfileLease inner, AgentRunProfile profile) : IAgentRunProfileLease
        {
            public AgentRunProfile Profile { get; } = profile;

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    /// <summary>Creates and registers a fresh live session (its own family database), making
    /// it the foreground. Used for the boot session.</summary>
    private IAgentSession CreateLive(AgentSessionId sessionId)
    {
        var wrapper = CreateWrapper();
        IAgentSession session = new AgentSession(
            ProviderFor(wrapper, sessionId),
            transcriptStore: OpenStore(sessionId),
            objectStore: _objectStore,
            sessionId: sessionId);
        session = new TitledAgentSession(session, this);
        lock (_gate)
        {
            _live[sessionId.Value.ToString("N")] = new LiveSession(session, wrapper);
            MoveForegroundLocked(session);
        }

        return session;
    }

    /// <summary>Requires the addressed session's profile wrapper, lazily resuming a stored
    /// session so addressed <c>%model</c> commands work on a freshly reopened notebook.</summary>
    private MaieuticsSessionProfileProvider RequireWrapper(AgentSessionId sessionId)
    {
        if (_runtimeConfiguration is null)
        {
            throw new ArgumentException("Model profile selection is not available in this host.");
        }

        Resolve(sessionId);

        lock (_gate)
        {
            if (_live.TryGetValue(sessionId.Value.ToString("N"), out var entry) && entry.Profile is { } profile)
            {
                return profile;
            }
        }

        throw new ArgumentException("The session's profile provider is not configured.");
    }

    /// <summary>Acquires a cooperative eviction-pin lease on a live session. A pinned
    /// session is skipped by <see cref="EnforceLiveCapacity" /> (both at candidacy and at
    /// the removal re-check), so a session with active queued turn work (ADR 0025) is not
    /// evicted while it is active interest. Eviction stays survivable through lazy-resume,
    /// so the lease is capacity management, not a lifetime guarantee. Returns
    /// <see langword="null" /> for an unknown session id. Disposing the lease releases the
    /// pin exactly once, harmlessly, even after an eviction or disposal.</summary>
    internal IDisposable? TryPinSession(AgentSessionId sessionId)
    {
        var key = sessionId.Value.ToString("N");
        lock (_gate)
        {
            if (!_live.TryGetValue(key, out var entry)) return null;

            entry.AddPin();
        }

        _logger.LogDebug("Pinned session {SessionId} against eviction.", sessionId);
        return new SessionPin(this, key);
    }

    /// <summary>Whether a live session currently holds at least one eviction-pin lease,
    /// so tests and diagnostics can observe pin lifetime across queue drain and
    /// disposal. An unknown or unpinned session is not pinned.</summary>
    internal bool IsPinned(AgentSessionId sessionId)
    {
        lock (_gate)
        {
            return _live.TryGetValue(sessionId.Value.ToString("N"), out var entry) && entry.IsPinned;
        }
    }

    private void ReleasePin(string key)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(key, out var entry)) entry.RemovePin();

            // An evicted or replaced entry has no pin left to release; the lease is
            // cooperative and never resurrects a session.
        }

        _logger.LogDebug("Released the eviction pin for session {SessionId}.", key);
    }

    /// <summary>Keeps the live set bounded, evicting the least recently used sessions that
    /// can still be lazily re-resumed from storage. Eviction drops the session's profile
    /// override with it (overrides are not durable; a re-resume follows the process
    /// selection). A session with a run in flight is never
    /// evicted (evicting it would let the next addressed turn build a second instance for
    /// the same identity and break the per-instance single-run gate), a pinned session is
    /// skipped (see <see cref="TryPinSession" />), the newly activated
    /// session is protected, and a candidate that was touched after candidacy is skipped.
    /// Runs outside the gate because resumability checks open family databases.</summary>
    private void EnforceLiveCapacity(AgentSessionId? protectedId = null)
    {
        List<(string Key, long Touched, AgentSessionId Id)> candidates;
        List<(AgentSessionId Id, string Reason)> skipped;
        lock (_gate)
        {
            if (_live.Count <= LiveSessionCapacity) return;

            candidates = [];
            skipped = [];
            foreach (var entry in _live.Values)
            {
                var skipReason =
                    ReferenceEquals(entry.Session, _foreground) ? "foreground" :
                    entry.Session.IsRunInProgress ? "run-in-flight" :
                    entry.IsPinned ? "pinned" :
                    protectedId is not null && entry.Session.Id == protectedId.Value ? "protected" :
                    null;
                if (skipReason is { } reason)
                {
                    skipped.Add((entry.Session.Id, reason));
                }
                else
                {
                    candidates.Add((entry.Session.Id.Value.ToString("N"), entry.Touched, entry.Session.Id));
                }
            }

            // OrderBy is stable like the LINQ pipeline this replaces; List<T>.Sort is not,
            // and eviction order must not reshuffle when Touched ticks tie.
            candidates = [.. candidates.OrderBy(entry => entry.Touched)];
        }

        foreach (var (id, reason) in skipped)
        {
            _logger.LogDebug("Eviction skipped session {SessionId}: {Reason}.", id, reason);
        }

        foreach (var (key, touched, id) in candidates)
        {
            var family = ResolveFamily(id);
            if (family is null || !RequiredStore(family.Value).ContainsSession(id)) continue;

            var evicted = false;
            string? recheckSkipReason = null;
            lock (_gate)
            {
                if (_live.Count <= LiveSessionCapacity) return;
                // Still the stale entry we evaluated: not foreground or protected,
                // not pinned, not touched since candidacy, and with no run in flight.
                if (_live.TryGetValue(key, out var entry) &&
                    entry.Touched == touched &&
                    !ReferenceEquals(entry.Session, _foreground) &&
                    !entry.Session.IsRunInProgress &&
                    !entry.IsPinned &&
                    (protectedId is null || entry.Session.Id != protectedId.Value))
                {
                    _live.Remove(key);
                    evicted = true;
                }
                else if (_live.TryGetValue(key, out var current))
                {
                    recheckSkipReason =
                        current.Touched != touched ? "touched-since-candidacy" :
                        ReferenceEquals(current.Session, _foreground) ? "foreground" :
                        current.Session.IsRunInProgress ? "run-in-flight" :
                        current.IsPinned ? "pinned" :
                        "protected";
                }
            }

            if (evicted)
            {
                _logger.LogInformation(
                    "Evicted session {SessionId} from the live set (last touched at {TouchedAt}).",
                    id,
                    touched);
            }
            else if (recheckSkipReason is { } reason)
            {
                _logger.LogDebug("Eviction skipped session {SessionId}: {Reason}.", id, reason);
            }
        }
    }

    private SqliteTranscriptStore? OpenStore(AgentSessionId familyId)
    {
        if (_storeFactory is null) return null;

        lock (_gate)
        {
            if (_stores.TryGetValue(familyId.Value.ToString("N"), out var existing)) return existing;

            var store = _storeFactory(familyId);
            _stores[familyId.Value.ToString("N")] = store;
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
        if (_familiesRoot is null) return null;
        if (File.Exists(SqliteTranscriptStore.FamilyDatabasePath(_familiesRoot, sessionId))) return sessionId;

        if (Directory.Exists(_familiesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(_familiesRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var familyId)) continue;

                if (RequiredStore(new AgentSessionId(familyId)).ContainsSession(sessionId))
                {
                    return new AgentSessionId(familyId);
                }
            }
        }

        return null;
    }

    private sealed class LiveSession(IAgentSession session, MaieuticsSessionProfileProvider? profile)
    {
        private int _pins;

        public IAgentSession Session { get; } = session;

        public MaieuticsSessionProfileProvider? Profile { get; } = profile;

        public long Touched { get; private set; } = Environment.TickCount64;

        /// <summary>Whether an eviction-pin lease is held. Read and written under the
        /// manager gate.</summary>
        public bool IsPinned => _pins > 0;

        public void Touch()
        {
            Touched = Environment.TickCount64;
        }

        public void AddPin()
        {
            _pins++;
        }

        public void RemovePin()
        {
            _pins--;
        }
    }

    /// <summary>One eviction-pin lease; disposal releases the pin exactly once.</summary>
    private sealed class SessionPin(MaieuticsAgentSessionManager owner, string key) : IDisposable
    {
        private int _disposeState;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;

            owner.ReleasePin(key);
        }
    }
}
