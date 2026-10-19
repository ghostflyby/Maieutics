using System.Security.Cryptography;
using System.Text;
using Maieutics.Persistence;

namespace Maieutics.Product.Tests;

/// <summary>Product tests for the content-addressed object store: hash-addressed publication,
/// deduplication by CAS hit, readback, id validation, and staging reclamation.</summary>
public sealed class ObjectStoreTests : IAsyncDisposable
{
    // SHA-256 of the UTF-8 bytes "abc" (RFC 9160 test vector).
    private const string AbcDigest = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private readonly string objectsRoot;

    public ObjectStoreTests()
    {
        objectsRoot = Path.Combine(
            Path.GetTempPath(),
            "maieutics-object-store-tests",
            Guid.NewGuid().ToString("N"));
    }

    public async ValueTask DisposeAsync()
    {
        await TempDirectoryCleanup.DeleteWithRetryAsync(objectsRoot);
    }

    private ObjectStore CreateStore() => new(objectsRoot);

    [Fact]
    public void IngestPublishesTheObjectAtItsHashAddress()
    {
        var store = CreateStore();
        var ingested = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("abc")));

        Assert.Equal(AbcDigest, ingested.Sha256);
        Assert.Equal(3, ingested.Size);
        Assert.True(File.Exists(Path.Combine(objectsRoot, "ba", AbcDigest)));

        using var read = store.Open(AbcDigest);
        using var reader = new StreamReader(read);
        Assert.Equal("abc", reader.ReadToEnd());
    }

    [Fact]
    public void IngestingIdenticalContentIsACasHit()
    {
        var store = CreateStore();
        var first = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("abc")));
        var second = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("abc")));

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(Path.Combine(objectsRoot, "ba")));
        Assert.Empty(Directory.GetFiles(Path.Combine(objectsRoot, ".staging")));
    }

    [Fact]
    public void IngestLargeContentMatchesAReferenceHash()
    {
        var payload = new byte[3 * (1 << 20) + 7];
        Random.Shared.NextBytes(payload);
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var store = CreateStore();
        var ingested = store.Ingest(new MemoryStream(payload));

        Assert.Equal(payload.Length, ingested.Size);
        Assert.Equal(expected, ingested.Sha256);
        Assert.True(store.Exists(expected));
    }

    [Fact]
    public void UnknownObjectsAreAbsentAndRejected()
    {
        var store = CreateStore();
        var digest = new string('a', 64);

        Assert.False(store.Exists(digest));
        Assert.ThrowsAny<FileNotFoundException>(() => store.Open(digest));
        Assert.ThrowsAny<ArgumentException>(() => store.Open("not-a-digest"));
        Assert.ThrowsAny<ArgumentException>(() => store.Open(new string('A', 64)));
    }

    [Fact]
    public void SweepStagingReclaimsOnlyExpiredTemporaries()
    {
        var store = CreateStore();
        var staging = Path.Combine(objectsRoot, ".staging");
        var stale = Path.Combine(staging, "00000000000000000000000000000000.tmp");
        var fresh = Path.Combine(staging, "11111111111111111111111111111111.tmp");
        File.WriteAllBytes(stale, [1]);
        File.WriteAllBytes(fresh, [2]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(2));

        var removed = store.SweepStaging(TimeSpan.FromMinutes(30));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void DeleteExceptReclaimsUnreferencedObjects()
    {
        var store = CreateStore();
        var keep = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("keep")));
        var drop = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("drop")));

        var removed = store.DeleteExcept([keep.Sha256], DateTimeOffset.UtcNow);

        Assert.Equal(1, removed);
        Assert.True(store.Exists(keep.Sha256));
        Assert.False(store.Exists(drop.Sha256));
        Assert.Equal(0, store.DeleteExcept([keep.Sha256], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DeleteRemovesThePublishedObject()
    {
        var store = CreateStore();
        store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("abc")));

        store.Delete(AbcDigest);

        Assert.False(store.Exists(AbcDigest));
    }
}
