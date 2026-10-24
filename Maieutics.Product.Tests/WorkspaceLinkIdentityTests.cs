using System.Runtime.InteropServices;
using System.Text.Json;
using Maieutics.Execution;

namespace Maieutics.Product.Tests;

/// <summary>The rename-surviving file identity of managed project links (ADR 0027
/// amendment): capture at open, re-location by fingerprint, and the withheld state when
/// the registered path holds a different directory.</summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class WorkspaceLinkIdentityTests
{
    [Fact(Timeout = 30_000)]
    public void OpenCapturesTheTargetIdentityAndPersistsIt()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);

        var record = Assert.Single((context.OpenLink(project, null).Links ?? throw new InvalidOperationException("missing links"))
            .Records);
        var identity = record.Identity;
        Assert.NotNull(identity);
        Assert.NotEqual(0UL, identity.Inode);

        var persisted = Assert.Single(WorkspaceLinkRegistry.Load(home.HomePath).Entries());
        Assert.NotNull(persisted.Identity);
        Assert.True(persisted.Identity!.Matches(identity));

        if (!OperatingSystem.IsLinux())
        {
            Assert.NotNull(identity.BirthTimeUtc);
            Assert.True(identity.BirthTimeUtc <= DateTimeOffset.UtcNow.AddMinutes(1));
        }

        var recaptured = WorkspaceFileIdentityReader.TryRead(project);
        Assert.NotNull(recaptured);
        Assert.True(identity.Matches(recaptured), "recapturing the same directory is stable");
    }

    [Fact(Timeout = 30_000)]
    public async Task OpeningTheMovedProjectRePointsTheExistingRecord()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "same object",
            TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        context.OpenLink(project, null);

        var movedPath = Path.Combine(workspace.ParentPath, "renamed");
        Directory.Move(project, movedPath);
        Assert.NotNull(context.OpenLink(movedPath, null));

        var links = context.Capture().Links ?? throw new InvalidOperationException("missing links");
        Assert.Equal(movedPath, Assert.Single(links.Records).Target);

        var resolved = context.Capture().Resolve("workspace://local/projects/project/marker.txt", false);
        Assert.Equal(Path.Combine(movedPath, "marker.txt"), resolved.FullPath);
    }

    [Fact(Timeout = 30_000)]
    public async Task StartupRemountRelocatesARenamedTargetWithinItsParent()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "moved between boots",
            TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        var movedPath = Path.Combine(workspace.ParentPath, "renamed");
        Directory.Move(project, movedPath);
        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(remounted);
        var links = context.Capture().Links ?? throw new InvalidOperationException("missing links");

        Assert.Equal(movedPath, Assert.Single(links.Records).Target);
        Assert.Equal(WorkspaceLinkState.Relocated, links.HealthByName["project"].State);
        Assert.True(Directory.Exists(Path.Combine(remounted.ProjectsRoot, "project")), "the physical link is repaired at the re-pointed target");

        var resolved = context.Capture().Resolve("workspace://local/projects/project/marker.txt", false);
        Assert.Equal(Path.Combine(movedPath, "marker.txt"), resolved.FullPath);

        var executor = new Commands.MaieuticsCommandExecutor(null, null, context, null, null);
        var rendered = await executor.ExecuteAsync(
            "%workspace current",
            null,
            TestContext.Current.CancellationToken);
        Assert.Contains("relocated", rendered.Markdown);
    }

    [Fact(Timeout = 30_000)]
    public async Task RemountWithholdsTheLinkWhenThePathHoldsADifferentDirectory()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "original project",
            TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        // A different project now occupies the registered path. It is built beside the
        // original and swapped in, so it coexisted with the original and is guaranteed a
        // different identity on every file system — no reliance on birth-time precision.
        var replacementSource = Directory.CreateDirectory(
            Path.Combine(workspace.ParentPath, "project-replacement")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(replacementSource, "marker.txt"),
            "replacement project",
            TestContext.Current.CancellationToken);
        await TempArea.DeleteAsync(project, CancellationToken.None);
        Directory.Move(replacementSource, project);
        var replacement = project;

        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(remounted);
        var links = context.Capture().Links ?? throw new InvalidOperationException("missing links");

        Assert.Single(links.Records);
        Assert.Equal(WorkspaceLinkState.IdentityChanged, links.HealthByName["project"].State);
        Assert.False(Directory.Exists(Path.Combine(remounted.ProjectsRoot, "project")), "the physical link is withheld so the wrong project is never read");

        var assertion41 = Assert.ThrowsAny<InvalidOperationException>(() => context.OpenLink(replacement, null));
        Assert.Matches("(?is)^.*bound\\ to\\ a\\ different\\ project.*$", assertion41.Message);

        // The explicit recovery path: close the stale binding and adopt the new directory,
        // which gets a fresh suffixed name because the retired name belongs to the old object.
        context.CloseLink("project");
        var reopened = context.OpenLink(replacement, null);
        var reopenedLinks = reopened.Links ?? throw new InvalidOperationException("missing links");
        var adoptedName = Assert.Single(reopenedLinks.Records).Name;
        Assert.NotEqual("project", adoptedName);

        context.Capture().Resolve($"workspace://local/projects/{adoptedName}/marker.txt", false);
    }

    [Fact(Timeout = 30_000)]
    public async Task RemountKeepsTheEntryDanglingWhenTheFingerprintIsNotFound()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        await TempArea.DeleteAsync(project, CancellationToken.None);
        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var links = Workspace.Create(remounted).Capture().Links
                    ?? throw new InvalidOperationException("missing links");

        Assert.Single(links.Records);
        Assert.Equal(WorkspaceLinkState.TargetMissing, links.HealthByName["project"].State);
    }

    [Fact(Timeout = 30_000)]
    public async Task RemountKeepsTheEntryDanglingWhenTheProjectMovedOutsideTheSearchedParent()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var outer = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "outer")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(outer, "project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        var elsewhere = Path.Combine(workspace.ParentPath, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.Move(project, Path.Combine(elsewhere, "project"));

        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var links = Workspace.Create(remounted).Capture().Links
                    ?? throw new InvalidOperationException("missing links");

        Assert.Equal(project, Assert.Single(links.Records).Target);
        Assert.Equal(WorkspaceLinkState.TargetMissing, links.HealthByName["project"].State);
    }

    [Fact(Timeout = 30_000)]
    public void FingerprintlessRegistryKeepsPathOnlySemantics()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        // Rewrite the persisted registry into the pre-amendment shape: no identity fields.
        var registryPath = Path.Combine(
            workspace.Path,
            WorkspaceHome.StateDirectoryName,
            "registry.json");
        var state = JsonSerializer.Deserialize(
            File.ReadAllText(registryPath),
            WorkspaceLinkJsonContext.Default.WorkspaceLinkRegistryState);
        Assert.NotNull(state);
        var stateLinks = state.Links ?? throw new InvalidOperationException("missing links");
        Assert.NotNull(Assert.Single(stateLinks).Identity);
        File.WriteAllText(
            registryPath,
            JsonSerializer.Serialize(
                new WorkspaceLinkRegistryState(
                    state.Version,
                    [stateLinks[0] with { Identity = null }],
                    state.Retired),
                WorkspaceLinkJsonContext.Default.WorkspaceLinkRegistryState));

        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var links = Workspace.Create(remounted).Capture().Links
                    ?? throw new InvalidOperationException("missing links");
        Assert.Null(Assert.Single(links.Records).Identity);
        Assert.Equal(WorkspaceLinkState.Verified, links.HealthByName["project"].State);
        Assert.True(Directory.Exists(Path.Combine(remounted.ProjectsRoot, "project")), "path-only remount still repairs the physical link");
    }

    [Fact(Timeout = 30_000)]
    public async Task ClosedThenRenamedProjectResurrectsItsNameOnReopen()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "reopened after a rename",
            TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        context.OpenLink(project, null);
        context.CloseLink("project");

        var movedPath = Path.Combine(workspace.ParentPath, "renamed");
        Directory.Move(project, movedPath);
        var reopened = context.OpenLink(movedPath, null);
        var links = reopened.Links ?? throw new InvalidOperationException("missing links");
        var record = Assert.Single(links.Records);
        Assert.Equal("project", record.Name);
        Assert.Equal(movedPath, record.Target);

        context.Capture().Resolve("workspace://local/projects/project/marker.txt", false);
    }

    [Fact(Timeout = 30_000)]
    public async Task WithheldEntryRecoversWhenTheOriginalObjectReturns()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "original project",
            TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        // Park the original outside the searched parent so the withhold below cannot be
        // resolved by relocation, then occupy the registered path with another directory.
        var parking = Directory.CreateDirectory(
            Path.Combine(workspace.ParentPath, "elsewhere")).FullName;
        var parked = Path.Combine(parking, "parked-original");
        Directory.Move(project, parked);
        Directory.CreateDirectory(project);
        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var links = Workspace.Create(remounted).Capture().Links
                    ?? throw new InvalidOperationException("missing links");
        Assert.Equal(WorkspaceLinkState.IdentityChanged, links.HealthByName["project"].State);

        Directory.Delete(project);
        Directory.Move(parked, project);
        var recovered = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var recoveredLinks = Workspace.Create(recovered).Capture().Links
                             ?? throw new InvalidOperationException("missing links");

        Assert.Equal(WorkspaceLinkState.Verified, recoveredLinks.HealthByName["project"].State);
        Assert.True(Directory.Exists(Path.Combine(recovered.ProjectsRoot, "project")), "the withheld link is recreated once identity verifies again");
        Workspace.Create(recovered).Capture().Resolve("workspace://local/projects/project/marker.txt", false);
    }

    [Fact(Timeout = 30_000)]
    public void RegistryToleratesUnknownFutureFields()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        // A future format writes members this build has never heard of; the registry must
        // load and keep working (ADR 0027 §4: unknown fields tolerated).
        var registryPath = Path.Combine(
            workspace.Path,
            WorkspaceHome.StateDirectoryName,
            "registry.json");
        var text = File.ReadAllText(registryPath);
        File.WriteAllText(
            registryPath,
            text.Insert(text.IndexOf('{') + 1, " \"futureField\": true,"));

        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Assert.Equal(project, Assert.Single(remounted.LinksView().Records).Target);
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkspaceCommandRendersTheWithheldState()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        // Swap in a directory that coexisted with the original, so its identity differs
        // on every file system regardless of birth-time support.
        var replacementSource = Directory.CreateDirectory(
            Path.Combine(workspace.ParentPath, "project-replacement")).FullName;
        await TempArea.DeleteAsync(project, CancellationToken.None);
        Directory.Move(replacementSource, project);

        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var executor = new Commands.MaieuticsCommandExecutor(
            null,
            null,
            Workspace.Create(remounted),
            null,
            null);
        var rendered = await executor.ExecuteAsync(
            "%workspace current",
            null,
            TestContext.Current.CancellationToken);
        Assert.Contains("identity changed", rendered.Markdown);
    }

    [Fact]
    public void NativeStatStructuresMatchTheirPlatformLayouts()
    {
        if (OperatingSystem.IsMacOS())
            Assert.Equal(144, Marshal.SizeOf<WorkspaceFileIdentityReader.StatMacOS>());
        else if (OperatingSystem.IsLinux())
            Assert.Equal(0x100, Marshal.SizeOf<WorkspaceFileIdentityReader.StatLinux>());
        else if (OperatingSystem.IsWindows())
            Assert.Equal(52, Marshal.SizeOf<WorkspaceFileIdentityReader.ByHandleFileInformation>());
    }
}
