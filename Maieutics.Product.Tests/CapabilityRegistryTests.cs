using System.Text;
using Maieutics.Agent;
using Maieutics.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Maieutics.Product.Tests;

public sealed class CapabilityRegistryTests
{
    [Fact]
    public void BuiltInVendorAggregateIntersectsFormatCapabilities()
    {
        var registry = CreateRegistry("{}");
        // Shell is in the format but the built-in OpenAI catalog does not serve it, so the
        // intersection must drop it.
        var source = new FakeSource("OpenAI", "https://api.openai.com/v1", format: ["WebSearch", "Shell"]);

        var resolution = registry.Resolve(source, "gpt-5");

        Assert.True(resolution.KnownVendor);
        Assert.Equal("openai", resolution.VendorId);
        Assert.False(resolution.Matched);
        Assert.Equal(["WebSearch"], resolution.Potential);
        Assert.Equal(resolution.Potential, resolution.Effective);
    }

    [Fact]
    public void FormatCapabilitiesIntersectWithAggregate()
    {
        var registry = CreateRegistry("""
        {
          "Vendors": {
            "opencode": {
              "Endpoints": ["https://opencode.ai/v1"],
              "Capabilities": ["WebSearch", "Shell", "FileSearch"]
            }
          }
        }
        """);
        // The format cannot host Shell, so the intersection drops it.
        var source = new FakeSource(
            "OpenAI",
            "https://opencode.ai/v1",
            vendor: "opencode",
            format: ["WebSearch", "FileSearch"]);

        var resolution = registry.Resolve(source, "gpt-5");

        Assert.True(resolution.KnownVendor);
        Assert.Equivalent(new[] { "FileSearch", "WebSearch" }, resolution.Potential, strict: true);
        Assert.Equal(resolution.Potential, resolution.Effective);
    }

    [Fact]
    public void ModelDeclarationNarrowsAggregateCapabilities()
    {
        var registry = CreateRegistry("""
        {
          "Vendors": {
            "opencode": {
              "Endpoints": ["https://opencode.ai/v1"],
              "Capabilities": ["WebSearch", "Shell"],
              "Models": {
                "gpt-5": { "Capabilities": ["WebSearch"] }
              }
            }
          }
        }
        """);
        var source = new FakeSource(
            "OpenAI",
            "https://opencode.ai/v1",
            vendor: "opencode",
            format: ["WebSearch", "Shell"]);

        var narrowed = registry.Resolve(source, "gpt-5");
        Assert.Equal(["WebSearch"], narrowed.Potential);

        // A model without a declaration falls back to the vendor aggregate.
        var aggregate = registry.Resolve(source, "gpt-4");
        Assert.Equivalent(new[] { "Shell", "WebSearch" }, aggregate.Potential, strict: true);
    }

    [Fact]
    public void AnthropicFormatHostsNothingEvenWhenVendorDeclaresCapabilities()
    {
        var registry = CreateRegistry("""
        {
          "Vendors": {
            "opencode": {
              "Endpoints": ["https://opencode.ai/v1"],
              "Capabilities": ["WebSearch", "Shell"]
            }
          }
        }
        """);
        var source = new FakeSource(
            "Anthropic",
            "https://opencode.ai/v1",
            vendor: "opencode",
            format: []);

        var resolution = registry.Resolve(source, "claude-sonnet");

        Assert.True(resolution.KnownVendor);
        Assert.Empty(resolution.Potential);
        Assert.Empty(resolution.Effective);
    }

    [Fact]
    public void EmptyEndpointFallsBackToProviderDefaultVendorHost()
    {
        var registry = CreateRegistry("{}");
        var source = new FakeSource("OpenAI", endpoint: null, format: ["WebSearch"]);

        var resolution = registry.Resolve(source, "gpt-5");

        Assert.True(resolution.KnownVendor);
        Assert.Equal("openai", resolution.VendorId);
        Assert.Equal(["WebSearch"], resolution.Potential);
    }

    [Fact]
    public void UnknownGatewayDefaultsToEmptyEffectiveAndNeedsExplicitEndpoints()
    {
        var registry = CreateRegistry("{}");
        var source = new FakeSource("OpenAI", "https://selfhost.example.com/v1", format: ["WebSearch"]);

        var resolution = registry.Resolve(source, "gpt-5");

        Assert.False(resolution.KnownVendor);
        Assert.Null(resolution.VendorId);
        // The format still defines the potential ceiling; only the default is empty.
        Assert.Equal(["WebSearch"], resolution.Potential);
        Assert.Empty(resolution.Effective);
    }

