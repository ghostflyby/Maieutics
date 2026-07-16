namespace Maieutics.Agent;

public abstract class AgentException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class AgentTurnInProgressException() : AgentException("An agent turn is already in progress.");

public sealed class AgentInputLimitExceededException(int actualCharacters, int maximumCharacters)
    : AgentException(
        $"The agent input contains {actualCharacters} characters, exceeding the limit of {maximumCharacters}.")
{
    public int ActualCharacters { get; } = actualCharacters;

    public int MaximumCharacters { get; } = maximumCharacters;
}

public sealed class AgentResponseLimitExceededException(int maximumCharacters)
    : AgentException($"The agent response exceeded the limit of {maximumCharacters} characters.")
{
    public int MaximumCharacters { get; } = maximumCharacters;
}

public sealed class AgentProviderException(Exception innerException)
    : AgentException("The model provider failed while producing a response.", innerException);

public sealed class AgentUnsupportedResponseException(string message) : AgentException(message);