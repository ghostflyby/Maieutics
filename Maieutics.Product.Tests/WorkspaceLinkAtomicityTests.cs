using Maieutics.Execution;

namespace Maieutics.Product.Tests;

/// <summary>Derived-state ordering (ADR 0027 follow-up, 2026-09-16): the registry
/// commits first, so every crash leftover is either self-healing (registered link,
/// remount rebuilds it) or inert (unregistered link, traversal rejects it and the next
/// open sweeps it) — never an unowned link and never a resurrected one.</summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class WorkspaceLinkAtomicityTests
{
    [Fact(Timeout = 30_000)]
    public void FailedLinkCreationRollsBackTheRegistryWithoutATombstone()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX-only fault injection: directory mode

        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var first = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "second")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        context.OpenLink(first, null);

        // Making projects/ unwritable fails exactly the physical link creation: the
        // registry lives in .maieutics/ and keeps committing, RemoveStrayLink on an
        // absent path needs no write, and CreateSymbolicLink needs the write bit.
        File.SetUnixFileMode(
            home.ProjectsRoot,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        try
        {
        var exception = Assert.ThrowsAny<Exception>(() => context.OpenLink(second, null));
        Assert.True(exception is IOException or UnauthorizedAccessException, $"the link creation failed with {exception.GetType().Name}");

            var entries = WorkspaceLinkRegistry.Load(home.HomePath).Entries();
            Assert.Equal("first", Assert.Single(entries).Name);
        }
        finally
        {
            File.SetUnixFileMode(
                home.ProjectsRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        // The rolled-back name never entered circulation: no tombstone, so the retry
        // allocates the plain name again instead of a hash suffix.
        var reopened = context.OpenLink(second, null);
        Assert.Equal(2, (reopened.Links ?? throw new InvalidOperationException("missing links")).Records.Count());
        Assert.Contains((reopened.Links ?? throw new InvalidOperationException("missing links")).Records, record => record.Name == "second");
    }

    [Fact(Timeout = 30_000)]
    public async Task RegistryCommittedWithoutLinkIsRepairedByTheNextStartup()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "crash between commit and create",
            TestContext.Current.CancellationToken);

        // The crash leftover of commit-first ordering: a registry entry whose physical
        // link was never created.
        var registry = WorkspaceLinkRegistry.Load(workspace.Path);
        registry.CommitNew(
            new WorkspaceLinkRecord("project", project, null, DateTimeOffset.UtcNow));

        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Assert.True(Directory.Exists(Path.Combine(remounted.ProjectsRoot, "project")), "remount repairs a registered link that was never created");
        Workspace.Create(remounted).Capture().Resolve("workspace://local/projects/project/marker.txt", false);
    }

    [Fact(Timeout = 30_000)]
    public async Task LeftoverLinkAfterCloseStaysInertAndIsSweptByReopen()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "close leftover",
            TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        context.OpenLink(project, null);
        context.CloseLink("project");

        // The crash leftover of tombstone-first ordering: the physical delete never ran.
        ManagedWorkspaceLink.Create(Path.Combine(home.ProjectsRoot, "project"), project);

        var after = context.Capture().Links ?? throw new InvalidOperationException("missing links");
        Assert.Empty(after.Records);
        Assert.Equal("workspace_symbolic_link_not_allowed", Assert.ThrowsAny<WorkspaceException>(() => context.Capture().Resolve("workspace://local/projects/project/marker.txt", false)).Code);

        // Reopening the same target sweeps the leftover and reuses the retired name.
        var reopened = context.OpenLink(project, null);
        Assert.Equal("project", Assert.Single((reopened.Links ?? throw new InvalidOperationException("missing links")).Records).Name);
        context.Capture().Resolve("workspace://local/projects/project/marker.txt", false);
    }
}