    [Fact]
    public void ExplicitEndpointProfilesAddToEffectiveForUnknownGateways()
    {
        var registry = CreateRegistry("""
        {
          "Endpoints": [
            { "Url": "https://selfhost.example.com/v1", "Capabilities": ["WebSearch"] }
          ]
        }
        """);
        var source = new FakeSource("OpenAI", "https://selfhost.example.com/v1/", format: ["WebSearch"]);

        var resolution = registry.Resolve(source, "gpt-5");

        Assert.True(resolution.Matched);
        Assert.False(resolution.KnownVendor);
        Assert.Equal(["WebSearch"], resolution.Potential);
        Assert.Equal(["WebSearch"], resolution.Effective);
    }

    [Fact]
    public void ExplicitVendorWinsOverHostInference()
    {
        var registry = CreateRegistry("""
        {
          "Vendors": {
            "opencode": {
              "Endpoints": ["https://api.openai.com/v1"],
              "Capabilities": ["WebSearch", "Shell"]
            }
          }
        }
        """);
        // The host is api.openai.com, but the explicit Vendor marks the source as opencode.
        var source = new FakeSource(
            "OpenAI",
            "https://api.openai.com/v1",
            vendor: "opencode",
            format: ["WebSearch", "Shell"]);

        var resolution = registry.Resolve(source, "gpt-5");

        Assert.True(resolution.KnownVendor);
        Assert.Equal("opencode", resolution.VendorId);
        Assert.Equivalent(new[] { "Shell", "WebSearch" }, resolution.Potential, strict: true);
    }

    [Fact]
    public void ConfiguredVendorHostMatchesCaseInsensitively()
    {
        var registry = CreateRegistry("""
        {
          "Vendors": {
            "opencode": {
              "Endpoints": ["https://OPENCODE.ai/v1"],
              "Capabilities": ["WebSearch"]
            }
          }
        }
        """);
        var source = new FakeSource("OpenAI", "https://opencode.ai/v1", format: ["WebSearch"]);

        var resolution = registry.Resolve(source, "gpt-5");

        Assert.Equal("opencode", resolution.VendorId);
        Assert.True(resolution.KnownVendor);
        Assert.Equal(["WebSearch"], resolution.Potential);
    }

    [Fact]
    public void CanonicalNamesAreNormalizedAndUnknownNamesPassThrough()
    {
        var registry = CreateRegistry("""
        {
          "Vendors": {
            "opencode": {
              "Endpoints": ["https://opencode.ai/v1"],
              "Capabilities": ["websearch", "MyGateway.SuperTool"]
            }
          }
        }
        """);
        var source = new FakeSource(
            "OpenAI",
            "https://opencode.ai/v1",
            vendor: "opencode",
            format: ["WebSearch", "MyGateway.SuperTool"]);

        var resolution = registry.Resolve(source, "gpt-5");

        // Known names normalize to canonical spelling; unknown names keep the full configured spelling.
        Assert.Equal(["MyGateway.SuperTool", "WebSearch"], resolution.Potential);
    }

    [Fact]
    public void EndpointConfigurationNormalizesKnownNamesAndKeepsUnknownSpelling()
    {
        var registry = CreateRegistry("""
        {
          "Endpoints": [
            {
              "Url": "https://gateway.example.com/v1",
              "Capabilities": ["websearch", "MyGateway.SuperTool"]
            }
          ]
        }
        """);

        var capabilities = registry.Resolve(
            new FakeSource("OpenAI", "https://gateway.example.com/v1"),
            "gpt-5").Effective;

        Assert.Equal(["MyGateway.SuperTool", "WebSearch"], capabilities);
    }

    [Fact]
    public void CapabilityNamesParseCaseInsensitivelyAndCombine()
    {
        var registry = CreateRegistry("""
        {
          "Endpoints": [
            {
              "Url": "https://api.example.com/v1",
              "Capabilities": ["websearch", "FILEsearch", "FileSearch"]
            }
          ]
        }
        """);

        var capabilities = registry.Resolve(
            new FakeSource("OpenAI", "https://api.example.com/v1"),
            "gpt-5").Effective;

        Assert.Equal(["FileSearch", "WebSearch"], capabilities);
    }

