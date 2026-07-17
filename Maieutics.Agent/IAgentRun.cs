namespace Maieutics.Agent;

/// <summary>Owns one running Agent turn and its event stream.</summary>
public interface IAgentRun : IAsyncDisposable
{
    /// <summary>Gets the run identifier.</summary>
    AgentRunId Id { get; }

    /// <summary>Gets the owning session identifier.</summary>
    AgentSessionId SessionId { get; }

    /// <summary>Gets the single-consumer ordered event stream.</summary>
    IAsyncEnumerable<AgentEvent> Events { get; }

    /// <summary>Gets the terminal result. Failures and cancellation are surfaced by this task.</summary>
    Task<AgentRunResult> Completion { get; }

    /// <summary>Requests cancellation and waits for the run to terminate.</summary>
    Task CancelAsync(CancellationToken cancellationToken = default);
}