using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>
///     The canonical persistence form of a content-addressed blob reference: a DataContent
///     carrying the descriptor JSON under the reserved media type. Reference payloads are small
///     structured JSON, so they satisfy the transcript's no-inline-binary rule while naming
///     arbitrarily large bytes in the object store. Provider adapters translate this content
///     into API-specific uploads or references; no provider ever sees the reserved media type.
/// </summary>
public static class AgentBlobContent
{
    /// <summary>The reserved media type of a blob reference DataContent.</summary>
    public const string MediaType = "application/vnd.maieutics.blob+json";

    /// <summary>Creates the blob reference content for one published object.</summary>
    public static DataContent Create(AgentObjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sha256", descriptor.Sha256);
            writer.WriteNumber("size", descriptor.Size);
            if (descriptor.MediaType is { } mediaType) writer.WriteString("mediaType", mediaType);
            if (descriptor.Name is { } name) writer.WriteString("name", name);
            writer.WriteEndObject();
        }

        return new DataContent(buffer.ToArray(), MediaType);
    }

    /// <summary>Parses a blob reference from transcript content.</summary>
    public static bool TryParse(AIContent? content, out AgentObjectDescriptor descriptor)
    {
        if (content is DataContent data &&
            string.Equals(data.MediaType, MediaType, StringComparison.OrdinalIgnoreCase))
        {
            return TryParseDescriptor(data.Data.ToArray(), out descriptor);
        }

        descriptor = new AgentObjectDescriptor(string.Empty, 0);
        return false;
    }

    /// <summary>Validates that the content is a well-formed blob reference; used by the
    /// transcript codec as part of the commit-time compatibility check.</summary>
    /// <exception cref="AgentUnsupportedResponseException">The content is a malformed blob reference.</exception>
    public static void Validate(AIContent content)
    {
        if (!TryParse(content, out var descriptor) || string.IsNullOrEmpty(descriptor.Sha256))
        {
            throw new AgentUnsupportedResponseException(
                "The model or tool produced a malformed blob reference; the descriptor must carry a valid content address.");
        }
    }

    internal static bool TryParseDescriptor(ReadOnlyMemory<byte> json, out AgentObjectDescriptor descriptor)
    {
        descriptor = new AgentObjectDescriptor(string.Empty, 0);
        string? sha256 = null;
        long size = -1;
        string? mediaType = null;
        string? name = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "sha256" when property.Value.ValueKind == JsonValueKind.String:
                        sha256 = property.Value.GetString();
                        break;
                    case "size" when property.Value.ValueKind == JsonValueKind.Number:
                        size = property.Value.GetInt64();
                        break;
                    case "mediaType" when property.Value.ValueKind == JsonValueKind.String:
                        mediaType = property.Value.GetString();
                        break;
                    case "name" when property.Value.ValueKind == JsonValueKind.String:
                        name = property.Value.GetString();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (sha256 is null || size < 0 || sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || sha256.Length != 64)
            return false;

        descriptor = new AgentObjectDescriptor(sha256, size, mediaType, name);
        return true;
    }
}
