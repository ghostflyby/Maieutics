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

    /// <summary>Triage bytes into inline text or a stored blob reference. At most one byte past
    /// the inline threshold is buffered to make the inline decision; stored content is streamed
    /// to the store (rewound when the source is seekable, continued from the buffered prefix
    /// when it is not) instead of being materialized in memory.</summary>
    /// <param name="store">The object store that receives non-inline content.</param>
    /// <param name="content">The raw bytes.</param>
    /// <param name="mediaType">The content's media type; only text types may stay inline.</param>
    /// <param name="name">An optional display name carried by the blob reference.</param>
    /// <param name="inlineThresholdBytes">Inline size ceiling; zero always stores.</param>
    /// <param name="cancellationToken">Cancels reading from <paramref name="content" />.</param>
    /// <returns>The canonical content: TextContent or a blob reference DataContent.</returns>
    public static async ValueTask<AIContent> IngestAsync(
        IAgentObjectStore store,
        Stream content,
        string mediaType,
        string? name = null,
        int inlineThresholdBytes = DefaultInlineThresholdBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentOutOfRangeException.ThrowIfNegative(inlineThresholdBytes);

        // One byte past the threshold answers "does the content fit inline" without ever
        // reading the whole stream; the prefix is also reused for the store path when the
        // source cannot be rewound.
        var prefix = new byte[checked(inlineThresholdBytes + 1)];
        var length = 0;
        while (length < prefix.Length)
        {
            var read = await content
                .ReadAsync(prefix.AsMemory(length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;

            length += read;
        }

        if (length <= inlineThresholdBytes && IsInlineText(mediaType, prefix.AsSpan(0, length)))
            return new TextContent(StrictUtf8.GetString(prefix.AsSpan(0, length)));

        if (content.CanSeek)
        {
            content.Seek(0, SeekOrigin.Begin);
            var ingested = store.Ingest(content);
            return BlobReference(ingested, mediaType, name);
        }

        var streamed = store.Ingest(new PrefixedStream(prefix, length, content));
        return BlobReference(streamed, mediaType, name);
    }

    private static AIContent BlobReference(AgentObjectDescriptor ingested, string mediaType, string? name)
    {
        return AgentBlobContent.Create(new AgentObjectDescriptor(ingested.Sha256, ingested.Size, mediaType, name));
    }

    private static bool IsInlineText(string mediaType, ReadOnlySpan<byte> bytes)
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

    /// <summary>Serves a bounded buffered prefix followed by the remaining live bytes of a
    /// non-seekable source, so the object store can hash oversized content without the triage
    /// point ever materializing it whole. The caller keeps ownership of the source stream.</summary>
    private sealed class PrefixedStream(byte[] prefix, int prefixLength, Stream remainder) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var copied = 0;
            if (position < prefixLength)
            {
                copied = Math.Min(count, prefixLength - position);
                Array.Copy(prefix, position, buffer, offset, copied);
                position += copied;
            }

            while (copied < count)
            {
                var read = remainder.Read(buffer, offset + copied, count - copied);
                if (read == 0) break;

                position += read;
                copied += read;
            }

            return copied;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
