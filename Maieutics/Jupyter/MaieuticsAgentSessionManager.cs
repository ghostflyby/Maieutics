using Maieutics.Agent;

namespace Maieutics.Jupyter;

/// <summary>
///     Owns the kernel's active <see cref="IAgentSession" /> and implements the interface by
///     delegation, so notebook control cells can replace the session without re-resolving the
///     kernel application. Runs already started keep executing against the session they began
///     on; a swap only affects later turns. Recovery is manual only: nothing is restored until
///     a <c>%session resume</c> cell names a stored session.
/// </summary>
internal sealed class MaieuticsAgentSessionManager : IAgentSession
{
    private readonly IAgentRunProfileProvider profileProvider;
    private readonly IAgentTranscriptStore? transcriptStore;
    private readonly Lock gate = new();
    private IAgentSession current;

    public MaieuticsAgentSessionManager(
        IAgentRunProfileProvider profileProvider,
        IAgentTranscriptStore? transcriptStore)
    {
        this.profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        this.transcriptStore = transcriptStore;
        current = new AgentSession(profileProvider, transcriptStore: transcriptStore);
    }

    public bool PersistenceEnabled => transcriptStore is not null;

    public AgentSessionId Id => Current.Id;

    public Task<IAgentRun> StartTurnAsync(AgentTurn turn, CancellationToken cancellationToken = default)
    {
        return Current.StartTurnAsync(turn, cancellationToken);
    }

    public AgentTranscript GetTranscriptSnapshot()
    {
        return Current.GetTranscriptSnapshot();
    }

    /// <summary>Lists the stored sessions; empty when persistence is disabled.</summary>
    public IReadOnlyList<AgentSessionDescriptor> ListStoredSessions()
    {
        return transcriptStore?.ListSessions() ?? [];
    }

    /// <summary>Replaces the active session with the stored session. Resuming the already active
    /// identity is a no-op; in-flight runs continue against the session they started on.</summary>
    /// <exception cref="ArgumentException">Transcript persistence is disabled.</exception>
    /// <exception cref="AgentSessionNotFoundException">The store holds no such session.</exception>
    public AgentSessionId Resume(AgentSessionId sessionId)
    {
        if (transcriptStore is null)
        {
            throw new ArgumentException(
                "Transcript persistence is disabled; enable Maieutics:Agent:Persistence:Enabled to resume sessions.");
        }

        if (sessionId == Id) return sessionId;

        var restored = AgentSession.Resume(profileProvider, transcriptStore, sessionId);
        lock (gate)
        {
            current = restored;
        }

        return sessionId;
    }

    /// <summary>Replaces the active session with a fresh one. The fresh session keeps persisting
    /// to the store when persistence is enabled; the previous session's stored history remains.</summary>
    public AgentSessionId StartNew()
    {
        lock (gate)
        {
            current = new AgentSession(profileProvider, transcriptStore: transcriptStore);
            return current.Id;
        }
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
