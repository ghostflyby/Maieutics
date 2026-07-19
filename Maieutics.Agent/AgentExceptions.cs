namespace Maieutics.Agent;

/// <summary>Base class for normalized Agent runtime failures.</summary>
public abstract class AgentException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Indicates that a session already owns an active run.</summary>
public sealed class AgentTurnInProgressException() : AgentException("An agent turn is already in progress.");

/// <summary>Indicates that submitted input exceeded the configured character limit.</summary>
public sealed class AgentInputLimitExceededException(int actualCharacters, int maximumCharacters)
    : AgentException(
        $"The agent input contains {actualCharacters} characters, exceeding the limit of {maximumCharacters}.")
{
    /// <summary>Gets the submitted UTF-16 character count.</summary>
    public int ActualCharacters { get; } = actualCharacters;

    /// <summary>Gets the configured maximum UTF-16 character count.</summary>
    public int MaximumCharacters { get; } = maximumCharacters;
}

/// <summary>Indicates that model output exceeded the configured character limit.</summary>
public sealed class AgentResponseLimitExceededException(int maximumCharacters)
    : AgentException($"The agent response exceeded the limit of {maximumCharacters} characters.")
{
    /// <summary>Gets the configured maximum UTF-16 character count.</summary>
    public int MaximumCharacters { get; } = maximumCharacters;
}

/// <summary>Wraps a non-cancellation provider or framework failure.</summary>
public sealed class AgentProviderException(Exception innerException)
    : AgentException("The model provider failed while producing a response.", innerException);

/// <summary>Indicates that the selected model profile lacks behavior required by the Agent run.</summary>
public sealed class AgentModelCapabilityException(
    AgentModelCapabilities requiredCapability,
    AgentModelIdentity? modelIdentity = null)
    : AgentException(CreateMessage(requiredCapability, modelIdentity))
{
    /// <summary>Gets the capability required by the run.</summary>
    public AgentModelCapabilities RequiredCapability { get; } = requiredCapability;

    /// <summary>Gets the selected model identity, when known.</summary>
    public AgentModelIdentity? ModelIdentity { get; } = modelIdentity;

    private static string CreateMessage(
        AgentModelCapabilities requiredCapability,
        AgentModelIdentity? modelIdentity)
    {
        var profile = modelIdentity is null
            ? "The selected model profile"
            : $"Agent model profile '{modelIdentity.ProfileId}'";
        return $"{profile} does not support the required capability '{requiredCapability}'.";
    }
}

/// <summary>Indicates that provider output cannot be represented by the current Agent contract.</summary>
public sealed class AgentUnsupportedResponseException(string message) : AgentException(message);

/// <summary>Indicates that a configured tool-runtime budget was exceeded.</summary>
public sealed class AgentToolLimitExceededException(string limitName, int maximum)
    : AgentException($"The Agent tool limit '{limitName}' exceeded its configured maximum of {maximum}.")
{
    /// <summary>Gets the exceeded option name.</summary>
    public string LimitName { get; } = limitName;

    /// <summary>Gets the configured maximum.</summary>
    public int Maximum { get; } = maximum;
}

/// <summary>Indicates that a model supplied an unknown or malformed tool request.</summary>
public sealed class AgentToolArgumentsException(string message, Exception? innerException = null)
    : AgentException(message, innerException);

/// <summary>Wraps an unexpected exception thrown by an Agent tool.</summary>
public sealed class AgentToolInvocationException(string toolName, Exception innerException)
    : AgentException($"The Agent tool '{toolName}' failed unexpectedly.", innerException)
{
    /// <summary>Gets the registered tool name.</summary>
    public string ToolName { get; } = toolName;
}

/// <summary>Indicates that a turn exhausted its model-request budget before a final answer.</summary>
public sealed class AgentModelIterationLimitExceededException(int maximumIterations)
    : AgentException($"The Agent model exceeded the limit of {maximumIterations} iterations in one turn.")
{
    /// <summary>Gets the configured maximum provider iteration count.</summary>
    public int MaximumIterations { get; } = maximumIterations;
}