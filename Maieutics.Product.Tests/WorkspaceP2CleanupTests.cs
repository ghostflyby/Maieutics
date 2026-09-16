using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.AI;
using static Maieutics.Product.Tests.WorkspaceToolInvocations;

namespace Maieutics.Product.Tests;

/// <summary>The #102 P2 cleanups: a dangling registered link fails with the typed
/// not-found code (not a generic I/O error), the <c>projects</c> URI segment matches the
/// registry's case-insensitive discipline, and link names sanitize identically on every
/// platform.</summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class WorkspaceP2CleanupTests
{
    [Fact(Timeout = 30_000)]
    public async Task DanglingLinkUseFailsWithTheTypedNotFoundCode()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "gone soon",
            TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        context.OpenLink(project, null);

        // The target vanishes without a remount: the registered link dangles. Whether the
        // failure comes from resolution or the pinned open, the code must say not-found,
        // never a generic I/O error.
        Directory.Delete(project, true);

        var uri = "workspace://local/projects/project/marker.txt";
        var readFailure = ShouldFailure(await InvokeAsync(
            Function(new WorkspaceFunctions(context), "read_text"),
            $$"""{"uri":"{{uri}}"}"""));
        readFailure.Code.Should().Be("workspace_path_not_found",
            $"a dangling link is a missing path, not an I/O error (got: {readFailure.Message})");

        var listFailure = ShouldFailure(await InvokeAsync(
            Function(new WorkspaceFunctions(context), "list_directory"),
            """{"uri":"workspace://local/projects/project"}"""));
        listFailure.Code.Should().Be("workspace_path_not_found");
    }

    [Fact(Timeout = 30_000)]
    public async Task CapitalizedProjectsSegmentResolvesLikeTheRegistryDoes()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "case insensitive segment",
            TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        context.OpenLink(project, null);

        var functions = new WorkspaceFunctions(context);
        var invocation = await InvokeAsync(
            Function(functions, "read_text"),
            """{"uri":"workspace://local/Projects/project/marker.txt"}""");

        if (OperatingSystem.IsLinux())
        {
            // A case-sensitive file system has no `Projects/` directory; the URI is
            // honestly not found there even though the registry matches names loosely.
            ShouldFailure(invocation).Code.Should().Be("workspace_path_not_found");
        }
        else
        {
            var read = Result<ReadTextResult>(
                invocation,
                WorkspaceJsonSerializerContext.Default.ReadTextResult);
            read.Text.Should().Be("case insensitive segment",
                "the segment is compared like the registry compares names");
        }
    }
}
