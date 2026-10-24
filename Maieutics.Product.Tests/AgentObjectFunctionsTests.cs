using System.Text;
using Maieutics.Agent;
using Maieutics.Persistence;

namespace Maieutics.Product.Tests;

/// <summary>The model-visible read path for stored objects: object_fetch returns stored JSON as
/// structured data and rejects absent or non-JSON objects with typed failures.</summary>
public sealed class AgentObjectFunctionsTests : IAsyncDisposable
{
    private readonly string objectsRoot;

    public AgentObjectFunctionsTests()
    {
        objectsRoot = Path.Combine(
            Path.GetTempPath(),
            "maieutics-object-functions-tests",
            Guid.NewGuid().ToString("N"));
    }

    public async ValueTask DisposeAsync()
    {
        await TempArea.DeleteAsync(objectsRoot, CancellationToken.None);
    }

    [Fact(Timeout = 30_000)]
    public async Task FetchReturnsTheStoredJsonAsStructuredData()
    {
        var store = new ObjectStore(objectsRoot);
        var ingested = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("""{"answer": 42}""")));

        var value = await AgentObjectFunctions.FetchAsync(
            store, ingested.Sha256, TestContext.Current.CancellationToken);

        Assert.NotNull(value);
        Assert.Equal(42, value.Value.GetProperty("answer").GetInt32());
    }

    [Fact(Timeout = 30_000)]
    public async Task FetchRejectsNonJsonObjectsWithATypedFailure()
    {
        var store = new ObjectStore(objectsRoot);
        var ingested = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("plain text")));

        var fetch = () => AgentObjectFunctions.FetchAsync(
            store, ingested.Sha256, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<AgentToolException>(async () => { await fetch.Invoke(); });
        Assert.Equal("object_not_json", exception.Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task FetchRejectsUnknownObjectsWithATypedFailure()
    {
        var store = new ObjectStore(objectsRoot);
        var functions = new AgentObjectFunctions(store);
        Assert.Equal("object_fetch", Assert.Single(functions.Functions).Name);

        var fetch = () => AgentObjectFunctions.FetchAsync(
            store, new string('a', 64), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<AgentToolException>(async () => { await fetch.Invoke(); });
        Assert.Equal("object_not_found", exception.Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task FetchRejectsMalformedIdsBeforeTouchingTheStore()
    {
        var store = new ObjectStore(objectsRoot);

        var fetch = () => AgentObjectFunctions.FetchAsync(
            store, "not-a-digest", TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAnyAsync<ArgumentException>(async () => { await fetch.Invoke(); });
        Assert.Equal("sha256", failure.ParamName);
    }
}