    [Fact]
    public void MissingSectionsYieldEmptyRegistry()
    {
        var registry = CreateRegistry("{}");

        Assert.False(registry.Resolve(new FakeSource("OpenAI", "https://api.example.com/v1"), "gpt-5")
            .KnownVendor);
    }

    [Theory]
    [InlineData("https://api.example.com/v1", "https://api.example.com/v1/")]
    [InlineData("https://api.example.com/v1/", "https://api.example.com/v1/")]
    public void DuplicateNormalizedUrlsAreRejected(string first, string second)
    {
        var assertion2 = Assert.ThrowsAny<ArgumentException>(() => CreateRegistry($$"""
        {
          "Endpoints": [
            { "Url": "{{first}}", "Capabilities": ["WebSearch"] },
            { "Url": "{{second}}", "Capabilities": ["Shell"] }
          ]
        }
        """));
        Assert.Matches("(?is)^.*configured\\ more\\ than\\ once.*$", assertion2.Message);
    }

    [Theory]
    [InlineData("ftp://api.example.com/v1")]
    [InlineData("file:///tmp/api")]
    [InlineData("api.example.com/v1")]
    [InlineData("not a url")]
    public void InvalidEndpointUrlsAreRejected(string url)
    {
        var assertion3 = Assert.ThrowsAny<ArgumentException>(() => CreateRegistry($$"""
        {
          "Endpoints": [
            { "Url": "{{url}}", "Capabilities": ["WebSearch"] }
          ]
        }
        """));
        Assert.Matches("(?is)^.*HTTP\\ or\\ HTTPS.*$", assertion3.Message);
    }

    [Fact]
    public void UserInfoInUrlIsRejected()
    {
        var assertion4 = Assert.ThrowsAny<ArgumentException>(() => CreateRegistry("""
        {
          "Endpoints": [
            { "Url": "https://user:secret@api.example.com/v1", "Capabilities": ["WebSearch"] }
          ]
        }
        """));
        Assert.Matches("(?is)^.*user\\ information.*$", assertion4.Message);
    }

    [Fact]
    public void QueryAndFragmentInUrlAreRejected()
    {
        var assertion5 = Assert.ThrowsAny<ArgumentException>(() => CreateRegistry("""
        {
          "Endpoints": [
            { "Url": "https://api.example.com/v1?tenant=one", "Capabilities": ["WebSearch"] }
          ]
        }
        """));
        Assert.Matches("(?is)^.*query\\ or\\ fragment.*$", assertion5.Message);
    }

    [Fact]
    public void SourceEndpointsWithUnsupportedComponentsMatchNothingWithoutThrowing()
    {
        var registry = CreateRegistry("""
        {
          "Endpoints": [
            { "Url": "https://api.example.com/v1", "Capabilities": ["WebSearch"] }
          ]
        }
        """);

        var resolution = registry.Resolve(
            new FakeSource("OpenAI", "https://api.example.com/v1?tenant=one"),
            "gpt-5");

        Assert.False(resolution.Matched);
        Assert.Empty(resolution.Effective);
    }

    [Theory]
    [InlineData("Web Search")]
    [InlineData(".WebSearch")]
    [InlineData("WebSearch.")]
    [InlineData("Web..Search")]
    [InlineData("123")]
    public void GrammarInvalidCapabilityNamesAreRejected(string capability)
    {
        var assertion6 = Assert.ThrowsAny<ArgumentException>(() => CreateRegistry($$"""
        {
          "Endpoints": [
            { "Url": "https://api.example.com/v1", "Capabilities": ["{{capability}}"] }
          ]
        }
        """));
        Assert.Matches("(?is)^.*capability.*$", assertion6.Message);
    }

