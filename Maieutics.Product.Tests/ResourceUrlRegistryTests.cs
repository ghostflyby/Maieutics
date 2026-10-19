using System.Text;
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

        Assert.True(ResourceRegistry.TryParseUri("notes://db1/table", out var uri));
        Assert.Same(specific, registry.Resolve(uri));

        Assert.True(ResourceRegistry.TryParseUri("notes://db2/table", out var other));
        Assert.Same(wildcard, registry.Resolve(other));
    }

    [Fact]
    public void BuiltInProvidersCannotBeShadowedByLaterClasses()
    {
        var builtIn = new FakeProvider("builtin", ResourceProviderClass.BuiltIn, new ResourceClaim("notes"));
        var custom = new FakeProvider("custom", ResourceProviderClass.Custom, new ResourceClaim("notes"));
        var mcp = new FakeProvider("mcp", ResourceProviderClass.Mcp, new ResourceClaim("notes"));
        var registry = new ResourceRegistry([builtIn, custom, mcp]);

        Assert.True(ResourceRegistry.TryParseUri("notes://any/thing", out var uri));
        Assert.Same(builtIn, registry.Resolve(uri));
        Assert.Empty(registry.GetConflicts());
    }

    [Fact]
    public void DuplicateClaimsInOneClassShadowTheLaterRegistration()
    {
        var first = new FakeProvider("first", ResourceProviderClass.Custom, new ResourceClaim("notes"));
        var second = new FakeProvider("second", ResourceProviderClass.Custom, new ResourceClaim("notes"));
        var registry = new ResourceRegistry([first, second]);

        Assert.True(ResourceRegistry.TryParseUri("notes://a/b", out var uri));
        Assert.Same(first, registry.Resolve(uri));
        var conflict = Assert.Single(registry.GetConflicts());
        Assert.Equal("second", conflict.ProviderId);
        Assert.Equal("duplicate_claim", conflict.Reason);
        Assert.Equal("first", conflict.ShadowedBy);
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
        Assert.Contains(conflicts, conflict =>
            conflict.ProviderId == "custom-workspace" && conflict.Reason == "reserved_scheme");
        Assert.Contains(conflicts, conflict =>
            conflict.ProviderId == "custom-file" && conflict.Reason == "reserved_scheme");
        Assert.DoesNotContain(conflicts, conflict => conflict.ProviderId == "mcp");

        Assert.True(ResourceRegistry.TryParseUri("workspace://local/a.txt", out var uri));
        Assert.Null(registry.Resolve(uri));

        Assert.True(ResourceRegistry.TryParseUri("mcp://server/x", out var escape));
        Assert.Same(mcp, registry.Resolve(escape));
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkspaceProviderReadsFilesThroughWorkspaceContainment()
    {
        using var workspace = TemporaryWorkspace.Create();
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "a.txt"), "content", TestContext.Current.CancellationToken);

        var provider = new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path));

        Assert.True(ResourceRegistry.TryParseUri("workspace://local/a.txt", out _));
        var read = await provider.ReadAsync(
            "workspace://local/a.txt",
            new ResourceReadRequest(1024),
            TestContext.Current.CancellationToken);
        using (read.Content)
        {
            using var buffered = new MemoryStream();
            await read.Content.CopyToAsync(buffered, TestContext.Current.CancellationToken);
            Assert.Equal("content", Encoding.UTF8.GetString(buffered.ToArray()));
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkspaceProviderKeepsItsTypedErrors()
    {
        using var workspace = TemporaryWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "folder"));
        var provider = new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path));

        Func<Task> notFile = async () => await provider.ReadAsync(
            "workspace://local/folder",
            new ResourceReadRequest(1024),
            TestContext.Current.CancellationToken);
        var notFileAssertions = await Assert.ThrowsAnyAsync<WorkspaceException>(async () => { await notFile.Invoke(); });
        Assert.Equal("workspace_not_file", notFileAssertions.Code);

        Func<Task> missing = async () => await provider.ReadAsync(
            "workspace://local/absent.txt",
            new ResourceReadRequest(1024),
            TestContext.Current.CancellationToken);
        var missingAssertions = await Assert.ThrowsAnyAsync<WorkspaceException>(async () => { await missing.Invoke(); });
        Assert.Equal("workspace_path_not_found", missingAssertions.Code);
    }

    [Fact(Timeout = 30_000)]
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
        var assertions = await Assert.ThrowsAnyAsync<ResourceException>(async () => { await tooLarge.Invoke(); });
        Assert.Equal("resource_too_large", assertions.Code);
    }

    [Fact]
    public void ParseUriRejectsRelativeValuesAndFragments()
    {
        Assert.False(ResourceRegistry.TryParseUri("not a uri", out _));
        Assert.False(ResourceRegistry.TryParseUri("", out _));
        Assert.False(ResourceRegistry.TryParseUri("notes://a/b#frag", out _));
        Assert.True(ResourceRegistry.TryParseUri("notes://a/b?q=1", out _));
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
