using FluentAssertions;
using Maieutics.Execution;
using Maieutics.Mcp;

namespace Maieutics.Product.Tests;

public sealed class McpResourceCatalogTests
{
    [Theory]
    [InlineData("postgres://db/{database}/{table}", "postgres://db/sales/orders", true)]
    [InlineData("postgres://db/{database}", "postgres://db/sales", true)]
    [InlineData("postgres://db/{database}", "postgres://db/sales/orders", false)]
    [InlineData("postgres://db/{+path}", "postgres://db/a/b/c", true)]
    [InlineData("test://static", "test://static", true)]
    [InlineData("test://static", "test://staticx", false)]
    [InlineData("test://{?query}", "test://x", false)]
    [InlineData("postgres://other/{database}", "postgres://db/sales", false)]
    public void TemplateMatchingFollowsRfc6570Level1And2(
        string template,
        string candidate,
        bool expected)
    {
        McpResourceTemplateMatcher.Matches(template, candidate).Should().Be(expected);
    }

    [Theory]
    [InlineData("postgres://db.example.com/{database}", "postgres", "db.example.com")]
    [InlineData("notes://{id}", "notes", null)]
    [InlineData("urn:example:{id}", "urn", null)]
    public void TemplateClaimsExtractSchemeAndLiteralAuthorities(
        string template,
        string scheme,
        string? authority)
    {
        var claim = McpResourceTemplateMatcher.TryGetClaim(template);
        claim.Should().NotBeNull();
        claim!.Scheme.Should().Be(scheme);
        claim.Authority.Should().Be(authority);
    }

    [Fact]
    public void CatalogContainsPrefersExactResourcesOverTemplates()
    {
        var catalog = new McpResourceCatalog(
            [new McpResourceDescriptor("test://static/hello", "hello", null, "text/plain")],
            [new McpResourceTemplateDescriptor("test://users/{id}", "users", null, null)]);

        catalog.Contains("test://static/hello").Should().BeTrue();
        catalog.Contains("test://users/42").Should().BeTrue();
        catalog.Contains("test://absent").Should().BeFalse();
    }
}
