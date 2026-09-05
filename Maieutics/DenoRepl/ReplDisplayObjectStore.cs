using Maieutics.Persistence;

namespace Maieutics.DenoRepl;

/// <summary>
///     Adapts the persistence object store onto the display-object contract: bytes are
///     stored content-addressed and served under a stable relative URL. Identical bytes
///     always land on the same path, so client caches hit across displays, runs, and
///     notebooks.
/// </summary>
internal sealed class ReplDisplayObjectStore(ObjectStore store) : IReplDisplayObjectStore
{
    /// <summary>The relative URL prefix under which display objects are served.</summary>
    internal const string UrlPrefix = "/v1/objects/";

    public string Store(ReadOnlySpan<byte> content, string mime)
    {
        var sha = store.Ingest(new MemoryStream(content.ToArray())).Sha256;
        return $"{UrlPrefix}{sha}";
    }
}

internal static class ReplDisplayObjectUrl
{
    /// <summary>The relative URL prefix under which display objects are served.</summary>
    internal const string UrlPrefix = "/v1/objects/";

    /// <summary>The relative URL for an object reference payload.</summary>
    internal static string FromReference(string sha256) => $"{UrlPrefix}{sha256}";
}