    [Theory]
    [InlineData("VideoGeneration")]
    [InlineData("Responses.VideoGeneration")]
    [InlineData("Web.Search")]
    [InlineData("MyGateway.SuperTool")]
    public void UnknownCapabilityNamesPassThroughWithConfiguredSpelling(string capability)
    {
        // The capability vocabulary is open: unknown names are kept as opaque capability
        // identifiers with their full configured spelling, so novel vendor capabilities
        // need no code change.
        var registry = CreateRegistry($$"""
        {
          "Endpoints": [
            { "Url": "https://api.example.com/v1", "Capabilities": ["{{capability}}"] }
          ]
        }
        """);

        var effective = registry.Resolve(
            new FakeSource("OpenAI", "https://api.example.com/v1"),
            "gpt-5").Effective;

        Assert.Equal([capability], effective);
    }

    [Fact]
    public void InvalidLimitIsRejected()
    {
        var assertion7 = Assert.ThrowsAny<ArgumentOutOfRangeException>(() => CreateRegistry("""
        {
          "Endpoints": [
            { "Url": "https://api.example.com/v1", "Limits": { "MaxBuiltinToolCalls": 0 } }
          ]
        }
        """));
        Assert.Matches("(?is)^.*at\\ least\\ 1.*$", assertion7.Message);
    }

    [Fact]
    public void EndpointLimitResolvesOnlyWhenTheEndpointMatches()
    {
        var registry = CreateRegistry("""
        {
          "Endpoints": [
            {
              "Url": "https://api.example.com/v1",
              "Capabilities": ["WebSearch"],
              "Limits": { "MaxBuiltinToolCalls": 3 }
            }
          ]
        }
        """);

        Assert.Equal(3, registry
            .Resolve(new FakeSource("OpenAI", "https://api.example.com/v1"), "gpt-5")
            .MaxBuiltinToolCalls);
        Assert.Null(registry
            .Resolve(new FakeSource("OpenAI", "https://other.example.com/v1"), "gpt-5")
            .MaxBuiltinToolCalls);
    }

    [Fact]
    public void MissingUrlIsRejected()
    {
        var assertion8 = Assert.ThrowsAny<ArgumentException>(() => CreateRegistry("""
        {
          "Endpoints": [
            { "Capabilities": ["WebSearch"] }
          ]
        }
        """));
        Assert.Matches("(?is)^.*Url.*$", assertion8.Message);
    }

    [Fact]
    public void VendorEndpointHostsCannotBeClaimedByMultipleVendors()
    {
        var assertion9 = Assert.ThrowsAny<ArgumentException>(() => CreateRegistry("""
        {
          "Vendors": {
            "opencode": { "Endpoints": ["https://gateway.example.com/v1"] },
            "other": { "Endpoints": ["https://gateway.example.com/v1/"] }
          }
        }
        """));
        Assert.Matches("(?is)^.*claimed\\ by\\ both.*$", assertion9.Message);
    }

    [Fact]
    public void InvalidVendorIdentifierIsRejected()
    {
        var assertion10 = Assert.ThrowsAny<ArgumentException>(() => CreateRegistry("""
        {
          "Vendors": {
            "op encode": { "Capabilities": ["WebSearch"] }
          }
        }
        """));
        Assert.Matches("(?is)^.*Vendor\\ identifier.*$", assertion10.Message);
    }

    [Fact]
    public void InvalidVendorEndpointUrlIsRejected()
    {
        var assertion11 = Assert.ThrowsAny<ArgumentException>(() => CreateRegistry("""
        {
          "Vendors": {
            "opencode": { "Endpoints": ["not a url"] }
          }
        }
        """));
        Assert.Matches("(?is)^.*HTTP\\ or\\ HTTPS.*$", assertion11.Message);
    }

    [Fact]
    public void BlankVendorModelIdentifierIsRejected()
    {
        var assertion12 = Assert.ThrowsAny<ArgumentException>(() => CreateRegistry("""
        {
          "Vendors": {
            "opencode": { "Models": { " ": { "Capabilities": ["WebSearch"] } } }
          }
        }
        """));
        Assert.Matches("(?is)^.*model\\ identifiers.*$", assertion12.Message);
    }

