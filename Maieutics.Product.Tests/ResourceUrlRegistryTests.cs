using System.Text;
using FluentAssertions;
using Maieutics.Execution;

namespace Maieutics.Product.Tests;

public sealed class ResourceUrlRegistryTests
{
    [Fact]
    public void AuthorityClaimsBeatSchemeWildcards()
    {
        var wildcard = new FakeProvider("wildcard", ResourceProviderClass.Custom, new ResourceClaim("notes"));
        var specific = new FakeProvider("specific", ResourceProviderClass.Custom, new ResourceClaim("notes", "db1"));
        var registry = new ResourceRegistry([wildcard, specific]);

        ResourceRegistry.TryParseUri("notes://db1/table", out var uri).Should().BeTrue();
        registry.Resolve(uri).Should().BeSameAs(specific);

        ResourceRegistry.TryParseUri("notes://db2/table", out var other).Should().BeTrue();
        registry.Resolve(other).Should().BeSameAs(wildcard);
    }

    [Fact]
    public void BuiltInProvidersCannotBeShadowedByLaterClasses()
    {
        var builtIn = new FakeProvider("builtin", ResourceProviderClass.BuiltIn, new ResourceClaim("notes"));
        var custom = new FakeProvider("custom", ResourceProviderClass.Custom, new ResourceClaim("notes"));
        var mcp = new FakeProvider("mcp", ResourceProviderClass.Mcp, new ResourceClaim("notes"));
        var registry = new ResourceRegistry([builtIn, custom, mcp]);

        ResourceRegistry.TryParseUri("notes://any/thing", out var uri).Should().BeTrue();
        registry.Resolve(uri).Should().BeSameAs(builtIn);
        registry.GetConflicts().Should().BeEmpty("different classes resolve by precedence, not conflict");
    }

    [Fact]
    public void DuplicateClaimsInOneClassShadowTheLaterRegistration()
    {
        var first = new FakeProvider("first", ResourceProviderClass.Custom, new ResourceClaim("notes"));
        var second = new FakeProvider("second", ResourceProviderClass.Custom, new ResourceClaim("notes"));
        var registry = new ResourceRegistry([first, second]);

        ResourceRegistry.TryParseUri("notes://a/b", out var uri).Should().BeTrue();
        registry.Resolve(uri).Should().BeSameAs(first);
        var conflict = registry.GetConflicts().Should().ContainSingle().Which;
        conflict.ProviderId.Should().Be("second");
        conflict.Reason.Should().Be("duplicate_claim");
        conflict.ShadowedBy.Should().Be("first");
    }

    [Fact]
    public void ReservedSchemesRejectNonOwningClaims()
    {
        var customWorkspace = new FakeProvider(
            "custom-workspace", ResourceProviderClass.Custom, new ResourceClaim("workspace", "local"));
        var customFile = new FakeProvider("custom-file", ResourceProviderClass.Custom, new ResourceClaim("file"));
        var mcp = new FakeProvider("mcp", ResourceProviderClass.Mcp, new ResourceClaim("mcp"));
        var registry = new ResourceRegistry([customWorkspace, customFile, mcp]);

        var conflicts = registry.GetConflicts();
        conflicts.Should().Contain(conflict =>
            conflict.ProviderId == "custom-workspace" && conflict.Reason == "reserved_scheme");
        conflicts.Should().Contain(conflict =>
            conflict.ProviderId == "custom-file" && conflict.Reason == "reserved_scheme");
        conflicts.Should().NotContain(conflict => conflict.ProviderId == "mcp");

        ResourceRegistry.TryParseUri("workspace://local/a.txt", out var uri).Should().BeTrue();
        registry.Resolve(uri).Should().BeNull("the reserved claim is disabled, not honored");

        ResourceRegistry.TryParseUri("mcp://server/x", out var escape).Should().BeTrue();
        registry.Resolve(escape).Should().BeSameAs(mcp, "the owning class keeps its scheme");
    }

    [Fact]
    public async Task WorkspaceProviderReadsFilesThroughWorkspaceContainment()
    {
        using var workspace = TemporaryWorkspace.Create();
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "a.txt"), "content", TestContext.Current.CancellationToken);

        var provider = new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path));

        ResourceRegistry.TryParseUri("workspace://local/a.txt", out var uri).Should().BeTrue();
        var read = await provider.ReadAsync(
            "workspace://local/a.txt",
            new ResourceReadRequest(1024),
            TestContext.Current.CancellationToken);
        using (read.Content)
        {
            using var buffered = new MemoryStream();
            read.Content.CopyTo(buffered);
            Encoding.UTF8.GetString(buffered.ToArray()).Should().Be("content");
        }
    }

    [Fact]
    public async Task WorkspaceProviderKeepsItsTypedErrors()
    {
        using var workspace = TemporaryWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "folder"));
        var provider = new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path));

        Func<Task> notFile = async () => await provider.ReadAsync(
            "workspace://local/folder",
            new ResourceReadRequest(1024),
            TestContext.Current.CancellationToken);
        var notFileAssertions = await notFile.Should().ThrowAsync<WorkspaceException>();
        notFileAssertions.Which.Code.Should().Be("workspace_not_file");

        Func<Task> missing = async () => await provider.ReadAsync(
            "workspace://local/absent.txt",
            new ResourceReadRequest(1024),
            TestContext.Current.CancellationToken);
        var missingAssertions = await missing.Should().ThrowAsync<WorkspaceException>();
        missingAssertions.Which.Code.Should().Be("workspace_path_not_found");
    }

    [Fact]
    public async Task WorkspaceProviderRefusesBodiesBeyondTheLimit()
    {
        using var workspace = TemporaryWorkspace.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "big.txt"),
            new string('x', 64),
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        var provider = new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path));

        Func<Task> tooLarge = async () => await provider.ReadAsync(
            "workspace://local/big.txt",
            new ResourceReadRequest(8),
            TestContext.Current.CancellationToken);
        var assertions = await tooLarge.Should().ThrowAsync<ResourceException>();
        assertions.Which.Code.Should().Be("resource_too_large");
    }

    [Fact]
    public void ParseUriRejectsRelativeValuesAndFragments()
    {
        ResourceRegistry.TryParseUri("not a uri", out _).Should().BeFalse();
        ResourceRegistry.TryParseUri("", out _).Should().BeFalse();
        ResourceRegistry.TryParseUri("notes://a/b#frag", out _).Should().BeFalse();
        ResourceRegistry.TryParseUri("notes://a/b?q=1", out _).Should().BeTrue();
    }

    private sealed class FakeProvider(
        string id,
        ResourceProviderClass providerClass,
        params ResourceClaim[] claims) : IResourceProvider
    {
        public string Id => id;

        public ResourceProviderClass Class => providerClass;

        public IReadOnlyList<ResourceClaim> Claims { get; } = claims;

        public ValueTask<ResourceReadResult> ReadAsync(
            string uri,
            ResourceReadRequest request,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes($"content of {uri}");
            return ValueTask.FromResult(new ResourceReadResult(new MemoryStream(bytes), "text/plain"));
        }
    }
}
