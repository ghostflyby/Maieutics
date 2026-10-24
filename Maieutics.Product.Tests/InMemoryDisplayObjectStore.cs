using System.Collections.Concurrent;
using Maieutics.DenoRepl;

namespace Maieutics.Product.Tests;

/// <summary>In-memory display object store: bytes keyed by their relative URL.</summary>
internal sealed class InMemoryDisplayObjectStore : IReplDisplayObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private int _sequence;

    public string Store(ReadOnlySpan<byte> content, string mime)
    {
        var url = $"/v1/objects/test-{++_sequence}";
        _objects[url] = content.ToArray();
        return url;
    }

    internal byte[]? Get(string url) => _objects.GetValueOrDefault(url);
}
