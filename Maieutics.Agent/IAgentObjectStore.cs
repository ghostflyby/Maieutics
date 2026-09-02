namespace Maieutics.Agent;

/// <summary>
///     Publishes immutable byte sequences into the content-addressed object store. Tool results
///     that exceed the inline envelope limit are ingested here before the truncated preview
///     enters the transcript, so the full bytes outlive the turn. Ingestion is a pure append:
///     identical content is deduplicated by address and published objects are never rewritten.
/// </summary>
public interface IAgentObjectStore
{
    /// <summary>Ingests a stream: hashes while writing and publishes atomically. The stream is not
    /// disposed by the store; the caller owns it.</summary>
    /// <param name="content">The bytes to publish.</param>
    /// <returns>The descriptor of the published object.</returns>
    /// <exception cref="InvalidOperationException">The bytes could not be published durably.</exception>
    AgentObjectDescriptor Ingest(Stream content);

    /// <summary>Opens a published object for reading.</summary>
    /// <param name="sha256">The lowercase SHA-256 content address.</param>
    /// <returns>A read-only stream over the object bytes.</returns>
    /// <exception cref="ArgumentException">The id is not a valid content address.</exception>
    /// <exception cref="FileNotFoundException">The object is not present in the store.</exception>
    Stream Open(string sha256);
}

/// <summary>Describes one published object: its content address and byte size.</summary>
public sealed record AgentObjectDescriptor(string Sha256, long Size);
