using System.Collections.Immutable;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>Identifies an Agent session.</summary>
public readonly record struct AgentSessionId
{
    /// <summary>Initializes a session identifier.</summary>
    public AgentSessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Agent session identifiers cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the underlying value.</summary>
    public Guid Value { get; }

    /// <summary>Creates a new identifier.</summary>
    public static AgentSessionId Create() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies one Agent run.</summary>
public readonly record struct AgentRunId
{
    /// <summary>Initializes a run identifier.</summary>
    public AgentRunId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Agent run identifiers cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the underlying value.</summary>
    public Guid Value { get; }

    /// <summary>Creates a new identifier.</summary>
    public static AgentRunId Create() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies one transcript message.</summary>
public readonly record struct AgentMessageId
{
    /// <summary>Initializes a message identifier.</summary>
    public AgentMessageId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Agent message identifiers cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the underlying value.</summary>
    public Guid Value { get; }

    /// <summary>Creates a new identifier.</summary>
    public static AgentMessageId Create() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies a configured model profile independently of any provider SDK.</summary>
public readonly record struct AgentModelProfileId
{
    /// <summary>Initializes a model profile identifier.</summary>
    public AgentModelProfileId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64 || !IsAsciiLetterOrDigit(value[0]) ||
            value.AsSpan(1).ContainsAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-"))
        {
            throw new ArgumentException(
                "Agent model profile identifiers must match [A-Za-z0-9][A-Za-z0-9_-]{0,63}.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the configured profile identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}

/// <summary>Identifies the configured provider and model used by an Agent run.</summary>
public sealed record AgentModelIdentity
{
    /// <summary>Initializes a provider-neutral model identity.</summary>
    public AgentModelIdentity(AgentModelProfileId profileId, string provider, string model)
    {
        if (string.IsNullOrEmpty(profileId.Value))
        {
            throw new ArgumentException("Agent model profile identifiers cannot be empty.", nameof(profileId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ProfileId = profileId;
        Provider = provider;
        Model = model;
    }

    /// <summary>Gets the configured profile identifier.</summary>
    public AgentModelProfileId ProfileId { get; }

    /// <summary>Gets the provider family name.</summary>
    public string Provider { get; }

    /// <summary>Gets the provider-specific model identifier.</summary>
    public string Model { get; }
}

/// <summary>Describes model behaviors that an Agent run may require.</summary>
[Flags]
public enum AgentModelCapabilities
{
    /// <summary>No model behavior is declared.</summary>
    None = 0,

    /// <summary>The model supports streamed textual responses.</summary>
    StreamingText = 1,

    /// <summary>The model supports function-based tool calls.</summary>
    FunctionCalling = 2
}

/// <summary>Represents the input submitted for one Agent run.</summary>
public sealed record AgentTurn
{
    /// <summary>Initializes a turn from typed content.</summary>
    public AgentTurn(ImmutableArray<AIContent> contents)
    {
        if (contents.IsDefault)
        {
            throw new ArgumentException("Agent turn contents must be initialized.", nameof(contents));
        }

        Contents = contents;
    }

    /// <summary>Gets the ordered input content.</summary>
    public ImmutableArray<AIContent> Contents { get; }

    /// <summary>Creates a text-only turn.</summary>
    public static AgentTurn FromText(string text) => new([new TextContent(text)]);
}

/// <summary>Represents an immutable snapshot of committed conversation history.</summary>
public sealed record AgentTranscript
{
    /// <summary>Initializes a transcript snapshot.</summary>
    public AgentTranscript(
        AgentSessionId sessionId,
        long version,
        ImmutableArray<AgentTranscriptTurn> turns)
    {
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent session identifiers cannot be empty.", nameof(sessionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(version);
        if (turns.IsDefault)
        {
            throw new ArgumentException("Transcript turns must be initialized and non-null.", nameof(turns));
        }

        SessionId = sessionId;
        Version = version;
        Turns = turns;
    }

    /// <summary>Gets the session identifier.</summary>
    public AgentSessionId SessionId { get; }

    /// <summary>Gets the committed transcript version.</summary>
    public long Version { get; }

    /// <summary>Gets the committed complete turns.</summary>
    public ImmutableArray<AgentTranscriptTurn> Turns { get; }
}

/// <summary>Represents one complete committed Agent turn.</summary>
public sealed record AgentTranscriptTurn
{
    /// <summary>Initializes a complete transcript turn.</summary>
    public AgentTranscriptTurn(AgentRunId runId, IReadOnlyList<ChatMessage> messages)
        : this(runId, messages, null, false)
    {
    }

    /// <summary>Initializes a complete transcript turn with its model identity.</summary>
    public AgentTranscriptTurn(
        AgentRunId runId,
        IReadOnlyList<ChatMessage> messages,
        AgentModelIdentity? modelIdentity,
        bool truncated = false)
    {
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent run identifiers cannot be empty.", nameof(runId));
        }

        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            throw new ArgumentException("Transcript turn messages must be initialized and non-empty.",
                nameof(messages));
        }

        if (messages[0].Role != ChatRole.User || messages[^1].Role != ChatRole.Assistant)
        {
            throw new ArgumentException(
                "A transcript turn must begin with a user message and end with an assistant message.",
                nameof(messages));
        }

        RunId = runId;
        Messages = messages;
        ModelIdentity = modelIdentity;
        Truncated = truncated;
    }

    /// <summary>Gets the run that produced this turn.</summary>
    public AgentRunId RunId { get; }

    /// <summary>Gets all messages in provider order, including tool calls and results.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>Gets the configured model identity that produced the turn, when known.</summary>
    public AgentModelIdentity? ModelIdentity { get; }

    /// <summary>Gets whether the turn exhausted its budget before a validated final answer.</summary>
    public bool Truncated { get; }
}

/// <summary>Represents the successful terminal result of one Agent run.</summary>
public sealed record AgentRunResult
{
    /// <summary>Initializes a successful run result.</summary>
    public AgentRunResult(
        AgentRunId runId,
        ChatMessage userMessage,
        ChatMessage assistantMessage,
        AgentTranscript transcript)
        : this(runId, userMessage, assistantMessage, transcript, null, false)
    {
    }

    /// <summary>Initializes a successful run result with its model identity.</summary>
    public AgentRunResult(
        AgentRunId runId,
        ChatMessage userMessage,
        ChatMessage assistantMessage,
        AgentTranscript transcript,
        AgentModelIdentity? modelIdentity,
        bool truncated = false)
    {
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent run identifiers cannot be empty.", nameof(runId));
        }

        RunId = runId;
        UserMessage = userMessage ?? throw new ArgumentNullException(nameof(userMessage));
        AssistantMessage = assistantMessage ?? throw new ArgumentNullException(nameof(assistantMessage));
        Transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        ModelIdentity = modelIdentity;
        Truncated = truncated;
    }

    /// <summary>Gets the run identifier.</summary>
    public AgentRunId RunId { get; }

    /// <summary>Gets the committed user message.</summary>
    public ChatMessage UserMessage { get; }

    /// <summary>Gets the committed assistant message.</summary>
    public ChatMessage AssistantMessage { get; }

    /// <summary>Gets the transcript after commit.</summary>
    public AgentTranscript Transcript { get; }

    /// <summary>Gets the configured model identity used throughout the run, when known.</summary>
    public AgentModelIdentity? ModelIdentity { get; }

    /// <summary>Gets whether the turn exhausted its budget and committed partial progress.</summary>
    public bool Truncated { get; }
}

/// <summary>Represents one normalized event emitted by an Agent run.</summary>
public abstract record AgentEvent
{
    /// <summary>Initializes an Agent event.</summary>
    protected AgentEvent(AgentRunId runId, long sequence)
    {
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent run identifiers cannot be empty.", nameof(runId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        RunId = runId;
        Sequence = sequence;
    }

    /// <summary>Gets the owning run identifier.</summary>
    public AgentRunId RunId { get; }

    /// <summary>Gets the strictly increasing run-local sequence number.</summary>
    public long Sequence { get; }
}

/// <summary>Represents an assistant text fragment.</summary>
public sealed record AgentTextDelta : AgentEvent
{
    /// <summary>Initializes a text delta.</summary>
    public AgentTextDelta(
        AgentRunId runId,
        long sequence,
        AgentMessageId messageId,
        string text) : base(runId, sequence)
    {
        if (messageId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent message identifiers cannot be empty.", nameof(messageId));
        }

        MessageId = messageId;
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <summary>Gets the assistant message identifier.</summary>
    public AgentMessageId MessageId { get; }

    /// <summary>Gets the text fragment.</summary>
    public string Text { get; }
}

/// <summary>Represents a fully assembled assistant message.</summary>
public sealed record AgentMessageCompleted : AgentEvent
{
    /// <summary>Initializes a completed-message event.</summary>
    public AgentMessageCompleted(
        AgentRunId runId,
        long sequence,
        AgentMessageId agentMessageId,
        ChatMessage message) : base(runId, sequence)
    {
        if (agentMessageId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent message identifiers cannot be empty.", nameof(agentMessageId));
        }

        AgentMessageId = agentMessageId;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    /// <summary>Gets the run-local identifier used to correlate streamed text with this message.</summary>
    public AgentMessageId AgentMessageId { get; }

    /// <summary>Gets the assembled assistant message.</summary>
    public ChatMessage Message { get; }
}

/// <summary>Indicates that a turn ended because it exhausted its configured budget.</summary>
public sealed record AgentTurnTruncated : AgentEvent
{
    /// <summary>Initializes a truncation event.</summary>
    public AgentTurnTruncated(AgentRunId runId, long sequence) : base(runId, sequence)
    {
    }
}

/// <summary>Describes one model discovered from a provider API endpoint.</summary>
public sealed record AgentModelDescriptor
{
    /// <summary>Initializes a model descriptor.</summary>
    public AgentModelDescriptor(
        string id,
        string provider,
        string? ownedBy = null,
        DateTime? createdAt = null,
        long? contextWindow = null,
        string? family = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        Id = id;
        Provider = provider;
        OwnedBy = ownedBy;
        CreatedAt = createdAt;
        ContextWindow = contextWindow;
        Family = family;
    }

    /// <summary>Gets the provider-specific model identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the provider family name.</summary>
    public string Provider { get; }

    /// <summary>Gets the organization that owns the model, when known.</summary>
    public string? OwnedBy { get; }

    /// <summary>Gets the model creation timestamp, when known.</summary>
    public DateTime? CreatedAt { get; }

    /// <summary>Gets the maximum context window size, when known.</summary>
    public long? ContextWindow { get; }

    /// <summary>Gets the model family or architecture group, when known.</summary>
    public string? Family { get; }
}
