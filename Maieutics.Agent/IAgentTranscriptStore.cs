using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>
///     Persists committed Agent transcript turns so the canonical history survives process exit.
///     The store receives one call per committed turn on the session's transactional commit path;
///     a failing implementation aborts the commit and the turn rolls back like any other terminal
///     failure. Stores receive the canonical (private) turn content, including provider reasoning
///     retained for replay; exposing it publicly remains the transcript snapshot's responsibility.
/// </summary>
/// <remarks>
///     Implementations must be thread safe and durable before returning: the session updates its
///     in-memory state only after <see cref="AppendTurn" /> succeeds. All members are synchronous
///     by contract because they run on the short commit path; long-running work (blob publication,
///     compaction) belongs in separate maintenance APIs owned by the implementation.
/// </remarks>
public interface IAgentTranscriptStore
{
    /// <summary>Appends one complete committed turn, its object references, and advances the
    /// session's stored head. References feed reachability tracking for object garbage collection.</summary>
    /// <param name="sessionId">The session that committed the turn. The first append creates the session.</param>
    /// <param name="turn">The complete committed turn, from the submitted user message to the final assistant message.</param>
    /// <param name="objectReferences">The distinct object addresses referenced by the turn's content.</param>
    /// <exception cref="InvalidOperationException">The turn could not be committed durably.</exception>
    void AppendTurn(AgentSessionId sessionId, AgentTranscriptTurn turn, IReadOnlyList<string> objectReferences);

    /// <summary>Loads the committed transcript of one stored session.</summary>
    /// <param name="sessionId">The session to load.</param>
    /// <returns>The stored transcript, or <see langword="null" /> when the store holds no such session.</returns>
    /// <exception cref="InvalidOperationException">The stored transcript exists but cannot be reconstructed.</exception>
    AgentTranscript? LoadTranscript(AgentSessionId sessionId);

    /// <summary>Lists the stored sessions, most recently active first, for recovery and inspection.</summary>
    /// <returns>The session descriptors; ordering beyond recency is unspecified.</returns>
    IReadOnlyList<AgentSessionDescriptor> ListSessions();
}

/// <summary>Describes one persisted Agent session without its transcript content.</summary>
public sealed record AgentSessionDescriptor
{
    /// <summary>Initializes a session descriptor.</summary>
    public AgentSessionDescriptor(
        AgentSessionId id,
        DateTimeOffset createdAt,
        DateTimeOffset lastActivityAt,
        int turnCount,
        string? title = null,
        string? preview = null,
        string? workspaceRoot = null,
        AgentSessionId? parentSessionId = null,
        int? forkPointSeq = null)
    {
        Id = id;
        CreatedAt = createdAt;
        LastActivityAt = lastActivityAt;
        TurnCount = turnCount;
        Title = title;
        Preview = preview;
        WorkspaceRoot = workspaceRoot;
        ParentSessionId = parentSessionId;
        ForkPointSeq = forkPointSeq;
    }

    /// <summary>Gets the session identifier.</summary>
    public AgentSessionId Id { get; }

    /// <summary>Gets when the session stored its first turn.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets when the session stored its most recent turn.</summary>
    public DateTimeOffset LastActivityAt { get; }

    /// <summary>Gets the number of complete turns stored for the session.</summary>
    public int TurnCount { get; }

    /// <summary>Gets the user-set session title, or <see langword="null" /> when the session
    /// was never renamed. A session renamed before its first committed turn has a row with a
    /// title and zero turns.</summary>
    public string? Title { get; }

    /// <summary>Gets the first committed user message text, truncated for display, or
    /// <see langword="null" /> when no turn was committed.</summary>
    public string? Preview { get; }

    /// <summary>Gets the workspace root the session's owning process was launched with, stamped
    /// when the row was created, or <see langword="null" /> for rows written before the column
    /// existed.</summary>
    public string? WorkspaceRoot { get; }

    /// <summary>Gets the session this one forked from, or <see langword="null" /> for a root
    /// session. The fork's visible history is this parent's first
    /// <see cref="ForkPointSeq" /> turns followed by the fork's own turns.</summary>
    public AgentSessionId? ParentSessionId { get; }

    /// <summary>Gets how many turns of <see cref="ParentSessionId" /> the fork keeps as its
    /// history prefix, or <see langword="null" /> for a root session. Zero means the fork
    /// starts from an empty history.</summary>
    public int? ForkPointSeq { get; }
}
