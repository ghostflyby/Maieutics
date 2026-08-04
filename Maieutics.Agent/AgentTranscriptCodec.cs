using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

internal static class AgentTranscriptCodec
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    private static readonly JsonTypeInfo<ChatMessage[]> ChatMessageArrayTypeInfo =
        (JsonTypeInfo<ChatMessage[]>)SerializerOptions.GetTypeInfo(typeof(ChatMessage[]));

    internal static AgentTranscriptState CreateInitialState(AgentSessionId sessionId) =>
        new(sessionId, 0, []);

    internal static AgentTranscriptStateTurn DetachPrivateTurn(
        AgentRunId runId,
        AgentModelIdentity? modelIdentity,
        IReadOnlyList<ChatMessage> messages,
        bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ValidateMessages(messages);
        var serialized = SerializeMessages(messages);
        return new AgentTranscriptStateTurn(
            runId,
            modelIdentity,
            DeserializeMessages(serialized).ToImmutableArray(),
            serialized.Length,
            truncated);
    }

    internal static ChatMessage DetachPrivateMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return DetachPrivateMessages([message])[0];
    }

    internal static IReadOnlyList<ChatMessage> DetachPrivateMessages(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ValidateMessages(messages);
        return DeserializeMessages(SerializeMessages(messages));
    }

    internal static ChatMessage CreatePublicMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var publicContents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            ValidateContent(content);
            if (content is TextReasoningContent)
            {
                continue;
            }

            try
            {
                publicContents.Add(CreatePublicContent(content));
            }
            catch (AgentContentCompatibilityException)
            {
                // Compatibility is enforced atomically when the complete private turn is committed.
            }
        }

        return new ChatMessage(message.Role, publicContents)
        {
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            MessageId = message.MessageId
        };
    }

    internal static AIContent CreatePublicContent(AIContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateContent(content);
        var detached = DetachPrivateMessages([new ChatMessage(ChatRole.Tool, [content])])[0].Contents.Single();
        SanitizePublicContent(detached);
        return detached;
    }

    internal static AgentTranscript CreatePublicTranscript(AgentTranscriptState state)
    {
        var turns = ImmutableArray.CreateBuilder<AgentTranscriptTurn>(state.Turns.Length);
        foreach (var turn in state.Turns)
        {
            var messages = new ChatMessage[turn.Messages.Length];
            for (var index = 0; index < turn.Messages.Length; index++)
            {
                messages[index] = CreatePublicMessage(turn.Messages[index]);
            }

            turns.Add(new AgentTranscriptTurn(turn.RunId, messages, turn.ModelIdentity, turn.Truncated));
        }

        return new AgentTranscript(state.SessionId, state.Version, turns.ToImmutable());
    }

    private static byte[] SerializeMessages(IReadOnlyList<ChatMessage> messages)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(messages.ToArray(), ChatMessageArrayTypeInfo);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw CreateCompatibilityException(messages, exception);
        }
    }

    private static ChatMessage[] DeserializeMessages(ReadOnlySpan<byte> messages) =>
        JsonSerializer.Deserialize(messages, ChatMessageArrayTypeInfo)
        ?? throw new JsonException("A canonical Agent transcript turn contains no message array.");

    private static AgentContentCompatibilityException CreateCompatibilityException(
        IReadOnlyList<ChatMessage> messages,
        Exception innerException)
    {
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                try
                {
                    _ = JsonSerializer.SerializeToUtf8Bytes(
                        new[] { new ChatMessage(message.Role, [content]) },
                        ChatMessageArrayTypeInfo);
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException)
                {
                    return new AgentContentCompatibilityException(
                        content.GetType().FullName ?? content.GetType().Name,
                        innerException);
                }
            }
        }

        // ReSharper disable once NullableWarningSuppressionIsUsed
        return new AgentContentCompatibilityException(typeof(ChatMessage).FullName!, innerException);
    }

    private static void ValidateMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            ValidateValue(message.AdditionalProperties, visited);
            foreach (var content in message.Contents)
            {
                ValidateContent(content, visited);
            }
        }
    }

    private static void ValidateContent(AIContent content) =>
        ValidateContent(content, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static void ValidateContent(AIContent content, HashSet<object> visited)
    {
        if (!visited.Add(content))
        {
            return;
        }

        ValidateValue(content.AdditionalProperties, visited);
        if (content.Annotations is not null)
        {
            foreach (var annotation in content.Annotations)
            {
                ValidateValue(annotation.AdditionalProperties, visited);
            }
        }

        switch (content)
        {
            case DataContent data:
                ValidateDataContent(data);
                break;
            case CodeInterpreterToolCallContent call:
                ValidateContents(call.Inputs, visited);
                break;
            case CodeInterpreterToolResultContent result:
                ValidateContents(result.Outputs, visited);
                break;
            case ImageGenerationToolResultContent result:
                ValidateContents(result.Outputs, visited);
                break;
            case McpServerToolResultContent result:
                ValidateContents(result.Outputs, visited);
                break;
            case WebSearchToolResultContent result:
                ValidateContents(result.Outputs, visited);
                break;
            case ToolApprovalRequestContent request:
                ValidateContent(request.ToolCall, visited);
                break;
            case ToolApprovalResponseContent response:
                ValidateContent(response.ToolCall, visited);
                break;
            case FunctionCallContent call:
                ValidateValue(call.Arguments, visited);
                break;
            case FunctionResultContent result:
                ValidateValue(result.Result, visited);
                break;
            case McpServerToolCallContent call:
                ValidateValue(call.Arguments, visited);
                break;
            case ErrorContent error:
                ValidateValue(error.Details, visited);
                break;
        }
    }

    private static void ValidateContents(IEnumerable<AIContent>? contents, HashSet<object> visited)
    {
        if (contents is null)
        {
            return;
        }

        foreach (var content in contents)
        {
            ValidateContent(content, visited);
        }
    }

    private static void ValidateValue(object? value, HashSet<object> visited)
    {
        switch (value)
        {
            case null or string or JsonElement or JsonNode:
                return;
            case AIContent content:
                ValidateContent(content, visited);
                return;
            case IEnumerable<AIContent> contents:
                ValidateContents(contents, visited);
                return;
            case byte[] or Memory<byte> or ReadOnlyMemory<byte> or ArraySegment<byte>:
                throw CreateInlineBinaryException(value.GetType().Name);
        }

        if (string.Equals(value.GetType().FullName, "System.BinaryData", StringComparison.Ordinal))
        {
            throw CreateInlineBinaryException(value.GetType().Name);
        }

        if (value.GetType().IsValueType || !visited.Add(value))
        {
            return;
        }

        switch (value)
        {
            case IEnumerable<KeyValuePair<string, object?>> properties:
                foreach (var (_, item) in properties)
                {
                    ValidateValue(item, visited);
                }

                break;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    ValidateValue(entry.Value, visited);
                }

                break;
            case IEnumerable sequence:
                foreach (var item in sequence)
                {
                    ValidateValue(item, visited);
                }

                break;
        }
    }

    private static void ValidateDataContent(DataContent data)
    {
        if (!string.Equals(data.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw CreateInlineBinaryException(data.MediaType);
        }

        try
        {
            using var _ = JsonDocument.Parse(data.Data);
        }
        catch (JsonException)
        {
            throw new AgentUnsupportedResponseException(
                "The model provider returned application/json DataContent that is not valid UTF-8 JSON.");
        }
    }

    private static AgentUnsupportedResponseException CreateInlineBinaryException(string? description) =>
        new(
            $"The model provider returned unsupported inline binary content '{description ?? "unknown"}'. " +
            "Binary content requires an artifact reference.");

    private static void SanitizePublicContent(AIContent content)
    {
        content.RawRepresentation = null;
        content.AdditionalProperties = null;
        if (content.Annotations is null)
        {
            return;
        }

        foreach (var annotation in content.Annotations)
        {
            annotation.RawRepresentation = null;
            annotation.AdditionalProperties = null;
        }
    }
}

internal sealed record AgentTranscriptState(
    AgentSessionId SessionId,
    long Version,
    ImmutableArray<AgentTranscriptStateTurn> Turns);

internal sealed record AgentTranscriptStateTurn(
    AgentRunId RunId,
    AgentModelIdentity? ModelIdentity,
    ImmutableArray<ChatMessage> Messages,
    int MessageByteCount,
    bool Truncated);
