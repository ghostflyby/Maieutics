using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

internal static class AgentTranscriptCodec
{
    private const int SchemaVersion = 1;

    internal static readonly string ContractVersion =
        $"Microsoft.Extensions.AI/{GetInformationalVersion(typeof(AIContent).Assembly)}";

    internal static readonly string ProducerVersion =
        $"Maieutics.Agent/{GetInformationalVersion(typeof(AgentTranscriptCodec).Assembly)}";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static readonly JsonTypeInfo<ChatMessage[]> ChatMessageArrayTypeInfo =
        (JsonTypeInfo<ChatMessage[]>)SerializerOptions.GetTypeInfo(typeof(ChatMessage[]));

    private static readonly JsonTypeInfo<AgentTranscriptStateEnvelope> EnvelopeTypeInfo =
        (JsonTypeInfo<AgentTranscriptStateEnvelope>)SerializerOptions.GetTypeInfo(
            typeof(AgentTranscriptStateEnvelope));

    internal static byte[] CreateInitialState(AgentSessionId sessionId) =>
        Serialize(new AgentTranscriptState(sessionId, 0, []));

    internal static byte[] Serialize(AgentTranscriptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var turns = new AgentTranscriptStateTurnEnvelope[state.Turns.Length];
        for (var index = 0; index < state.Turns.Length; index++)
        {
            var turn = state.Turns[index];
            turns[index] = new AgentTranscriptStateTurnEnvelope(
                turn.RunId.Value,
                turn.ModelIdentity is null
                    ? null
                    : new AgentModelIdentityEnvelope(
                        turn.ModelIdentity.ProfileId.Value,
                        turn.ModelIdentity.Provider,
                        turn.ModelIdentity.Model),
                SerializeMessages(turn.Messages));
        }

        var envelope = new AgentTranscriptStateEnvelope(
            SchemaVersion,
            ContractVersion,
            ProducerVersion,
            state.SessionId.Value,
            state.Version,
            turns);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, EnvelopeTypeInfo);
    }

    internal static AgentTranscriptState Deserialize(ReadOnlySpan<byte> json)
    {
        var envelope = JsonSerializer.Deserialize(json, EnvelopeTypeInfo)
                       ?? throw new JsonException("The canonical Agent transcript is empty.");
        if (envelope.SchemaVersion != SchemaVersion)
        {
            throw new JsonException(
                $"Unsupported canonical Agent transcript schema version {envelope.SchemaVersion}.");
        }

        if (!string.Equals(envelope.ContractVersion, ContractVersion, StringComparison.Ordinal) ||
            !string.Equals(envelope.ProducerVersion, ProducerVersion, StringComparison.Ordinal))
        {
            throw new JsonException("The canonical Agent transcript contract is incompatible.");
        }

        var turns = ImmutableArray.CreateBuilder<AgentTranscriptStateTurn>(envelope.Turns.Length);
        foreach (var turn in envelope.Turns)
        {
            var modelIdentity = turn.ModelIdentity is null
                ? null
                : new AgentModelIdentity(
                    new AgentModelProfileId(turn.ModelIdentity.ProfileId),
                    turn.ModelIdentity.Provider,
                    turn.ModelIdentity.Model);
            turns.Add(new AgentTranscriptStateTurn(
                new AgentRunId(turn.RunId),
                modelIdentity,
                DeserializeMessages(turn.Messages)));
        }

        return new AgentTranscriptState(
            new AgentSessionId(envelope.SessionId),
            envelope.TranscriptVersion,
            turns.ToImmutable());
    }

    internal static int GetMessageByteCount(AgentTranscriptStateTurn turn) =>
        Encoding.UTF8.GetByteCount(SerializeMessages(turn.Messages).GetRawText());

    internal static ChatMessage DetachPrivateMessage(ChatMessage message) =>
        RoundTripMessages([message])[0];

    internal static ChatMessage CreatePublicMessage(ChatMessage message)
    {
        var publicContents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
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
        var detached = RoundTripMessages([new ChatMessage(ChatRole.Tool, [content])])[0].Contents.Single();
        SanitizePublicContent(detached);
        return detached;
    }

    internal static AgentTranscript CreatePublicTranscript(AgentTranscriptState state)
    {
        var turns = ImmutableArray.CreateBuilder<AgentTranscriptTurn>(state.Turns.Length);
        foreach (var turn in state.Turns)
        {
            var messages = new ChatMessage[turn.Messages.Count];
            for (var index = 0; index < turn.Messages.Count; index++)
            {
                messages[index] = CreatePublicMessage(turn.Messages[index]);
            }

            turns.Add(new AgentTranscriptTurn(turn.RunId, messages, turn.ModelIdentity));
        }

        return new AgentTranscript(state.SessionId, state.Version, turns.ToImmutable());
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            WriteIndented = false
        };
        options.TypeInfoResolverChain.Insert(0, AgentTranscriptJsonSerializerContext.Default);
        return options;
    }

    private static string GetInformationalVersion(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion)) return assembly.GetName().Version?.ToString() ?? "unknown";
        var metadataSeparator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return metadataSeparator < 0
            ? informationalVersion
            : informationalVersion[..metadataSeparator];
    }

    private static JsonElement SerializeMessages(IReadOnlyList<ChatMessage> messages)
    {
        try
        {
            return JsonSerializer.SerializeToElement(messages.ToArray(), ChatMessageArrayTypeInfo);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw CreateCompatibilityException(messages, exception);
        }
    }

    private static ChatMessage[] DeserializeMessages(JsonElement messages) =>
        messages.Deserialize(ChatMessageArrayTypeInfo)
        ?? throw new JsonException("A canonical Agent transcript turn contains no message array.");

    private static IReadOnlyList<ChatMessage> RoundTripMessages(IReadOnlyList<ChatMessage> messages) =>
        DeserializeMessages(SerializeMessages(messages));

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
                    _ = JsonSerializer.SerializeToElement(
                        [new ChatMessage(message.Role, [content])],
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
    IReadOnlyList<ChatMessage> Messages);

internal sealed record AgentTranscriptStateEnvelope(
    [property: JsonPropertyName("schemaVersion")]
    int SchemaVersion,
    [property: JsonPropertyName("contractVersion")]
    string ContractVersion,
    [property: JsonPropertyName("producerVersion")]
    string ProducerVersion,
    [property: JsonPropertyName("sessionId")]
    Guid SessionId,
    [property: JsonPropertyName("transcriptVersion")]
    long TranscriptVersion,
    [property: JsonPropertyName("turns")] AgentTranscriptStateTurnEnvelope[] Turns);

internal sealed record AgentTranscriptStateTurnEnvelope(
    [property: JsonPropertyName("runId")] Guid RunId,
    [property: JsonPropertyName("modelIdentity")]
    AgentModelIdentityEnvelope? ModelIdentity,
    [property: JsonPropertyName("messages")]
    JsonElement Messages);

internal sealed record AgentModelIdentityEnvelope(
    [property: JsonPropertyName("profileId")]
    string ProfileId,
    [property: JsonPropertyName("provider")]
    string Provider,
    [property: JsonPropertyName("model")] string Model);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(AgentTranscriptStateEnvelope))]
internal sealed partial class AgentTranscriptJsonSerializerContext : JsonSerializerContext;