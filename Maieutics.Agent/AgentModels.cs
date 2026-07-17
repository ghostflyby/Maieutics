using System.Collections.Immutable;

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

/// <summary>Represents provider-neutral Agent content.</summary>
public abstract record AgentContent;

/// <summary>Represents plain textual Agent content.</summary>
public sealed record AgentTextContent : AgentContent
{
    /// <summary>Initializes textual content.</summary>
    public AgentTextContent(string text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <summary>Gets the text.</summary>
    public string Text { get; }
}

/// <summary>Represents the input submitted for one Agent run.</summary>
public sealed record AgentTurn
{
    /// <summary>Initializes a turn from typed content.</summary>
    public AgentTurn(ImmutableArray<AgentContent> contents)
    {
        if (contents.IsDefault)
        {
            throw new ArgumentException("Agent turn contents must be initialized.", nameof(contents));
        }

        Contents = contents;
    }

    /// <summary>Gets the ordered input content.</summary>
    public ImmutableArray<AgentContent> Contents { get; }

    /// <summary>Creates a text-only turn.</summary>
    public static AgentTurn FromText(string text) => new([new AgentTextContent(text)]);
}

/// <summary>Identifies the semantic role of a transcript message.</summary>
public enum AgentMessageRole
{
    /// <summary>A message supplied by the user.</summary>
    User,

    /// <summary>A message produced by the assistant.</summary>
    Assistant
}

/// <summary>Represents one committed provider-neutral transcript message.</summary>
public sealed record AgentMessage
{
    /// <summary>Initializes a transcript message.</summary>
    public AgentMessage(
        AgentMessageId id,
        AgentMessageRole role,
        ImmutableArray<AgentContent> contents)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent message identifiers cannot be empty.", nameof(id));
        }

        if (contents.IsDefault)
        {
            throw new ArgumentException("Agent message contents must be initialized and non-null.", nameof(contents));
        }

        Id = id;
        Role = role;
        Contents = contents;
    }

    /// <summary>Gets the message identifier.</summary>
    public AgentMessageId Id { get; }

    /// <summary>Gets the message role.</summary>
    public AgentMessageRole Role { get; }

    /// <summary>Gets the ordered message content.</summary>
    public ImmutableArray<AgentContent> Contents { get; }
}

/// <summary>Represents an immutable snapshot of committed conversation history.</summary>
public sealed record AgentTranscript
{
    /// <summary>Initializes a transcript snapshot.</summary>
    public AgentTranscript(
        AgentSessionId sessionId,
        long version,
        ImmutableArray<AgentMessage> messages)
    {
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent session identifiers cannot be empty.", nameof(sessionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(version);
        if (messages.IsDefault)
        {
            throw new ArgumentException("Transcript messages must be initialized and non-null.", nameof(messages));
        }

        SessionId = sessionId;
        Version = version;
        Messages = messages;
    }

    /// <summary>Gets the session identifier.</summary>
    public AgentSessionId SessionId { get; }

    /// <summary>Gets the committed transcript version.</summary>
    public long Version { get; }

    /// <summary>Gets the committed messages.</summary>
    public ImmutableArray<AgentMessage> Messages { get; }
}

/// <summary>Represents the successful terminal result of one Agent run.</summary>
public sealed record AgentRunResult
{
    /// <summary>Initializes a successful run result.</summary>
    public AgentRunResult(
        AgentRunId runId,
        AgentMessage userMessage,
        AgentMessage assistantMessage,
        AgentTranscript transcript)
    {
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent run identifiers cannot be empty.", nameof(runId));
        }

        RunId = runId;
        UserMessage = userMessage ?? throw new ArgumentNullException(nameof(userMessage));
        AssistantMessage = assistantMessage ?? throw new ArgumentNullException(nameof(assistantMessage));
        Transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
    }

    /// <summary>Gets the run identifier.</summary>
    public AgentRunId RunId { get; }

    /// <summary>Gets the committed user message.</summary>
    public AgentMessage UserMessage { get; }

    /// <summary>Gets the committed assistant message.</summary>
    public AgentMessage AssistantMessage { get; }

    /// <summary>Gets the transcript after commit.</summary>
    public AgentTranscript Transcript { get; }
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
        AgentMessage message) : base(runId, sequence)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    /// <summary>Gets the assembled assistant message.</summary>
    public AgentMessage Message { get; }
}