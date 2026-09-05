using System.Collections.Concurrent;
using Maieutics.DenoRepl;

namespace Maieutics.Jupyter.Tests;

/// <summary>In-memory display object store: bytes keyed by their relative URL.</summary>
internal sealed class InMemoryDisplayObjectStore : IReplDisplayObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> objects = new(StringComparer.Ordinal);
    private int sequence;

    public string Store(ReadOnlySpan<byte> content, string mime)
    {
        var url = $"/v1/objects/test-{++sequence}";
        objects[url] = content.ToArray();
        return url;
    }

    internal byte[]? Get(string url) => objects.TryGetValue(url, out var bytes) ? bytes : null;
}
