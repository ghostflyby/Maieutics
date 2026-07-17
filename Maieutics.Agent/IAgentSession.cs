namespace Maieutics.Agent;

/// <summary>Owns authoritative conversation state and starts Agent runs.</summary>
public interface IAgentSession
{
    /// <summary>Gets the session identifier.</summary>
    AgentSessionId Id { get; }

    /// <summary>Starts a run and reserves the session before returning.</summary>
    Task<IAgentRun> StartTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default);

    /// <summary>Gets an immutable snapshot of committed history.</summary>
    AgentTranscript GetTranscriptSnapshot();
}