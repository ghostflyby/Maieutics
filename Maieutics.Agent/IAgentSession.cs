namespace Maieutics.Agent;

public interface IAgentSession
{
    IAsyncEnumerable<AgentEvent> ExecuteTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default);

    IReadOnlyList<AgentMessage> GetHistorySnapshot();
}