using FluentAssertions;
using Maieutics.Providers;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

public sealed class HostedToolCatalogTests
{
    [Fact]
    public void CatalogMapsWebSearchAndSkipsNamesWithoutANeutralToolType()
    {
        var tools = HostedToolCatalog.Create(["ApplyPatch", "WebSearch", "CodeInterpreter", "Shell"]);

        tools.Should().HaveCount(1);
        tools[0].Should().BeOfType<HostedWebSearchTool>();
    }

    [Fact]
    public void CatalogIsEmptyForNoCapabilities() =>
        HostedToolCatalog.Create([]).Should().BeEmpty();

    [Fact]
    public void CatalogDeduplicatesRepeatedCapabilityNames()
    {
        var tools = HostedToolCatalog.Create(["WebSearch", "websearch"]);

        tools.Should().ContainSingle();
    }
}
