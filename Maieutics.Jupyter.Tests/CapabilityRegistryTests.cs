using System.Text;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Maieutics.Jupyter.Tests;

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

        resolution.KnownVendor.Should().BeTrue();
        resolution.VendorId.Should().Be("openai");
        resolution.Matched.Should().BeFalse();
        resolution.Potential.Should().Equal(["WebSearch"]);
        resolution.Effective.Should().Equal(resolution.Potential);
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

        resolution.KnownVendor.Should().BeTrue();
        resolution.Potential.Should().BeEquivalentTo(["FileSearch", "WebSearch"], options => options.WithStrictOrdering());
        resolution.Effective.Should().Equal(resolution.Potential);
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
        narrowed.Potential.Should().Equal(["WebSearch"]);

        // A model without a declaration falls back to the vendor aggregate.
        var aggregate = registry.Resolve(source, "gpt-4");
        aggregate.Potential.Should().BeEquivalentTo(["Shell", "WebSearch"], options => options.WithStrictOrdering());
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

        resolution.KnownVendor.Should().BeTrue();
        resolution.Potential.Should().BeEmpty();
        resolution.Effective.Should().BeEmpty();
    }

    [Fact]
    public void EmptyEndpointFallsBackToProviderDefaultVendorHost()
    {
        var registry = CreateRegistry("{}");
        var source = new FakeSource("OpenAI", endpoint: null, format: ["WebSearch"]);

        var resolution = registry.Resolve(source, "gpt-5");

        resolution.KnownVendor.Should().BeTrue();
        resolution.VendorId.Should().Be("openai");
        resolution.Potential.Should().Equal(["WebSearch"]);
    }

    [Fact]
    public void UnknownGatewayDefaultsToEmptyEffectiveAndNeedsExplicitEndpoints()
    {
        var registry = CreateRegistry("{}");
        var source = new FakeSource("OpenAI", "https://selfhost.example.com/v1", format: ["WebSearch"]);

        var resolution = registry.Resolve(source, "gpt-5");

        resolution.KnownVendor.Should().BeFalse();
        resolution.VendorId.Should().BeNull();
        // The format still defines the potential ceiling; only the default is empty.
        resolution.Potential.Should().Equal(["WebSearch"]);
        resolution.Effective.Should().BeEmpty();
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

        resolution.Matched.Should().BeTrue();
        resolution.KnownVendor.Should().BeFalse();
        resolution.Potential.Should().Equal(["WebSearch"]);
        resolution.Effective.Should().Equal(["WebSearch"]);
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

        resolution.KnownVendor.Should().BeTrue();
        resolution.VendorId.Should().Be("opencode");
        resolution.Potential.Should().BeEquivalentTo(["Shell", "WebSearch"], options => options.WithStrictOrdering());
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

        resolution.VendorId.Should().Be("opencode");
        resolution.KnownVendor.Should().BeTrue();
        resolution.Potential.Should().Equal(["WebSearch"]);
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
        resolution.Potential.Should().Equal(["MyGateway.SuperTool", "WebSearch"]);
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

        capabilities.Should().Equal(["MyGateway.SuperTool", "WebSearch"]);
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

        capabilities.Should().Equal(["FileSearch", "WebSearch"]);
    }

    [Fact]
    public void MissingSectionsYieldEmptyRegistry()
    {
        var registry = CreateRegistry("{}");

        registry.Resolve(new FakeSource("OpenAI", "https://api.example.com/v1"), "gpt-5")
            .KnownVendor.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://api.example.com/v1", "https://api.example.com/v1/")]
    [InlineData("https://api.example.com/v1/", "https://api.example.com/v1/")]
    public void DuplicateNormalizedUrlsAreRejected(string first, string second)
    {
        var action = () => CreateRegistry($$"""
        {
          "Endpoints": [
            { "Url": "{{first}}", "Capabilities": ["WebSearch"] },
            { "Url": "{{second}}", "Capabilities": ["Shell"] }
          ]
        }
        """);

        action.Should().Throw<ArgumentException>().WithMessage("*configured more than once*");
    }

    [Theory]
    [InlineData("ftp://api.example.com/v1")]
    [InlineData("file:///tmp/api")]
    [InlineData("api.example.com/v1")]
    [InlineData("not a url")]
    public void InvalidEndpointUrlsAreRejected(string url)
    {
        var action = () => CreateRegistry($$"""
        {
          "Endpoints": [
            { "Url": "{{url}}", "Capabilities": ["WebSearch"] }
          ]
        }
        """);

        action.Should().Throw<ArgumentException>().WithMessage("*HTTP or HTTPS*");
    }

    [Fact]
    public void UserInfoInUrlIsRejected()
    {
        var action = () => CreateRegistry("""
        {
          "Endpoints": [
            { "Url": "https://user:secret@api.example.com/v1", "Capabilities": ["WebSearch"] }
          ]
        }
        """);

        action.Should().Throw<ArgumentException>().WithMessage("*user information*");
    }

    [Fact]
    public void QueryAndFragmentInUrlAreRejected()
    {
        var action = () => CreateRegistry("""
        {
          "Endpoints": [
            { "Url": "https://api.example.com/v1?tenant=one", "Capabilities": ["WebSearch"] }
          ]
        }
        """);

        action.Should().Throw<ArgumentException>().WithMessage("*query or fragment*");
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

        resolution.Matched.Should().BeFalse();
        resolution.Effective.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Web Search")]
    [InlineData(".WebSearch")]
    [InlineData("WebSearch.")]
    [InlineData("Web..Search")]
    [InlineData("123")]
    public void GrammarInvalidCapabilityNamesAreRejected(string capability)
    {
        var action = () => CreateRegistry($$"""
        {
          "Endpoints": [
            { "Url": "https://api.example.com/v1", "Capabilities": ["{{capability}}"] }
          ]
        }
        """);

        action.Should().Throw<ArgumentException>().WithMessage("*capability*");
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

        effective.Should().Equal([capability]);
    }

    [Fact]
    public void InvalidLimitIsRejected()
    {
        var action = () => CreateRegistry("""
        {
          "Endpoints": [
            { "Url": "https://api.example.com/v1", "Limits": { "MaxBuiltinToolCalls": 0 } }
          ]
        }
        """);

        action.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*at least 1*");
    }

    [Fact]
    public void MissingUrlIsRejected()
    {
        var action = () => CreateRegistry("""
        {
          "Endpoints": [
            { "Capabilities": ["WebSearch"] }
          ]
        }
        """);

        action.Should().Throw<ArgumentException>().WithMessage("*Url*");
    }

    [Fact]
    public void VendorEndpointHostsCannotBeClaimedByMultipleVendors()
    {
        var action = () => CreateRegistry("""
        {
          "Vendors": {
            "opencode": { "Endpoints": ["https://gateway.example.com/v1"] },
            "other": { "Endpoints": ["https://gateway.example.com/v1/"] }
          }
        }
        """);

        action.Should().Throw<ArgumentException>().WithMessage("*claimed by both*");
    }

    [Fact]
    public void InvalidVendorIdentifierIsRejected()
    {
        var action = () => CreateRegistry("""
        {
          "Vendors": {
            "op encode": { "Capabilities": ["WebSearch"] }
          }
        }
        """);

        action.Should().Throw<ArgumentException>().WithMessage("*Vendor identifier*");
    }

    [Fact]
    public void InvalidVendorEndpointUrlIsRejected()
    {
        var action = () => CreateRegistry("""
        {
          "Vendors": {
            "opencode": { "Endpoints": ["not a url"] }
          }
        }
        """);

        action.Should().Throw<ArgumentException>().WithMessage("*HTTP or HTTPS*");
    }

    [Fact]
    public void BlankVendorModelIdentifierIsRejected()
    {
        var action = () => CreateRegistry("""
        {
          "Vendors": {
            "opencode": { "Models": { " ": { "Capabilities": ["WebSearch"] } } }
          }
        }
        """);

        action.Should().Throw<ArgumentException>().WithMessage("*model identifiers*");
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

        registry.Resolve(sourceA, "gpt-5").Potential.Should().Equal(["Shell", "WebSearch"]);
        // vendor-b must not inherit vendor-a's model declaration.
        registry.Resolve(sourceB, "gpt-5").Potential.Should().Equal(["FileSearch"]);
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

        resolution.VendorId.Should().Be("opencode");
        resolution.KnownVendor.Should().BeFalse();
        // The format still defines the potential ceiling; the default stays empty.
        resolution.Potential.Should().Equal(["WebSearch"]);
        resolution.Effective.Should().BeEmpty();
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

        resolution.KnownVendor.Should().BeTrue();
        resolution.Potential.Should().Equal(["WebSearch"]);
        // The explicit endpoint profile is added on top of the known-vendor default.
        resolution.Effective.Should().Equal(["MyGateway.SuperTool", "WebSearch"]);
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

        left.Equals(same).Should().BeTrue();
        left.Equals(different).Should().BeFalse();
        left.GetHashCode().Should().Be(same.GetHashCode());
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
