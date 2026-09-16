using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.AI;
using static Maieutics.Product.Tests.WorkspaceToolInvocations;

namespace Maieutics.Product.Tests;

/// <summary>Hop identity pinning (ADR 0027 follow-up): once a managed link is open, every
/// read, write, and delete through it pins the opened project directory against the
/// registered fingerprint — so a different directory swapped in at the registered path
/// fails typed instead of being silently read (Windows included, where the final open
/// re-walks the path and would otherwise follow the swap).</summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class WorkspaceLinkPinTests
{
    [Fact(Timeout = 30_000)]
    public async Task SwappingTheTargetDirectoryUnderAnOpenLinkFailsTypedOnUse()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "original",
            TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        context.OpenLink(project, null);

        // Swap a different directory in at the registered path without a remount. The
        // link still validates by path (the junction target string is unchanged), so only
        // the identity pin can see the swap.
        var replacement = Directory.CreateDirectory(
            Path.Combine(workspace.ParentPath, "replacement")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(replacement, "marker.txt"),
            "replacement",
            TestContext.Current.CancellationToken);
        Directory.Delete(project, true);
        Directory.Move(replacement, project);

        var uri = "workspace://local/projects/project/marker.txt";
        var functions = new WorkspaceFunctions(context);
        var readFailure = ShouldFailure(await InvokeAsync(
            Function(functions, "read_text"),
            $$"""{"uri":"{{uri}}"}"""));
        readFailure.Code.Should().Be("workspace_link_identity_changed",
            "the pin must refuse the swapped-in directory, not read it");

        var editFunctions = new WorkspaceEditFunctions(context);
        var writeFailure = ShouldFailure(await InvokeAsync(
            editFunctions.Functions.Single(function => function.Name == "write_text"),
            $$"""{"uri":"{{uri}}","content":"planted"}"""));
        writeFailure.Code.Should().Be("workspace_link_identity_changed",
            "writes traverse the same pinned hop as reads");
    }

    [Fact(Timeout = 30_000)]
    public async Task FingerprintlessLinkKeepsPathOnlyReadsUnderSwap()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var project = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, "marker.txt"),
            "original",
            TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        Workspace.Create(home).OpenLink(project, null);

        // Rewrite the persisted registry into the pre-amendment shape and reload, so the
        // live record carries no fingerprint and the pin cannot be enforced.
        var registryPath = Path.Combine(
            workspace.Path,
            WorkspaceHome.StateDirectoryName,
            "registry.json");
        var state = JsonSerializer.Deserialize(
            File.ReadAllText(registryPath),
            WorkspaceLinkJsonContext.Default.WorkspaceLinkRegistryState);
        var stateLinks = state!.Links ?? throw new InvalidOperationException("missing links");
        File.WriteAllText(
            registryPath,
            JsonSerializer.Serialize(
                new WorkspaceLinkRegistryState(
                    state.Version,
                    [stateLinks[0] with { Identity = null }],
                    state.Retired),
                WorkspaceLinkJsonContext.Default.WorkspaceLinkRegistryState));
        var remounted = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(remounted);

        var replacement = Directory.CreateDirectory(
            Path.Combine(workspace.ParentPath, "replacement")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(replacement, "marker.txt"),
            "replacement",
            TestContext.Current.CancellationToken);
        Directory.Delete(project, true);
        Directory.Move(replacement, project);

        // Legacy semantics: without a fingerprint the swap is indistinguishable, and the
        // read follows the path like it always did.
        var read = Result<ReadTextResult>(await InvokeAsync(
                Function(new WorkspaceFunctions(context), "read_text"),
                """{"uri":"workspace://local/projects/project/marker.txt"}"""),
            WorkspaceJsonSerializerContext.Default.ReadTextResult);
        read.Text.Should().Be("replacement");
    }
}
