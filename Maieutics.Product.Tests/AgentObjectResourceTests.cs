using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;

namespace Maieutics.Product.Tests;

public sealed class AgentObjectResourceTests
{
    [Fact]
    public async Task PublishedObjectReadsBackThroughItsContentAddress()
    {
        var harness = new ObjectHarness();
        var descriptor = harness.Store.Publish("resurrect the instruction from the object store"u8.ToArray());
        var provider = harness.Provider;
        var request = new ResourceReadRequest(1024 * 1024);

        var read = await provider.ReadAsync(
            $"objects://{descriptor.Sha256}",
            request,
            TestContext.Current.CancellationToken);

        read.MimeType.Should().Be("application/json");
        using var reader = new StreamReader(read.Content, Encoding.UTF8);
        reader.ReadToEnd().Should().Be("resurrect the instruction from the object store");
    }

    [Fact]
    public void ProviderIsABuiltInWholeSchemeClaim()
    {
        var harness = new ObjectHarness();
        var provider = harness.Provider;

        provider.Id.Should().Be("objects");
        provider.Class.Should().Be(ResourceProviderClass.BuiltIn);
        provider.Claims.Should().ContainSingle().Which.Should().Be(new ResourceClaim("objects"));
    }

    [Theory]
    [InlineData("objects://ABCDEF0000000000000000000000000000000000000000000000000000000000")]
    [InlineData("objects://abcd")]
    [InlineData("objects://abcd0000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("objects://zzzz0000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("objects://abcd%20g")]
    [InlineData("objects://")]
    public async Task MalformedContentAddressesAreRejectedAsInvalidUri(string uri)
    {
        var harness = new ObjectHarness();
        var request = new ResourceReadRequest(1024 * 1024);

        var read = () => harness.Provider.ReadAsync(
            uri,
            request,
            TestContext.Current.CancellationToken).AsTask();

        (await read.Should().ThrowAsync<ResourceException>())
            .Which.Code.Should().Be("resource_invalid_uri");
    }

    [Fact]
    public async Task UnknownObjectAddressReadsAsNotFound()
    {
        var harness = new ObjectHarness();
        var request = new ResourceReadRequest(1024 * 1024);

        var read = () => harness.Provider.ReadAsync(
            $"objects://{new string('a', 64)}",
            request,
            TestContext.Current.CancellationToken).AsTask();

        (await read.Should().ThrowAsync<ResourceException>())
            .Which.Code.Should().Be("resource_not_found");
    }

    [Fact]
    public async Task ReadLimitsBoundTheReturnedObject()
    {
        var harness = new ObjectHarness();
        var payload = Encoding.UTF8.GetBytes(new string('x', 512));
        var descriptor = harness.Store.Publish(payload);
        var request = new ResourceReadRequest(64);

        var read = () => harness.Provider.ReadAsync(
            $"objects://{descriptor.Sha256}",
            request,
            TestContext.Current.CancellationToken).AsTask();

        (await read.Should().ThrowAsync<ResourceException>())
            .Which.Code.Should().Be("resource_too_large");
    }

    private sealed class ObjectHarness
    {
        public ObjectHarness()
        {
            Store = new InMemoryObjectStore();
            Provider = new AgentObjectResourceProvider(Store);
        }

        public InMemoryObjectStore Store { get; }

        public AgentObjectResourceProvider Provider { get; }
    }

    private sealed class InMemoryObjectStore : IAgentObjectStore
    {
        private readonly Dictionary<string, byte[]> objects = new(StringComparer.Ordinal);

        public AgentObjectDescriptor Publish(byte[] bytes)
        {
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            objects[sha256] = bytes;
            return new AgentObjectDescriptor(sha256, bytes.Length);
        }

        public AgentObjectDescriptor Ingest(Stream content)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            return Publish(buffer.ToArray());
        }

        public Stream Open(string sha256)
        {
            if (!objects.TryGetValue(sha256, out var bytes))
                throw new FileNotFoundException($"Object '{sha256}' is not present.", sha256);

            return new MemoryStream(bytes, writable: false);
        }
    }
}
