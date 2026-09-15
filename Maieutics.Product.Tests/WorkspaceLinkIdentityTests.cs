using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
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

        var record = (context.OpenLink(project, null).Links ?? throw new InvalidOperationException("missing links"))
            .Records.Should().ContainSingle().Which;
        var identity = record.Identity;
        identity.Should().NotBeNull("the open captured the directory's file identity");
        identity!.Inode.Should().NotBe(0);

        var persisted = WorkspaceLinkRegistry.Load(home.HomePath).Entries().Should().ContainSingle().Which;
        persisted.Identity.Should().NotBeNull("the fingerprint survives persistence");
        persisted.Identity!.Matches(identity).Should().BeTrue();

        if (!OperatingSystem.IsLinux())
        {
            identity!.BirthTimeUtc.Should().NotBeNull("macOS and Windows report a creation time");
            identity!.BirthTimeUtc.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddMinutes(1));
        }

        var recaptured = WorkspaceFileIdentityReader.TryRead(project);
        recaptured.Should().NotBeNull();
        identity!.Matches(recaptured).Should().BeTrue("recapturing the same directory is stable");
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
        context.OpenLink(movedPath, null).Should().NotBeNull();

        var links = context.Capture().Links ?? throw new InvalidOperationException("missing links");
        links.Records.Should().ContainSingle("the moved project is recognized as the same object")
            .Which.Target.Should().Be(movedPath);

        var resolved = context.Capture().Resolve("workspace://local/projects/project/marker.txt", false);
        resolved.FullPath.Should().Be(Path.Combine(movedPath, "marker.txt"),
            "the physical link follows the re-pointed record");
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

        links.Records.Should().ContainSingle().Which.Target.Should().Be(movedPath);
        links.HealthByName["project"].State.Should().Be(WorkspaceLinkState.Relocated,
            "detail: {0}", links.HealthByName["project"].Detail);
        Directory.Exists(Path.Combine(remounted.ProjectsRoot, "project"))
            .Should().BeTrue("the physical link is repaired at the re-pointed target");

        var resolved = context.Capture().Resolve("workspace://local/projects/project/marker.txt", false);
        resolved.FullPath.Should().Be(Path.Combine(movedPath, "marker.txt"));

        var executor = new Maieutics.Commands.MaieuticsCommandExecutor(null, null, context, null, null);
        var rendered = await executor.ExecuteAsync(
            "%workspace current",
            null,
            TestContext.Current.CancellationToken);
        rendered.Markdown.Should().Contain("relocated",
            "the operator sees that the record followed the project");
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
        Directory.Delete(project, true);
        Directory.Move(replacementSource, project);
        var replacement = project;

        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(remounted);
        var links = context.Capture().Links ?? throw new InvalidOperationException("missing links");

        links.Records.Should().ContainSingle("the entry stays reserved for the registered object");
        links.HealthByName["project"].State.Should().Be(WorkspaceLinkState.IdentityChanged,
            "detail: {0}", links.HealthByName["project"].Detail);
        Directory.Exists(Path.Combine(remounted.ProjectsRoot, "project"))
            .Should().BeFalse("the physical link is withheld so the wrong project is never read");

        context.Invoking(c => c.OpenLink(replacement, null))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*bound to a different project*");

        // The explicit recovery path: close the stale binding and adopt the new directory,
        // which gets a fresh suffixed name because the retired name belongs to the old object.
        context.CloseLink("project");
        var reopened = context.OpenLink(replacement, null);
        var reopenedLinks = reopened.Links ?? throw new InvalidOperationException("missing links");
        var adoptedName = reopenedLinks.Records.Should().ContainSingle().Which.Name;
        adoptedName.Should().NotBe("project",
            "the retired name belongs to the old object and is never reused for a new one");

        context.Capture().Invoking(s => s.Resolve($"workspace://local/projects/{adoptedName}/marker.txt", false))
            .Should().NotThrow();
    }

    [Fact(Timeout = 30_000)]
    public async Task RemountKeepsTheEntryDanglingWhenTheFingerprintIsNotFound()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        Directory.Delete(project, true);
        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var links = Workspace.Create(remounted).Capture().Links
                    ?? throw new InvalidOperationException("missing links");

        links.Records.Should().ContainSingle("a missing target keeps its registry entry");
        links.HealthByName["project"].State.Should().Be(WorkspaceLinkState.TargetMissing);
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

        links.Records.Should().ContainSingle().Which.Target.Should().Be(project);
        links.HealthByName["project"].State.Should().Be(WorkspaceLinkState.TargetMissing,
            "the bounded search covers the former parent only");
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
        state.Should().NotBeNull();
        var stateLinks = state!.Links ?? throw new InvalidOperationException("missing links");
        stateLinks.Should().ContainSingle().Which.Identity.Should().NotBeNull();
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
        links.Records.Should().ContainSingle().Which.Identity.Should().BeNull();
        links.HealthByName["project"].State.Should().Be(WorkspaceLinkState.Verified,
            "a fingerprint-less record is verified by path, as before the amendment");
        Directory.Exists(Path.Combine(remounted.ProjectsRoot, "project"))
            .Should().BeTrue("path-only remount still repairs the physical link");
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
        var record = links.Records.Should().ContainSingle().Which;
        record.Name.Should().Be("project",
            "the tombstone belongs to the same object, so the name its transcripts cite returns");
        record.Target.Should().Be(movedPath);

        context.Capture().Invoking(s => s.Resolve("workspace://local/projects/project/marker.txt", false))
            .Should().NotThrow();
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
        links.HealthByName["project"].State.Should().Be(WorkspaceLinkState.IdentityChanged);

        Directory.Delete(project);
        Directory.Move(parked, project);
        var recovered = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var recoveredLinks = Workspace.Create(recovered).Capture().Links
                             ?? throw new InvalidOperationException("missing links");

        recoveredLinks.HealthByName["project"].State.Should().Be(WorkspaceLinkState.Verified,
            "the registered object is back at the registered path");
        Directory.Exists(Path.Combine(recovered.ProjectsRoot, "project"))
            .Should().BeTrue("the withheld link is recreated once identity verifies again");
        Workspace.Create(recovered).Capture()
            .Invoking(s => s.Resolve("workspace://local/projects/project/marker.txt", false))
            .Should().NotThrow();
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
        remounted.LinksView().Records.Should().ContainSingle().Which.Target.Should().Be(project);
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
        Directory.Delete(project, true);
        Directory.Move(replacementSource, project);

        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var executor = new Maieutics.Commands.MaieuticsCommandExecutor(
            null,
            null,
            Workspace.Create(remounted),
            null,
            null);
        var rendered = await executor.ExecuteAsync(
            "%workspace current",
            null,
            TestContext.Current.CancellationToken);
        rendered.Markdown.Should().Contain("identity changed");
    }

    [Fact]
    public void NativeStatStructuresMatchTheirPlatformLayouts()
    {
        if (OperatingSystem.IsMacOS())
            Marshal.SizeOf<WorkspaceFileIdentityReader.StatMacOS>().Should().Be(144);
        else if (OperatingSystem.IsLinux())
            Marshal.SizeOf<WorkspaceFileIdentityReader.StatLinux>().Should().Be(0x100);
        else if (OperatingSystem.IsWindows())
            Marshal.SizeOf<WorkspaceFileIdentityReader.ByHandleFileInformation>().Should().Be(52);
    }
}
