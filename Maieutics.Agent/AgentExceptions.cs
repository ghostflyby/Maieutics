namespace Maieutics.Agent;

/// <summary>Base class for normalized Agent runtime failures.</summary>
public abstract class AgentException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Indicates that a session already owns an active run.</summary>
public sealed class AgentTurnInProgressException() : AgentException("An agent turn is already in progress.");

/// <summary>Indicates that a run already completed, so it cannot accept further events; a tool
/// still reporting progress observes this typed failure instead of a channel error.</summary>
public sealed class AgentRunCompletedException(Exception? innerException = null)
    : AgentException("The agent run has already completed and cannot accept further events.", innerException);

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

/// <summary>Wraps a non-cancellation model-provider failure.</summary>
public sealed class AgentProviderException : AgentException
{
    /// <summary>Wraps an unclassified failure without exposing its original message.</summary>
    public AgentProviderException(Exception innerException)
        : this(AgentProviderFailureKind.Unknown, innerException) { }

    /// <summary>Wraps a provider failure with normalized, safe diagnostic metadata.</summary>
    /// <param name="kind">The provider-neutral failure category.</param>
    /// <param name="innerException">The original failure, for local diagnostics only.</param>
    /// <param name="statusCode">The upstream HTTP failure status, when available.</param>
    /// <param name="providerCode">A provider adapter's allowlisted error code, never a raw response message.</param>
    public AgentProviderException(AgentProviderFailureKind kind, Exception innerException,
        int? statusCode = null, string? providerCode = null)
        : base(CreateMessage(kind, statusCode, providerCode), innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        ProviderCode = providerCode;
    }

    /// <summary>Gets the provider-neutral failure category.</summary>
    public AgentProviderFailureKind Kind { get; }
    /// <summary>Gets the upstream HTTP failure status, when available.</summary>
    public int? StatusCode { get; }
    /// <summary>Gets the adapter's recognized provider error code, when available.</summary>
    public string? ProviderCode { get; }

    private static string CreateMessage(AgentProviderFailureKind kind, int? statusCode, string? providerCode)
    {
        var message = kind switch
        {
            AgentProviderFailureKind.Authentication => "The model provider rejected the credentials. Check the API key.",
            AgentProviderFailureKind.PermissionDenied => "The model provider denied access. Check the account's model and project permissions.",
            AgentProviderFailureKind.InvalidRequest => "The model provider rejected the request. Check the model settings and supported input or tools.",
            AgentProviderFailureKind.NotFound => "The requested model or provider endpoint was not found. Check the model name and endpoint.",
            AgentProviderFailureKind.ContextLimit => "The request exceeds the model's context window. Reduce the conversation or input size.",
            AgentProviderFailureKind.RateLimited => "The model provider's rate limit was reached. Wait before retrying or reduce request volume.",
            AgentProviderFailureKind.QuotaExceeded => "The model provider account has insufficient quota or credits. Check billing and usage limits.",
            AgentProviderFailureKind.Timeout => "The model provider request timed out. Retry or check the provider's availability.",
            AgentProviderFailureKind.Connection => "The connection to the model provider failed. Check the endpoint, network, proxy, and TLS configuration.",
            AgentProviderFailureKind.Unavailable => "The model provider is unavailable or overloaded. Retry later.",
            AgentProviderFailureKind.InvalidResponse => "The model provider returned an invalid or incomplete response. Check endpoint compatibility or retry.",
            _ => "The model provider failed while producing a response.",
        };
        var details = new List<string>(2);
        if (statusCode is { } status) details.Add($"HTTP {status}");
        if (providerCode is { } code) details.Add(code);
        return details.Count == 0 ? message : $"{message} ({string.Join("; ", details)})";
    }
}

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

/// <summary>Indicates that Agent content is incompatible with the canonical transcript codec.</summary>
public sealed class AgentContentCompatibilityException(string contentType, Exception innerException)
    : AgentException(
        $"Agent content of type '{contentType}' cannot be stored in the canonical transcript.",
        innerException)
{
    /// <summary>Gets the incompatible runtime content type name.</summary>
    public string ContentType { get; } = contentType;
}

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

/// <summary>Indicates that a requested stored session does not exist in the transcript store.</summary>
public sealed class AgentSessionNotFoundException(AgentSessionId sessionId)
    : AgentException($"No stored agent session matches '{sessionId.Value:N}'.");

/// <summary>Indicates that a turn exhausted its wall-clock budget before a final answer.</summary>
public sealed class AgentTurnDurationExceededException(TimeSpan maximumDuration)
    : AgentException($"The Agent turn exceeded the configured duration limit of {maximumDuration}.")
{
    /// <summary>Gets the configured maximum turn duration.</summary>
    public TimeSpan MaximumDuration { get; } = maximumDuration;
}

/// <summary>Indicates that a parent run exhausted one of its configured subagent budgets.</summary>
public sealed class AgentSubagentBudgetExceededException(string limitName, int maximum)
    : AgentException($"The Agent subagent budget '{limitName}' exceeded its configured maximum of {maximum}.")
{
    /// <summary>Gets the exceeded option name.</summary>
    public string LimitName { get; } = limitName;

    /// <summary>Gets the configured maximum.</summary>
    public int Maximum { get; } = maximum;
}

/// <summary>Indicates that a run identifier does not match any child of the addressed parent run.</summary>
public sealed class AgentSubagentNotFoundException(AgentRunId childRunId)
    : AgentException($"No subagent child run matches '{childRunId.Value:N}' on this parent run.")
{
    /// <summary>Gets the unmatched child run identifier.</summary>
    public AgentRunId ChildRunId { get; } = childRunId;
}

/// <summary>Indicates that a subagent wait gave up before the child run reached a terminal state.
/// The child run keeps running; the waiter may retry, cancel it, or let the parent turn join it.</summary>
public sealed class AgentSubagentWaitTimeoutException(AgentRunId childRunId, TimeSpan timeout, Exception? innerException = null)
    : AgentException($"The subagent child run '{childRunId.Value:N}' did not reach a terminal state within {timeout}.", innerException)
{
    /// <summary>Gets the child run identifier that was waited on.</summary>
    public AgentRunId ChildRunId { get; } = childRunId;

    /// <summary>Gets the configured wait timeout.</summary>
    public TimeSpan Timeout { get; } = timeout;
}
