using System.Text;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>
///     Captures raw bytes as canonical transcript content: small UTF-8 text stays inline as
///     TextContent; everything else — binary, non-text media, or text above the inline
///     threshold — is published to the object store and represented by a blob reference. This
///     is the single triage point for content entering the transcript from uploads, tools, or
///     future notebook attachments; inline size and media rules live here and nowhere else.
/// </summary>
public static class AgentContentTriage
{
    /// <summary>The default maximum size of content kept inline in the transcript.</summary>
    public const int DefaultInlineThresholdBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Triage bytes into inline text or a stored blob reference.</summary>
    /// <param name="store">The object store that receives non-inline content.</param>
    /// <param name="content">The raw bytes.</param>
    /// <param name="mediaType">The content's media type; only text types may stay inline.</param>
    /// <param name="name">An optional display name carried by the blob reference.</param>
    /// <param name="inlineThresholdBytes">Inline size ceiling; zero always stores.</param>
    /// <returns>The canonical content: TextContent or a blob reference DataContent.</returns>
    public static AIContent Ingest(
        IAgentObjectStore store,
        Stream content,
        string mediaType,
        string? name = null,
        int inlineThresholdBytes = DefaultInlineThresholdBytes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentOutOfRangeException.ThrowIfNegative(inlineThresholdBytes);

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();

        if (bytes.Length <= inlineThresholdBytes && IsInlineText(mediaType, bytes))
            return new TextContent(StrictUtf8.GetString(bytes));

        var ingested = store.Ingest(new MemoryStream(bytes, writable: false));
        return AgentBlobContent.Create(
            new AgentObjectDescriptor(ingested.Sha256, ingested.Size, mediaType, name));
    }

    private static bool IsInlineText(string mediaType, byte[] bytes)
    {
        var textual = mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        if (!textual) return false;

        try
        {
            _ = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
