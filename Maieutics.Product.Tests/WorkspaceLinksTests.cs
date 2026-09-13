using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.AI;
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
    public void RegistrySanitizesNameCandidates(string candidate, string expected)
    {
        WorkspaceLinkRegistry.SanitizeName(candidate).Should().Be(expected);
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
        firstName.Should().Be("shared-name");
        registry.CommitNew(new WorkspaceLinkRecord(firstName, first, null, DateTimeOffset.UtcNow));

        var secondName = registry.AllocateName(second, null);
        secondName.Should().Be($"shared-name-{TargetHash(second, 6)}");
        WorkspaceLinkRegistry.Load(workspace.Path).AllocateName(second, null)
            .Should().Be(secondName, "allocation must be order- and instance-independent");

        var record = new WorkspaceLinkRecord(firstName, first, null, DateTimeOffset.UtcNow);
        registry.CommitNew(record);
        registry.Remove(firstName);
        registry.AllocateName(first, null).Should().Be(firstName, "a retired name is reused for its own target");
        registry.AllocateName(second, null).Should().NotBe("shared-name", "a retired name is never reused for a different target");
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
        context.Capture().Version.Should().Be(versionBefore + 1);

        Directory.Exists(Path.Combine(home.ProjectsRoot, "linked project"))
            .Should().BeTrue("the physical link is created under projects/");

        var snapshot = context.Capture();
        var resolved = snapshot.Resolve("workspace://local/projects/linked%20project/src/main.cs", false);
        resolved.FullPath.Should().Be(Path.GetFullPath(source));
        resolved.Uri.Should().Be("workspace://local/projects/linked%20project/src/main.cs");

        var read = Result<ReadTextResult>(await InvokeAsync(
                Function(functions, "read_text"),
                """{"uri":"workspace://local/projects/linked%20project/src/main.cs"}"""),
            WorkspaceJsonSerializerContext.Default.ReadTextResult);
        read.Text.Should().Be("needle in project");

        var search = Result<SearchTextResult>(await InvokeAsync(
                Function(functions, "search_text"),
                """{"query":"needle"}"""),
            WorkspaceJsonSerializerContext.Default.SearchTextResult);
        search.Matches.Should().ContainSingle().Which.Uri
            .Should().Be("workspace://local/projects/linked%20project/src/main.cs");

        var listed = Result<ListDirectoryResult>(await InvokeAsync(
                Function(functions, "list_directory"),
                """{"uri":"workspace://local/projects/linked%20project"}"""),
            WorkspaceJsonSerializerContext.Default.ListDirectoryResult);
        listed.Entries.Should().ContainSingle().Which.Name.Should().Be("src");
    }

    [Fact(Timeout = 30_000)]
    public async Task OpenIsIdempotentAndStartupRemountsRegisteredLinks()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);

        var first = context.OpenLink(project, null);
        var second = context.OpenLink(project, null);
        second.Links.Should().NotBeNull();
        (first.Links ?? throw new InvalidOperationException("missing links")).Records
            .Should().ContainSingle().Which.Name.Should().Be("project");
        (second.Links ?? throw new InvalidOperationException("missing links")).Records
            .Should().ContainSingle("opening the same target twice is idempotent");

        File.Exists(Path.Combine(workspace.Path, WorkspaceHome.StateDirectoryName, "registry.json"))
            .Should().BeTrue("the registry is persisted inside the home state directory");

        Directory.Delete(Path.Combine(home.ProjectsRoot, "project"));
        WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Directory.Exists(Path.Combine(home.ProjectsRoot, "project"))
            .Should().BeTrue("startup remounts registered links whose target still exists");
    }

    [Fact(Timeout = 30_000)]
    public async Task ForeignLinksStateDirectoryAndInTargetLinksStayRejected()
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
        snapshot.Invoking(s => s.Resolve("workspace://local/foreign", false))
            .Should().Throw<WorkspaceException>()
            .Which.Code.Should().Be("workspace_symbolic_link_not_allowed");

        var unregistered = Path.Combine(home.ProjectsRoot, "unregistered");
        ManagedWorkspaceLink.Create(unregistered, workspace.ParentPath);
        snapshot.Invoking(s => s.Resolve("workspace://local/projects/unregistered", false))
            .Should().Throw<WorkspaceException>()
            .Which.Code.Should().Be("workspace_symbolic_link_not_allowed");

        var retargeted = Path.Combine(home.ProjectsRoot, "project");
        Directory.Delete(retargeted);
        ManagedWorkspaceLink.Create(retargeted, workspace.Path);
        snapshot.Invoking(s => s.Resolve("workspace://local/projects/project/inner", false))
            .Should().Throw<WorkspaceException>()
            .Which.Code.Should().Be("workspace_symbolic_link_not_allowed", "a tampered link target fails validation");

        snapshot.Invoking(s => s.Resolve("workspace://local/.maieutics/registry.json", false))
            .Should().Throw<WorkspaceException>()
            .Which.Code.Should().Be("workspace_path_denied");

        Directory.Delete(retargeted);
        ManagedWorkspaceLink.Create(retargeted, project);
        var insideTarget = Path.Combine(project, "inner", "escape");
        ManagedWorkspaceLink.Create(insideTarget, workspace.Path);
        snapshot.Invoking(s => s.Resolve("workspace://local/projects/project/inner/escape", false))
            .Should().Throw<WorkspaceException>()
            .Which.Code.Should().Be("workspace_symbolic_link_not_allowed", "the no-follow discipline continues inside the target");
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
        variables.GetVariable("workspace").Should().Be(home.HomePath);
        variables.GetVariable("project.project").Should().Be(Path.GetFullPath(project));
        variables.GetVariable("project.PROJECT").Should().Be(Path.GetFullPath(project), "names match case-insensitively");
        variables.GetVariable("project.missing").Should().BeNull();

        context.CloseLink("Project");
        Directory.Exists(Path.Combine(home.ProjectsRoot, "Project")).Should().BeFalse();
        File.Exists(marker).Should().BeTrue("closing a link never deletes the target");
        variables.GetVariable("project.Project").Should().BeNull();

        var reopened = context.OpenLink(project, null);
        (reopened.Links ?? throw new InvalidOperationException("missing links")).Records
            .Should().ContainSingle().Which.Name.Should().Be("Project", "the retired name is reused for its own target");
    }

    [Fact(Timeout = 30_000)]
    public async Task OpeningFailsWithTypedErrorsForMissingUnsupportedAndCyclingTargets()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);

        context.Invoking(c => c.OpenLink(
                Path.Combine(workspace.ParentPath, "missing"),
                null))
            .Should().Throw<DirectoryNotFoundException>();

        if (OperatingSystem.IsWindows())
        {
            context.Invoking(c => c.OpenLink(@"\\server\share\project", null))
                .Should().Throw<ArgumentException>()
                .WithMessage("*workspace link target*");
        }

        context.Invoking(c => c.OpenLink(workspace.Path, null))
            .Should().Throw<ArgumentException>()
            .WithMessage("*cannot contain the workspace home*");
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkspaceCommandsOpenRenderAndCloseLinks()
    {
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "cli project")).FullName;
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var executor = new Maieutics.Commands.MaieuticsCommandExecutor(
            null,
            null,
            Workspace.Create(home),
            null,
            null);

        var opened = await executor.ExecuteAsync(
            $"%workspace open {project} as work",
            null,
            TestContext.Current.CancellationToken);
        opened.Markdown.Should().Contain("fixed home").And.Contain("`work`");

        var current = await executor.ExecuteAsync(
            "%workspace current",
            null,
            TestContext.Current.CancellationToken);
        current.Markdown.Should().Contain("`work`").And.Contain(project);

        var closed = await executor.ExecuteAsync(
            "%workspace close work",
            null,
            TestContext.Current.CancellationToken);
        closed.Markdown.Should().Contain("Projects: none");

        await executor.Awaiting(e => e.ExecuteAsync(
                "%workspace use somewhere",
                null,
                TestContext.Current.CancellationToken))
            .Should().ThrowAsync<Maieutics.Commands.MaieuticsCommandException>();
    }

    private static string TargetHash(string canonicalTarget, int characters)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalTarget)));
        return hex[..characters].ToLowerInvariant();
    }
}
