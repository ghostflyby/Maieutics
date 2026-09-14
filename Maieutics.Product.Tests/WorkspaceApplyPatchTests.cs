using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class WorkspaceApplyPatchTests
{
    private const string MultiFilePatch =
        "*** Begin Patch\n" +
        "*** Add File: docs/new.txt\n" +
        "+created line\n" +
        "*** Update File: src/app.cs\n" +
        "@@ using System;\n" +
        "-var stale = true;\n" +
        "+var fresh = false;\n" +
        "*** Delete File: obsolete.txt\n" +
        "*** End Patch\n";

    [Fact(Timeout = 30_000)]
    public async Task ApplyPatchCreatesUpdatesAndDeletesInOrder()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(workspace.Path + Path.DirectorySeparatorChar + "src");
        await File.WriteAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "src" + Path.DirectorySeparatorChar + "app.cs",
            "using System;\nvar stale = true;\n",
            cancellationToken);
        await File.WriteAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "obsolete.txt",
            "old content\n",
            cancellationToken);

        var apply = Function(CreateFunctions(workspace.Path).Functions, "apply_patch");
        var result = Result<ApplyPatchResult>(await InvokeAsync(
                apply,
                $$"""{"patch":{{JsonSerializer.Serialize(MultiFilePatch)}}}"""),
            WorkspaceEditJsonSerializerContext.Default.ApplyPatchResult);

        result.Files.Select(static file => file.Operation)
            .Should().Equal("created", "updated", "deleted");
        result.Files[0].Path.Should().Be("docs/new.txt");
        result.Files[1].Diff.Unified.Should().Contain("+var fresh = false;\n");
        result.Files[2].Diff.Deletions.Should().Be(1);

        (await File.ReadAllTextAsync(
                workspace.Path + Path.DirectorySeparatorChar + "src" + Path.DirectorySeparatorChar + "app.cs",
                cancellationToken))
            .Should().Be("using System;\nvar fresh = false;\n");
        File.Exists(workspace.Path + Path.DirectorySeparatorChar + "obsolete.txt").Should().BeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task ApplyPatchAnchorsChangeSectionsAndSupportsMoveTo()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(workspace.Path + Path.DirectorySeparatorChar + "src");
        var path = workspace.Path + Path.DirectorySeparatorChar + "src" + Path.DirectorySeparatorChar + "main.py";
        await File.WriteAllTextAsync(path, "def a():\n    return 1\ndef b():\n    return 2\n", cancellationToken);

        var apply = Function(CreateFunctions(workspace.Path).Functions, "apply_patch");
        var patch = "*** Begin Patch\n" +
                    "*** Update File: src/main.py\n" +
                    "@@ def b():\n" +
                    "-    return 2\n" +
                    "+    return 3\n" +
                    "*** Move to: src/renamed.py\n" +
                    "*** End Patch\n";
        var result = Result<ApplyPatchResult>(await InvokeAsync(
                apply,
                $$"""{"patch":{{JsonSerializer.Serialize(patch)}}}"""),
            WorkspaceEditJsonSerializerContext.Default.ApplyPatchResult);

        result.Files[0].Operation.Should().Be("created");
        result.Files[0].MovedFrom.Should().Be("src/main.py");
        (await File.ReadAllTextAsync(
                workspace.Path + Path.DirectorySeparatorChar + "src" + Path.DirectorySeparatorChar + "renamed.py",
                cancellationToken))
            .Should().Be("def a():\n    return 1\ndef b():\n    return 3\n");
        File.Exists(path).Should().BeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task ApplyPatchStopsAtTheFirstFailureWithTypedErrors()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var apply = Function(CreateFunctions(workspace.Path).Functions, "apply_patch");
        var missing = "*** Begin Patch\n*** Delete File: absent.txt\n*** End Patch\n";
        (await ShouldFailAsync(apply, $$"""{"patch":{{JsonSerializer.Serialize(missing)}}}""", cancellationToken))
            .Code.Should().Be("workspace_path_not_found");

        var conflict = "*** Begin Patch\n*** Add File: exists.txt\n+dup\n*** End Patch\n";
        await File.WriteAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "exists.txt",
            "already here\n",
            cancellationToken);
        (await ShouldFailAsync(apply, $$"""{"patch":{{JsonSerializer.Serialize(conflict)}}}""", cancellationToken))
            .Code.Should().Be("workspace_path_exists");

        var escaped = "*** Begin Patch\n*** Update File: missing-file.txt\n@@\n-a\n+b\n*** End Patch\n";
        (await ShouldFailAsync(apply, $$"""{"patch":{{JsonSerializer.Serialize(escaped)}}}""", cancellationToken))
            .Code.Should().Be("workspace_path_not_found");

        // .git stays denied through patch paths.
        var git = "*** Begin Patch\n*** Delete File: .git/config\n*** End Patch\n";
        (await ShouldFailAsync(apply, $$"""{"patch":{{JsonSerializer.Serialize(git)}}}""", cancellationToken))
            .Code.Should().Be("workspace_path_denied");
    }

    [Fact(Timeout = 30_000)]
    public async Task ApplyPatchReportsUnmatchedContextAsRecoverable()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(workspace.Path + Path.DirectorySeparatorChar + "src");
        await File.WriteAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "src" + Path.DirectorySeparatorChar + "app.cs",
            "using System;\n",
            cancellationToken);

        var apply = Function(CreateFunctions(workspace.Path).Functions, "apply_patch");
        var patch = "*** Begin Patch\n" +
                    "*** Update File: src/app.cs\n" +
                    "@@ using System;\n" +
                    "-    return 2\n" +
                    "+    return 3\n" +
                    "*** End Patch\n";
        (await ShouldFailAsync(apply, $$"""{"patch":{{JsonSerializer.Serialize(patch)}}}""", cancellationToken))
            .Code.Should().Be("workspace_patch_context_not_found");
    }

    [Fact]
    public void ApplyPatchParserRejectsMalformedDocuments()
    {
        FluentActions.Invoking(() => ApplyPatchParser.Parse("no sentinels"))
            .Should().Throw<WorkspaceException>().Which.Code.Should().Be("workspace_patch_invalid");
        FluentActions.Invoking(() => ApplyPatchParser.Parse("*** Begin Patch\n*** Add File: a.txt\n+one\n"))
            .Should().Throw<WorkspaceException>().Which.Code.Should().Be("workspace_patch_invalid");
        FluentActions.Invoking(() => ApplyPatchParser.Parse(
                "*** Begin Patch\n*** Update File: a.txt\nplain text\n*** End Patch\n"))
            .Should().Throw<WorkspaceException>().Which.Code.Should().Be("workspace_patch_invalid");
        FluentActions.Invoking(() => ApplyPatchParser.Parse("*** Begin Patch\n*** End Patch\n"))
            .Should().Throw<WorkspaceException>().Which.Code.Should().Be("workspace_patch_invalid");
    }

    [Fact]
    public void ApplyPatchParserParsesStructuredOperationDiffs()
    {
        var created = ApplyPatchParser.ParseOperationDiff("create_file", "a.txt", "+one\n+two\n");
        created.Files[0].Diff.Should().Be("one\ntwo\n");

        // The text after @@ is the anchor line, not a context line.
        var updated = ApplyPatchParser.ParseOperationDiff("update_file", "a.txt", "@@ context\n-old\n+new\n");
        updated.Files[0].Changes.Should().HaveCount(1);
        updated.Files[0].Changes[0].Anchor.Should().Be("context");
        updated.Files[0].Changes[0].OldLines.Should().Equal("old");
        updated.Files[0].Changes[0].NewLines.Should().Equal("new");

        var deleted = ApplyPatchParser.ParseOperationDiff("delete_file", "a.txt", null);
        deleted.Files[0].Kind.Should().Be("delete_file");
    }

    [Fact(Timeout = 30_000)]
    public async Task ApplyPatchHandlesCrlfFilesAndPreservesTheirEndings()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = workspace.Path + Path.DirectorySeparatorChar + "windows.txt";
        await File.WriteAllTextAsync(path, "first\r\nsecond\r\n", cancellationToken);

        var apply = Function(CreateFunctions(workspace.Path).Functions, "apply_patch");
        var patch = "*** Begin Patch\n*** Update File: windows.txt\n@@ first\n-second\n+SECOND\n*** End Patch\n";
        var result = Result<ApplyPatchResult>(await InvokeAsync(
                apply,
                $$"""{"patch":{{JsonSerializer.Serialize(patch)}}}"""),
            WorkspaceEditJsonSerializerContext.Default.ApplyPatchResult);

        result.Files[0].Operation.Should().Be("updated");
        (await File.ReadAllTextAsync(path, cancellationToken)).Should().Be("first\r\nSECOND\r\n");
    }

    private static WorkspaceEditFunctions CreateFunctions(string root)
    {
        return new WorkspaceEditFunctions(Workspace.Create(root, root));
    }

    private static AIFunction Function(IReadOnlyList<AIFunction> functions, string name)
    {
        return functions.Single(function => function.Name == name);
    }

    private static AIFunctionArguments Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));
    }

    private static async ValueTask<ToolInvocation> InvokeAsync(
        AIFunction function,
        string json)
    {
        try
        {
            return new ToolInvocation(
                await function.InvokeAsync(Arguments(json), TestContext.Current.CancellationToken) as JsonElement?,
                null);
        }
        catch (AgentToolException exception)
        {
            return new ToolInvocation(null, exception);
        }
    }

    private static async Task<AgentToolException> ShouldFailAsync(
        AIFunction function,
        string json,
        CancellationToken cancellationToken)
    {
        var invocation = await InvokeAsync(function, json);
        return invocation.Failure.Should().NotBeNull().And.BeOfType<AgentToolException>().Which;
    }

    private static T Result<T>(ToolInvocation invocation, JsonTypeInfo<T> jsonTypeInfo)
    {
        invocation.Failure.Should().BeNull();
        var result = invocation.Result.Should().BeOfType<JsonElement>().Which;
        return result.Deserialize(jsonTypeInfo)
               ?? throw new InvalidOperationException("The tool returned an empty JSON result.");
    }

    private sealed record ToolInvocation(JsonElement? Result, AgentToolException? Failure);
}
