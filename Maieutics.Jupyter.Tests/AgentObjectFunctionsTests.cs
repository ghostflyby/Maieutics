using System.Text;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Persistence;

namespace Maieutics.Jupyter.Tests;

/// <summary>The model-visible read path for stored objects: object_fetch returns stored JSON as
/// structured data and rejects absent or non-JSON objects with typed failures.</summary>
public sealed class AgentObjectFunctionsTests : IDisposable
{
    private readonly string objectsRoot;

    public AgentObjectFunctionsTests()
    {
        objectsRoot = Path.Combine(
            Path.GetTempPath(),
            "maieutics-object-functions-tests",
            Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(objectsRoot))
        {
            Directory.Delete(objectsRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FetchReturnsTheStoredJsonAsStructuredData()
    {
        var store = new ObjectStore(objectsRoot);
        var functions = new AgentObjectFunctions(store);
        var ingested = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("""{"answer": 42}""")));

        var value = await AgentObjectFunctions.FetchAsync(
            store, ingested.Sha256, TestContext.Current.CancellationToken);

        value.Should().NotBeNull();
        value!.Value.GetProperty("answer").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task FetchRejectsNonJsonObjectsWithATypedFailure()
    {
        var store = new ObjectStore(objectsRoot);
        var ingested = store.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("plain text")));

        var fetch = () => AgentObjectFunctions.FetchAsync(
            store, ingested.Sha256, TestContext.Current.CancellationToken);

        var exception = (await fetch.Should().ThrowAsync<AgentToolException>()).And;
        exception.Code.Should().Be("object_not_json");
    }

    [Fact]
    public async Task FetchRejectsUnknownObjectsWithATypedFailure()
    {
        var store = new ObjectStore(objectsRoot);
        var functions = new AgentObjectFunctions(store);
        functions.Functions.Should().ContainSingle().Which.Name.Should().Be("object_fetch");

        var fetch = () => AgentObjectFunctions.FetchAsync(
            store, new string('a', 64), TestContext.Current.CancellationToken);

        var exception = (await fetch.Should().ThrowAsync<AgentToolException>()).And;
        exception.Code.Should().Be("object_not_found");
    }

    [Fact]
    public async Task FetchRejectsMalformedIdsBeforeTouchingTheStore()
    {
        var store = new ObjectStore(objectsRoot);

        var fetch = () => AgentObjectFunctions.FetchAsync(
            store, "not-a-digest", TestContext.Current.CancellationToken);

        (await fetch.Should().ThrowAsync<ArgumentException>()).And.ParamName.Should().Be("sha256");
    }
}