    [Fact]
    public void ModelDeclarationsDoNotLeakAcrossVendors()
    {
        var registry = CreateRegistry("""
        {
          "Vendors": {
            "vendor-a": {
              "Capabilities": ["WebSearch"],
              "Models": { "gpt-5": { "Capabilities": ["WebSearch", "Shell"] } }
            },
            "vendor-b": { "Capabilities": ["FileSearch"] }
          }
        }
        """);
        var sourceA = new FakeSource(
            "OpenAI",
            "https://a.example.com/v1",
            vendor: "vendor-a",
            format: ["WebSearch", "Shell", "FileSearch"]);
        var sourceB = new FakeSource(
            "OpenAI",
            "https://b.example.com/v1",
            vendor: "vendor-b",
            format: ["WebSearch", "Shell", "FileSearch"]);

        Assert.Equal(["Shell", "WebSearch"], registry.Resolve(sourceA, "gpt-5").Potential);
        // vendor-b must not inherit vendor-a's model declaration.
        Assert.Equal(["FileSearch"], registry.Resolve(sourceB, "gpt-5").Potential);
    }

    [Fact]
    public void ExplicitVendorWithoutCatalogKnowledgeIsNotKnown()
    {
        var registry = CreateRegistry("{}");
        var source = new FakeSource(
            "OpenAI",
            "https://opencode.ai/v1",
            vendor: "opencode",
            format: ["WebSearch"]);

        var resolution = registry.Resolve(source, "gpt-5");

        Assert.Equal("opencode", resolution.VendorId);
        Assert.False(resolution.KnownVendor);
        // The format still defines the potential ceiling; the default stays empty.
        Assert.Equal(["WebSearch"], resolution.Potential);
        Assert.Empty(resolution.Effective);
    }

    [Fact]
    public void KnownVendorWithExplicitEndpointProfileCombinesBoth()
    {
        var registry = CreateRegistry("""
        {
          "Endpoints": [
            { "Url": "https://api.openai.com/v1", "Capabilities": ["MyGateway.SuperTool"] }
          ]
        }
        """);
        var source = new FakeSource("OpenAI", "https://api.openai.com/v1", format: ["WebSearch"]);

        var resolution = registry.Resolve(source, "gpt-5");

        Assert.True(resolution.KnownVendor);
        Assert.Equal(["WebSearch"], resolution.Potential);
        // The explicit endpoint profile is added on top of the known-vendor default.
        Assert.Equal(["MyGateway.SuperTool", "WebSearch"], resolution.Effective);
    }

    [Fact]
    public void EqualityComparesVendorsAndEndpointsForReloadDetection()
    {
        var left = CreateRegistry("""
        {
          "Vendors": {
            "opencode": {
              "Endpoints": ["https://opencode.ai/v1"],
              "Capabilities": ["WebSearch"],
              "Models": { "gpt-5": { "Capabilities": ["WebSearch", "Shell"] } }
            }
          },
          "Endpoints": [
            { "Url": "https://selfhost.example.com/v1", "Capabilities": ["Shell"] }
          ]
        }
        """);
        var same = CreateRegistry("""
        {
          "Vendors": {
            "opencode": {
              "Endpoints": ["https://OPENCODE.ai/v1/"],
              "Capabilities": ["websearch"],
              "Models": { "gpt-5": { "Capabilities": ["SHELL", "WebSearch"] } }
            }
          },
          "Endpoints": [
            { "Url": "https://selfhost.example.com/v1/", "Capabilities": ["shell"] }
          ]
        }
        """);
        var different = CreateRegistry("""
        {
          "Vendors": {
            "opencode": {
              "Endpoints": ["https://opencode.ai/v1"],
              "Capabilities": ["WebSearch"]
            }
          },
          "Endpoints": []
        }
        """);

        Assert.True(left.Equals(same));
        Assert.False(left.Equals(different));
        Assert.Equal(same.GetHashCode(), left.GetHashCode());
    }

    private static CapabilityRegistry CreateRegistry(string json)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        return CapabilityRegistry.Create(configuration);
    }

    private sealed class FakeSource(
        string providerName,
        string? endpoint = null,
        string? vendor = null,
        IReadOnlyList<string>? format = null) : IConfiguredChatClientSource
    {
        public string ProviderName => providerName;

        public object ClientGenerationKey => "fake";

        public AgentModelCapabilities Capabilities =>
            AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

        public Uri? EndpointUri => endpoint is null ? null : new Uri(endpoint);

        public string? Vendor => vendor;

        public IReadOnlyList<string> FormatCapabilities => format ?? [];

        public IChatClient Create(string model)
        {
            throw new NotSupportedException("The registry test source is not callable.");
        }
    }
}
