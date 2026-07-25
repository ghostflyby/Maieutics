using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
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

/// <summary>Describes a provider-neutral Agent tool.</summary>
public sealed partial record AgentToolDescriptor
{
    /// <summary>Initializes a tool descriptor.</summary>
    public AgentToolDescriptor(string name, string description, JsonElement inputSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        if (!ToolNamePattern().IsMatch(name))
        {
            throw new ArgumentException(
                "Tool names must contain 1 to 64 ASCII letters, digits, underscores, or hyphens.",
                nameof(name));
        }

        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A tool input schema must be a JSON object.", nameof(inputSchema));
        }

        Name = name;
        Description = description;
        InputSchema = inputSchema.Clone();
    }

    /// <summary>Gets the provider-visible tool name.</summary>
    public string Name { get; }

    /// <summary>Gets the provider-visible tool description.</summary>
    public string Description { get; }

    /// <summary>Gets the JSON Schema object describing accepted arguments.</summary>
    public JsonElement InputSchema { get; }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();
}

/// <summary>Represents cloned JSON object arguments supplied by a model.</summary>
public sealed record AgentToolArguments
{
    /// <summary>Initializes tool arguments from a JSON object.</summary>
    public AgentToolArguments(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(value));
        }

        Value = value.Clone();
    }

    /// <summary>Gets the cloned JSON object.</summary>
    public JsonElement Value { get; }

    /// <summary>Deserializes the arguments using source-generated JSON metadata.</summary>
    public T Deserialize<T>(JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return Value.Deserialize(jsonTypeInfo)
               ?? throw new JsonException($"Tool arguments could not be deserialized as {typeof(T).Name}.");
    }

    internal int GetUtf8Size() => Encoding.UTF8.GetByteCount(Value.GetRawText());
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

/// <summary>Defines one provider-neutral Agent tool.</summary>
public interface IAgentTool
{
    /// <summary>Gets the immutable tool descriptor.</summary>
    AgentToolDescriptor Descriptor { get; }

    /// <summary>Invokes the tool with untrusted model arguments.</summary>
    ValueTask<AgentToolOutcome> InvokeAsync(
        AgentToolContext context,
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>Represents the semantic result of a tool call.</summary>
public abstract record AgentToolOutcome;

/// <summary>Represents a successful structured tool result.</summary>
public sealed record AgentToolSuccess : AgentToolOutcome
{
    /// <summary>Initializes a successful tool result.</summary>
    public AgentToolSuccess(ImmutableArray<AIContent> contents)
    {
        if (contents.IsDefault)
        {
            throw new ArgumentException("Tool result contents must be initialized.", nameof(contents));
        }

        foreach (var content in contents)
        {
            ArgumentNullException.ThrowIfNull(content);
            AgentToolContent.Validate(content);
        }

        Contents = contents;
    }

    /// <summary>Gets the ordered result contents.</summary>
    public ImmutableArray<AIContent> Contents { get; }
}

/// <summary>Represents an expected tool failure that the model may recover from.</summary>
public sealed record AgentToolFailure : AgentToolOutcome
{
    /// <summary>Initializes an expected tool failure.</summary>
    public AgentToolFailure(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
    }

    /// <summary>Gets the stable machine-readable failure code.</summary>
    public string Code { get; }

    /// <summary>Gets the safe model-visible failure message.</summary>
    public string Message { get; }
}

/// <summary>Reports that the model requested a tool call.</summary>
/// <param name="RunId">The owning run.</param>
/// <param name="Sequence">The strictly increasing run-local sequence.</param>
/// <param name="CallId">The Maieutics tool call identifier.</param>
/// <param name="Tool">The requested tool descriptor.</param>
/// <param name="Arguments">The cloned untrusted arguments.</param>
public sealed record AgentToolRequested(
    AgentRunId RunId,
    long Sequence,
    AgentToolCallId CallId,
    AgentToolDescriptor Tool,
    AgentToolArguments Arguments) : AgentEvent(RunId, Sequence);

/// <summary>Reports that a requested tool started executing.</summary>
/// <param name="RunId">The owning run.</param>
/// <param name="Sequence">The strictly increasing run-local sequence.</param>
/// <param name="CallId">The Maieutics tool call identifier.</param>
/// <param name="Tool">The executing tool descriptor.</param>
public sealed record AgentToolStarted(
    AgentRunId RunId,
    long Sequence,
    AgentToolCallId CallId,
    AgentToolDescriptor Tool) : AgentEvent(RunId, Sequence);

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

/// <summary>Reports a successful tool completion.</summary>
/// <param name="RunId">The owning run.</param>
/// <param name="Sequence">The strictly increasing run-local sequence.</param>
/// <param name="CallId">The Maieutics tool call identifier.</param>
/// <param name="Outcome">The successful structured outcome.</param>
public sealed record AgentToolCompleted(
    AgentRunId RunId,
    long Sequence,
    AgentToolCallId CallId,
    AgentToolSuccess Outcome) : AgentEvent(RunId, Sequence);

/// <summary>Reports an expected or terminal tool failure.</summary>
/// <param name="RunId">The owning run.</param>
/// <param name="Sequence">The strictly increasing run-local sequence.</param>
/// <param name="CallId">The Maieutics tool call identifier.</param>
/// <param name="Code">The stable failure code.</param>
/// <param name="Message">The safe failure message.</param>
public sealed record AgentToolFailed(
    AgentRunId RunId,
    long Sequence,
    AgentToolCallId CallId,
    string Code,
    string Message) : AgentEvent(RunId, Sequence);

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