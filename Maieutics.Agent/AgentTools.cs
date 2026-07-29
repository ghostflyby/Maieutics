using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>Identifies one Maieutics tool call.</summary>
public readonly record struct AgentToolCallId
{
    /// <summary>Initializes a tool call identifier.</summary>
    private AgentToolCallId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Agent tool call identifiers cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the underlying value.</summary>
    public Guid Value { get; }

    /// <summary>Creates a new identifier.</summary>
    public static AgentToolCallId Create() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

/// <summary>Provides invocation identity and progress reporting to an Agent tool.</summary>
public sealed class AgentToolContext
{
    private readonly Func<AIContent, CancellationToken, ValueTask> reportProgress;

    internal AgentToolContext(
        AgentSessionId sessionId,
        AgentRunId runId,
        AgentToolCallId callId,
        Func<AIContent, CancellationToken, ValueTask> reportProgress)
    {
        SessionId = sessionId;
        RunId = runId;
        CallId = callId;
        this.reportProgress = reportProgress;
    }

    /// <summary>Gets the owning session identifier.</summary>
    public AgentSessionId SessionId { get; }

    /// <summary>Gets the owning run identifier.</summary>
    public AgentRunId RunId { get; }

    /// <summary>Gets the tool call identifier.</summary>
    public AgentToolCallId CallId { get; }

    /// <summary>Gets the Maieutics invocation context attached to function arguments.</summary>
    public static AgentToolContext GetRequired(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Context?.TryGetValue(typeof(AgentToolContext), out var value) == true &&
            value is AgentToolContext context)
        {
            return context;
        }

        throw new InvalidOperationException("The AI function is not running inside a Maieutics Agent tool call.");
    }

    /// <summary>Reports one bounded progress item in call order.</summary>
    public ValueTask ReportProgressAsync(
        AIContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        AgentToolContent.Validate(content);
        return reportProgress(content, cancellationToken);
    }
}

/// <summary>Represents an expected tool failure that may be returned to the model.</summary>
public sealed class AgentToolException : Exception
{
    /// <summary>Initializes an expected tool failure.</summary>
    public AgentToolException(string code, string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
    }

    /// <summary>Gets the stable machine-readable failure code.</summary>
    public string Code { get; }
}

/// <summary>Reports that a model-requested tool started executing.</summary>
public sealed record AgentToolStarted : AgentEvent
{
    /// <summary>Initializes a tool-started event.</summary>
    public AgentToolStarted(
        AgentRunId runId,
        long sequence,
        AgentToolCallId callId,
        string toolName,
        JsonElement arguments) : base(runId, sequence)
    {
        if (callId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent tool call identifiers cannot be empty.", nameof(callId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(arguments));
        }

        CallId = callId;
        ToolName = toolName;
        Arguments = arguments.Clone();
    }

    /// <summary>Gets the Maieutics tool-call identifier.</summary>
    public AgentToolCallId CallId { get; }

    /// <summary>Gets the provider-visible function name.</summary>
    public string ToolName { get; }

    /// <summary>Gets detached untrusted model arguments.</summary>
    public JsonElement Arguments { get; }
}

/// <summary>Reports bounded progress from an executing tool.</summary>
/// <param name="RunId">The owning run.</param>
/// <param name="Sequence">The strictly increasing run-local sequence.</param>
/// <param name="CallId">The Maieutics tool call identifier.</param>
/// <param name="Content">The provider-neutral progress content.</param>
public sealed record AgentToolProgress(
    AgentRunId RunId,
    long Sequence,
    AgentToolCallId CallId,
    AIContent Content) : AgentEvent(RunId, Sequence);

/// <summary>Reports a successful, recoverable, or terminal tool completion.</summary>
public sealed record AgentToolFinished : AgentEvent
{
    /// <summary>Initializes a tool-finished event.</summary>
    public AgentToolFinished(
        AgentRunId runId,
        long sequence,
        AgentToolCallId callId,
        JsonElement result) : base(runId, sequence)
    {
        if (callId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent tool call identifiers cannot be empty.", nameof(callId));
        }

        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool result envelopes must be JSON objects.", nameof(result));
        }

        CallId = callId;
        Result = result.Clone();
    }

    /// <summary>Gets the Maieutics tool-call identifier.</summary>
    public AgentToolCallId CallId { get; }

    /// <summary>Gets the detached model-visible result envelope.</summary>
    public JsonElement Result { get; }
}

file static class AgentToolContent
{
    internal static void Validate(AIContent content)
    {
        if (content is TextContent)
        {
            return;
        }

        if (content is DataContent data &&
            string.Equals(data.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var _ = JsonDocument.Parse(data.Data);
                return;
            }
            catch (JsonException exception)
            {
                throw new ArgumentException("JSON tool content must contain valid UTF-8 JSON data.", nameof(content),
                    exception);
            }
        }

        throw new ArgumentException(
            "Tool content must be TextContent or application/json DataContent.",
            nameof(content));
    }
}