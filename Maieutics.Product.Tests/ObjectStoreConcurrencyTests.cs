using System.Text;
using FluentAssertions;
using Maieutics.Persistence;

namespace Maieutics.Product.Tests;

/// <summary>Concurrency guards for content-addressed publication. The object store is a
/// singleton shared by the Agent tool-result ingest and the REPL display-object store, so the
/// same payload can be ingested by two paths at once. Publication must be a compare-and-set on
/// the destination: racers all find it absent, only one rename can win, and the losers must
/// observe the hit rather than an <c>IOException</c>.</summary>
public sealed class ObjectStoreConcurrencyTests : IDisposable
{
    private readonly string objectsRoot;

    public ObjectStoreConcurrencyTests()
    {
        objectsRoot = Path.Combine(
            Path.GetTempPath(),
            "maieutics-object-store-concurrency-tests",
            Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(objectsRoot))
        {
            Directory.Delete(objectsRoot, recursive: true);
        }
    }

    /// <summary>Many ingests of identical bytes, released simultaneously, must all succeed and
    /// agree on the address. Against the pre-fix check-then-act publish the losers of the
    /// rename race surfaced <c>IOException</c> instead of the CAS hit.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ConcurrentIngestsOfIdenticalContentAllSucceed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new ObjectStore(objectsRoot);
        var payload = Encoding.UTF8.GetBytes(new string('a', 64 * 1024));
        const int racers = 8;

        // Release every racer at once so the check-then-act window is actually reachable; a
        // single round is a coin flip, so iterate enough rounds to be meaningful.
        for (var round = 0; round < 25; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = await RaceAsync(
                racers,
                () => store.Ingest(new MemoryStream(payload)),
                cancellationToken);

            results.Select(result => result.Sha256).Distinct().Should().ContainSingle(
                "identical bytes address the same object");
            results.Should().AllSatisfy(result => result.Size.Should().Be(payload.Length));
        }

        var staging = Path.Combine(objectsRoot, ".staging");
        if (Directory.Exists(staging))
        {
            Directory.GetFiles(staging).Should().BeEmpty(
                "every loser of the publication race must discard its own temporary");
        }
    }

    /// <summary>Distinct payloads ingested concurrently must all be published and readable.
    /// Content addressing must not be traded for a lock that serializes the whole ingest.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ConcurrentIngestsOfDistinctContentAllPublish()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new ObjectStore(objectsRoot);
        const int racers = 16;

        var payloads = Enumerable.Range(0, racers)
            .Select(index => Encoding.UTF8.GetBytes($"payload-{index}"))
            .ToArray();
        var results = await RaceAsync(
            racers,
            index => store.Ingest(new MemoryStream(payloads[index])),
            cancellationToken);

        results.Select(result => result.Sha256).Distinct().Should().HaveCount(racers);
        foreach (var result in results)
        {
            store.Exists(result.Sha256).Should().BeTrue();
            using var read = store.Open(result.Sha256);
            read.Length.Should().Be(result.Size);
        }
    }

    /// <summary>Starts <paramref name="racers" /> ingests, holds them all at a start gate, then
    /// releases them together so the publication race is entered simultaneously.</summary>
    private static async Task<T[]> RaceAsync<T>(
        int racers,
        Func<int, T> ingest,
        CancellationToken cancellationToken)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;

        var tasks = Enumerable.Range(0, racers).Select(index => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == racers) ready.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return ingest(index);
        }, cancellationToken)).ToArray();

        // Release only once every racer is actually parked on the gate.
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        gate.TrySetResult();
        return await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<T[]> RaceAsync<T>(int racers, Func<T> ingest, CancellationToken cancellationToken) =>
        RaceAsync(racers, _ => ingest(), cancellationToken);
}
