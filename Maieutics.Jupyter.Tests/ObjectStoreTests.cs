using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Maieutics.Persistence;

namespace Maieutics.Jupyter.Tests;

/// <summary>Product tests for the content-addressed object store: hash-addressed publication,
/// deduplication by CAS hit, readback, id validation, and staging reclamation.</summary>
public sealed class ObjectStoreTests : IDisposable
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

    public void Dispose()
    {
        if (Directory.Exists(objectsRoot))
        {
            Directory.Delete(objectsRoot, recursive: true);
        }
    }

    private ObjectStore CreateStore() => new(objectsRoot);

    [Fact]
    public void IngestPublishesTheObjectAtItsHashAddress()
    {
        var store = CreateStore();
        var ingested = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("abc")));

        ingested.Sha256.Should().Be(AbcDigest);
        ingested.Size.Should().Be(3);
        File.Exists(Path.Combine(objectsRoot, "ba", AbcDigest)).Should().BeTrue();

        using var read = store.Open(AbcDigest);
        using var reader = new StreamReader(read);
        reader.ReadToEnd().Should().Be("abc");
    }

    [Fact]
    public void IngestingIdenticalContentIsACasHit()
    {
        var store = CreateStore();
        var first = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("abc")));
        var second = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("abc")));

        second.Should().Be(first);
        Directory.GetFiles(Path.Combine(objectsRoot, "ba")).Should().ContainSingle();
        Directory.GetFiles(Path.Combine(objectsRoot, ".staging")).Should().BeEmpty();
    }

    [Fact]
    public void IngestLargeContentMatchesAReferenceHash()
    {
        var payload = new byte[3 * (1 << 20) + 7];
        Random.Shared.NextBytes(payload);
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var store = CreateStore();
        var ingested = store.Ingest(new MemoryStream(payload));

        ingested.Size.Should().Be(payload.Length);
        ingested.Sha256.Should().Be(expected);
        store.Exists(expected).Should().BeTrue();
    }

    [Fact]
    public void UnknownObjectsAreAbsentAndRejected()
    {
        var store = CreateStore();
        var digest = new string('a', 64);

        store.Exists(digest).Should().BeFalse();
        store.Invoking(s => s.Open(digest)).Should().Throw<FileNotFoundException>();
        store.Invoking(s => s.Open("not-a-digest")).Should().Throw<ArgumentException>();
        store.Invoking(s => s.Open(new string('A', 64))).Should().Throw<ArgumentException>();
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

        removed.Should().Be(1);
        File.Exists(stale).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void DeleteExceptReclaimsUnreferencedObjects()
    {
        var store = CreateStore();
        var keep = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("keep")));
        var drop = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("drop")));

        var removed = store.DeleteExcept([keep.Sha256]);

        removed.Should().Be(1);
        store.Exists(keep.Sha256).Should().BeTrue();
        store.Exists(drop.Sha256).Should().BeFalse();
        store.DeleteExcept([keep.Sha256]).Should().Be(0);
    }

    [Fact]
    public void DeleteRemovesThePublishedObject()
    {
        var store = CreateStore();
        store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("abc")));

        store.Delete(AbcDigest);

        store.Exists(AbcDigest).Should().BeFalse();
    }
}
