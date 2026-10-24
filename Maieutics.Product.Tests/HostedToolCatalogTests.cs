using Maieutics.Providers;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

public sealed class HostedToolCatalogTests
{
    [Fact]
    public void CatalogMapsWebSearchAndSkipsNamesWithoutANeutralToolType()
    {
        var tools = HostedToolCatalog.Create(["ApplyPatch", "WebSearch", "CodeInterpreter", "Shell"]);

        var item = Assert.Single(tools);
        Assert.IsType<HostedWebSearchTool>(item);
    }

    [Fact]
    public void CatalogIsEmptyForNoCapabilities() =>
        Assert.Empty(HostedToolCatalog.Create([]));

    [Fact]
    public void CatalogDeduplicatesRepeatedCapabilityNames()
    {
        var tools = HostedToolCatalog.Create(["WebSearch", "websearch"]);

        Assert.Single(tools);
    }
}
