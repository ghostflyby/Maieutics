using System.Security.Cryptography;
using System.Text;
using Maieutics.Execution;
using static Maieutics.Product.Tests.WorkspaceToolInvocations;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class WorkspaceLinksTests
{
    [Theory]
    [InlineData("my-app", "my-app")]
    [InlineData("my app", "my app")]
    [InlineData("app/variant", "app_variant")]
    [InlineData("trailing.", "trailing")]
    [InlineData("CON", "")]
    // The Windows-invalid character set is fixed (ADR 0027 §5), so these sanitize
    // identically on every host instead of via Path.GetInvalidFileNameChars.
    [InlineData("a:b", "a_b")]
    [InlineData("x*y?z|w", "x_y_z_w")]
    [InlineData("say \"hi\"", "say _hi_")]
    public void RegistrySanitizesNameCandidates(string candidate, string expected)
    {
        Assert.Equal(expected, WorkspaceLinkRegistry.SanitizeName(candidate));
    }

    [Fact(Timeout = 30_000)]
    public void RegistryAllocatesStableHashSuffixesAndRetiresNames()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var first = Directory.CreateDirectory(Path.Combine(workspace.Path, "shared-name")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(workspace.Path, "nested", "shared-name")).FullName;
        var registry = WorkspaceLinkRegistry.Load(workspace.Path);

        var firstName = registry.AllocateName(first, null);
        Assert.Equal("shared-name", firstName);
        registry.CommitNew(new WorkspaceLinkRecord(firstName, first, null, DateTimeOffset.UtcNow));

        var secondName = registry.AllocateName(second, null);
        Assert.Equal($"shared-name-{TargetHash(second, 6)}", secondName);
        Assert.Equal(secondName, WorkspaceLinkRegistry.Load(workspace.Path).AllocateName(second, null));

        var record = new WorkspaceLinkRecord(firstName, first, null, DateTimeOffset.UtcNow);
        registry.CommitNew(record);
        registry.Remove(firstName);
        Assert.Equal(firstName, registry.AllocateName(first, null));
        Assert.NotEqual("shared-name", registry.AllocateName(second, null));
    }

    [Fact(Timeout = 30_000)]
    public async Task OpenResolvesReadsAndSearchesThroughTheManagedHop()
    {
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "linked project", "src")).Parent!.FullName;
        var source = Path.Combine(project, "src", "main.cs");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        await File.WriteAllTextAsync(source, "needle in project", TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        var functions = new WorkspaceFunctions(context);

        var versionBefore = context.Capture().Version;
        context.OpenLink(project, null);
        Assert.Equal(versionBefore + 1, context.Capture().Version);

        Assert.True(Directory.Exists(Path.Combine(home.ProjectsRoot, "linked project")), "the physical link is created under projects/");

        var snapshot = context.Capture();
        var resolved = snapshot.Resolve("workspace://local/projects/linked%20project/src/main.cs", false);
        Assert.Equal(Path.GetFullPath(source), resolved.FullPath);
        Assert.Equal("workspace://local/projects/linked%20project/src/main.cs", resolved.Uri);

        var read = Result<ReadTextResult>(await InvokeAsync(
                Function(functions, "read_text"),
                """{"uri":"workspace://local/projects/linked%20project/src/main.cs"}"""),
            WorkspaceJsonSerializerContext.Default.ReadTextResult);
        Assert.Equal("needle in project", read.Text);

        var search = Result<SearchTextResult>(await InvokeAsync(
                Function(functions, "search_text"),
                """{"query":"needle"}"""),
            WorkspaceJsonSerializerContext.Default.SearchTextResult);
        Assert.Equal("workspace://local/projects/linked%20project/src/main.cs", Assert.Single(search.Matches).Uri);

        var listed = Result<ListDirectoryResult>(await InvokeAsync(
                Function(functions, "list_directory"),
                """{"uri":"workspace://local/projects/linked%20project"}"""),
            WorkspaceJsonSerializerContext.Default.ListDirectoryResult);
        Assert.Equal("src", Assert.Single(listed.Entries).Name);
    }

    [Fact(Timeout = 30_000)]
    public void OpenIsIdempotentAndStartupRemountsRegisteredLinks()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);

        var first = context.OpenLink(project, null);
        var second = context.OpenLink(project, null);
        Assert.NotNull(second.Links);
        Assert.Equal("project", Assert.Single((first.Links ?? throw new InvalidOperationException("missing links")).Records).Name);
        Assert.Single((second.Links ?? throw new InvalidOperationException("missing links")).Records);

        Assert.True(File.Exists(Path.Combine(workspace.Path, WorkspaceHome.StateDirectoryName, "registry.json")), "the registry is persisted inside the home state directory");

        Directory.Delete(Path.Combine(home.ProjectsRoot, "project"));
        WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Assert.True(Directory.Exists(Path.Combine(home.ProjectsRoot, "project")), "startup remounts registered links whose target still exists");
    }

    [Fact(Timeout = 30_000)]
    public void ForeignLinksStateDirectoryAndInTargetLinksStayRejected()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project", "inner")).Parent!.FullName;
        Directory.CreateDirectory(Path.Combine(project, "inner"));
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        context.OpenLink(project, null);
        var snapshot = context.Capture();

        var foreign = Path.Combine(workspace.Path, "foreign");
        ManagedWorkspaceLink.Create(foreign, workspace.ParentPath);
        Assert.Equal("workspace_symbolic_link_not_allowed", Assert.ThrowsAny<WorkspaceException>(() => snapshot.Resolve("workspace://local/foreign", false)).Code);

        var unregistered = Path.Combine(home.ProjectsRoot, "unregistered");
        ManagedWorkspaceLink.Create(unregistered, workspace.ParentPath);
        Assert.Equal("workspace_symbolic_link_not_allowed", Assert.ThrowsAny<WorkspaceException>(() => snapshot.Resolve("workspace://local/projects/unregistered", false)).Code);

        var retargeted = Path.Combine(home.ProjectsRoot, "project");
        Directory.Delete(retargeted);
        ManagedWorkspaceLink.Create(retargeted, workspace.Path);
        Assert.Equal("workspace_symbolic_link_not_allowed", Assert.ThrowsAny<WorkspaceException>(() => snapshot.Resolve("workspace://local/projects/project/inner", false)).Code);

        Assert.Equal("workspace_path_denied", Assert.ThrowsAny<WorkspaceException>(() => snapshot.Resolve("workspace://local/.maieutics/registry.json", false)).Code);

        Directory.Delete(retargeted);
        ManagedWorkspaceLink.Create(retargeted, project);
        var insideTarget = Path.Combine(project, "inner", "escape");
        ManagedWorkspaceLink.Create(insideTarget, workspace.Path);
        Assert.Equal("workspace_symbolic_link_not_allowed", Assert.ThrowsAny<WorkspaceException>(() => snapshot.Resolve("workspace://local/projects/project/inner/escape", false)).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task CloseRemovesOnlyTheLinkAndPermissionVariablesExposeTargets()
    {
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "Project")).FullName;
        var marker = Path.Combine(project, "keep.txt");
        await File.WriteAllTextAsync(marker, "kept", TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        context.OpenLink(project, null);

        var variables = (IPermissionVariableSource)context;
        Assert.Equal(home.HomePath, variables.GetVariable("workspace"));
        Assert.Equal(Path.GetFullPath(project), variables.GetVariable("project.project"));
        Assert.Equal(Path.GetFullPath(project), variables.GetVariable("project.PROJECT"));
        Assert.Null(variables.GetVariable("project.missing"));

        context.CloseLink("Project");
        Assert.False(Directory.Exists(Path.Combine(home.ProjectsRoot, "Project")));
        Assert.True(File.Exists(marker), "closing a link never deletes the target");
        Assert.Null(variables.GetVariable("project.Project"));

        var reopened = context.OpenLink(project, null);
        Assert.Equal("Project", Assert.Single((reopened.Links ?? throw new InvalidOperationException("missing links")).Records).Name);
    }

    [Fact(Timeout = 30_000)]
    public async Task LinkAwareTreeDeletionRemovesTheLinkButNeverTheTarget()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        var marker = Path.Combine(project, "keep.txt");
        await File.WriteAllTextAsync(marker, "kept", TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        TempArea.DeleteTree(home.ProjectsRoot);
        Assert.True(File.Exists(marker), "link-aware deletion of product-owned workspace state must not follow managed links");
    }

    [Fact(Timeout = 30_000)]
    public void OpeningFailsWithTypedErrorsForMissingUnsupportedAndCyclingTargets()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);

        Assert.ThrowsAny<DirectoryNotFoundException>(() => context.OpenLink(
            Path.Combine(workspace.ParentPath, "missing"),
            null));

        if (OperatingSystem.IsWindows())
        {
            var assertion42 = Assert.ThrowsAny<ArgumentException>(() => context.OpenLink(@"\\server\share\project", null));
            Assert.Matches("(?is)^.*workspace\\ link\\ target.*$", assertion42.Message);
        }

        var assertion43 = Assert.ThrowsAny<ArgumentException>(() => context.OpenLink(workspace.Path, null));
        Assert.Matches("(?is)^.*contain,\\ or\\ be\\ contained\\ by,\\ the\\ workspace\\ home.*$", assertion43.Message);
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkspaceCommandsOpenRenderAndCloseLinks()
    {
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "cli project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var executor = new Commands.MaieuticsCommandExecutor(
            null,
            null,
            Workspace.Create(home),
            null,
            null);

        var opened = await executor.ExecuteAsync(
            $"%workspace open {project} as work",
            null,
            TestContext.Current.CancellationToken);
        Assert.Contains("fixed home", opened.Markdown);
        Assert.Contains("`work`", opened.Markdown);

        var current = await executor.ExecuteAsync(
            "%workspace current",
            null,
            TestContext.Current.CancellationToken);
        Assert.Contains("`work`", current.Markdown);
        Assert.Contains(project, current.Markdown);

        var closed = await executor.ExecuteAsync(
            "%workspace close work",
            null,
            TestContext.Current.CancellationToken);
        Assert.Contains("Projects: none", closed.Markdown);

        await Assert.ThrowsAnyAsync<Commands.MaieuticsCommandException>(async () =>
        {
            await executor.ExecuteAsync(
                "%workspace use somewhere",
                null,
                TestContext.Current.CancellationToken);
        });
    }

    private static string TargetHash(string canonicalTarget, int characters)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalTarget)));
        return hex[..characters].ToLowerInvariant();
    }

    [Fact]
    public void OpenRejectsATargetReachedThroughASymlinkedAncestor()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The test exercises symlink semantics, which are POSIX-only.");


        // The home is reachable through a symlinked ancestor, so the raw target string looks
        // like it is outside the home while it physically resolves inside it. Resolving only the
        // final component (ResolveLinkTarget) misses this and would let the home be aliased.
        using var workspace = TemporaryWorkspace.Create();
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var alias = Path.Combine(workspace.ParentPath, "home-alias");
        Directory.CreateSymbolicLink(alias, workspace.Path);
        var viaAncestor = Path.Combine(alias, WorkspaceHome.ProjectsDirectoryName);

        // The canonical form must land on the home's projects directory. The comparison is made
        // on resolved forms because the temp path itself sits under a platform alias (macOS
        // /var -> /private/var), which is not what this test is about.
        var canonical = WorkspaceHome.CanonicalizeTarget(viaAncestor);
        var expected = WorkspaceHome.CanonicalizeTarget(home.ProjectsRoot);
        Assert.Equal(expected, canonical);

        var context = Workspace.Create(home);
        Assert.ThrowsAny<ArgumentException>(() => context.OpenLink(viaAncestor, null));
    }

    [Fact]
    public void OpenRejectsTheHomeAndItsAncestors()
    {
        using var workspace = TemporaryWorkspace.Create();
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);

        Assert.ThrowsAny<ArgumentException>(() => context.OpenLink(home.HomePath, null));

        Assert.ThrowsAny<ArgumentException>(() => context.OpenLink(workspace.ParentPath, null));
    }
}
