using Maieutics.Agent;
using Maieutics.Persistence;

namespace Maieutics.Jupyter;

/// <summary>
///     Owns the kernel's active <see cref="IAgentSession" /> and implements the interface by
///     delegation, so notebook control cells can replace the session without re-resolving the
///     kernel application. Runs already started keep executing against the session they began
///     on; a swap only affects later turns.
///     With persistence enabled, every session belongs to one fork family and each family owns
///     exactly one <c>families/&lt;family-id&gt;/history.db</c>. Fork does not exist yet, so a
///     session's family id is its own id; when fork arrives, a child opens its root ancestor's
///     family file and this layout is unchanged. Family databases are opened lazily, cached,
///     and disposed with the manager. Recovery is manual only: nothing is restored until a
///     <c>%session resume</c> cell names a stored session.
/// </summary>
internal sealed class MaieuticsAgentSessionManager : IAgentSession, IDisposable
{
    private readonly IAgentRunProfileProvider profileProvider;
    private readonly string? familiesRoot;
    private readonly Func<AgentSessionId, IAgentTranscriptStore>? storeFactory;
    private readonly Lock gate = new();
    private readonly Dictionary<string, IAgentTranscriptStore> stores = new(StringComparer.Ordinal);
    private IAgentSession current;

    public MaieuticsAgentSessionManager(
        IAgentRunProfileProvider profileProvider,
        string? familiesRoot,
        Func<AgentSessionId, IAgentTranscriptStore>? storeFactory)
    {
        this.profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        this.familiesRoot = familiesRoot;
        this.storeFactory = storeFactory;
        current = new AgentSession(profileProvider, transcriptStore: OpenStore(AgentSessionId.Create()));
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
    /// first. Sessions that never committed a turn have no row and are not listed.</summary>
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
        var restored = AgentSession.Resume(profileProvider, RequiredStore(familyId), sessionId);
        lock (gate)
        {
            current = restored;
        }

        return sessionId;
    }

    /// <summary>Replaces the active session with a fresh one in its own family. The previous
    /// session's stored history remains.</summary>
    public AgentSessionId StartNew()
    {
        var sessionId = AgentSessionId.Create();
        lock (gate)
        {
            current = new AgentSession(profileProvider, transcriptStore: OpenStore(sessionId));
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

    private IAgentTranscriptStore? OpenStore(AgentSessionId familyId)
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

    private IAgentTranscriptStore RequiredStore(AgentSessionId familyId)
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
