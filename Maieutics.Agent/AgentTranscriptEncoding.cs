using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>
///     Encodes canonical Agent transcript messages to and from their versioned UTF-8 JSON form.
///     This is the one serialization contract shared by the in-memory transcript and durable
///     stores, so a turn persisted by any implementation decodes to the same canonical messages.
///     Inline binary content is rejected here exactly as it is during transcript commit.
/// </summary>
public static class AgentTranscriptEncoding
{
    /// <summary>Encodes canonical messages to their versioned UTF-8 JSON form.</summary>
    /// <param name="messages">The messages in provider order.</param>
    /// <returns>The encoded UTF-8 JSON bytes.</returns>
    /// <exception cref="AgentContentCompatibilityException">
    ///     A message carries content the official Microsoft.Extensions.AI JSON contract cannot encode.
    /// </exception>
    public static byte[] Encode(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return AgentTranscriptCodec.SerializeMessages(messages);
    }

    /// <summary>Decodes canonical messages from their versioned UTF-8 JSON form.</summary>
    /// <param name="encoded">The encoded bytes produced by <see cref="Encode" />.</param>
    /// <returns>The decoded messages in provider order.</returns>
    /// <exception cref="System.Text.Json.JsonException">The bytes are not a valid transcript encoding.</exception>
    public static ChatMessage[] Decode(ReadOnlySpan<byte> encoded)
    {
        return AgentTranscriptCodec.DeserializeMessages(encoded);
    }
}
