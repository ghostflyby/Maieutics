namespace Maieutics.Agent;

public sealed record AgentTurn(string Input);

public enum AgentMessageRole
{
    User,
    Assistant
}

public sealed record AgentMessage(
    AgentMessageRole Role,
    string Text);

public abstract record AgentEvent;

public sealed record AgentTextDelta(string Text) : AgentEvent;

public sealed record AgentTurnCompleted(AgentMessage Assistant) : AgentEvent;