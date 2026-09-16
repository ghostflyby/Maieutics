using System.Text.Json;
using FluentAssertions;
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
        var exception = context.Invoking(c => c.OpenLink(second, null))
            .Should().Throw<Exception>(
                "the physical link cannot be created in an unwritable directory").Which;
        (exception is IOException or UnauthorizedAccessException)
            .Should().BeTrue($"the link creation failed with {exception.GetType().Name}");

            var entries = WorkspaceLinkRegistry.Load(home.HomePath).Entries();
            entries.Should().ContainSingle("the failed open rolled back")
                .Which.Name.Should().Be("first");
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
        (reopened.Links ?? throw new InvalidOperationException("missing links")).Records
            .Should().HaveCount(2).And.Contain(record => record.Name == "second");
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
        Directory.Exists(Path.Combine(remounted.ProjectsRoot, "project"))
            .Should().BeTrue("remount repairs a registered link that was never created");
        Workspace.Create(remounted).Capture()
            .Invoking(s => s.Resolve("workspace://local/projects/project/marker.txt", false))
            .Should().NotThrow();
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
        after.Records.Should().BeEmpty("the registry no longer knows this link");
        context.Capture().Invoking(s => s.Resolve("workspace://local/projects/project/marker.txt", false))
            .Should().Throw<WorkspaceException>()
            .Which.Code.Should().Be("workspace_symbolic_link_not_allowed",
                "an unregistered link is inert: traversal rejects it");

        // Reopening the same target sweeps the leftover and reuses the retired name.
        var reopened = context.OpenLink(project, null);
        (reopened.Links ?? throw new InvalidOperationException("missing links")).Records
            .Should().ContainSingle().Which.Name.Should().Be("project");
        context.Capture().Invoking(s => s.Resolve("workspace://local/projects/project/marker.txt", false))
            .Should().NotThrow();
    }
}
