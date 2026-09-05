using Maieutics.Persistence;

namespace Maieutics.DenoRepl;

/// <summary>
///     Adapts the persistence layer's object store onto the collector's object bypass: large
///     binary display payloads are ingested once and referenced by their content address.
/// </summary>
internal sealed class ReplObjectBypass(ObjectStore store) : IReplObjectBypass
{
    public string Ingest(ReadOnlySpan<byte> content)
    {
        return store.Ingest(new MemoryStream(content.ToArray())).Sha256;
    }
}
