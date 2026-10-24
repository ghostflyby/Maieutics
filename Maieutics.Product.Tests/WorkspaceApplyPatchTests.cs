using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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

        Assert.Equal(["created", "updated", "deleted"], result.Files.Select(static file => file.Operation));
        Assert.Equal("docs/new.txt", result.Files[0].Path);
        var updated = result.Files[1];
        Assert.NotNull(updated.Diff);
        Assert.Contains("+var fresh = false;\n", updated.Diff.Unified);
        var deleted = result.Files[2];
        Assert.NotNull(deleted.Diff);
        Assert.Equal(1, deleted.Diff.Deletions);

        Assert.Equal("using System;\nvar fresh = false;\n", await File.ReadAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "src" + Path.DirectorySeparatorChar + "app.cs",
            cancellationToken));
        Assert.False(File.Exists(workspace.Path + Path.DirectorySeparatorChar + "obsolete.txt"));
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

        Assert.Equal("created", result.Files[0].Operation);
        Assert.Equal("src/main.py", result.Files[0].MovedFrom);
        Assert.Equal("def a():\n    return 1\ndef b():\n    return 3\n", await File.ReadAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "src" + Path.DirectorySeparatorChar + "renamed.py",
            cancellationToken));
        Assert.False(File.Exists(path));
    }

    [Fact(Timeout = 30_000)]
    public async Task ApplyPatchStopsAtTheFirstFailureWithTypedErrors()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var apply = Function(CreateFunctions(workspace.Path).Functions, "apply_patch");
        var missing = "*** Begin Patch\n*** Delete File: absent.txt\n*** End Patch\n";
        Assert.Equal("workspace_path_not_found", (await ShouldFailAsync(apply, $$"""{"patch":{{JsonSerializer.Serialize(missing)}}}""", cancellationToken))
            .Code);

        var conflict = "*** Begin Patch\n*** Add File: exists.txt\n+dup\n*** End Patch\n";
        await File.WriteAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "exists.txt",
            "already here\n",
            cancellationToken);
        Assert.Equal("workspace_path_exists", (await ShouldFailAsync(apply, $$"""{"patch":{{JsonSerializer.Serialize(conflict)}}}""", cancellationToken))
            .Code);

        var escaped = "*** Begin Patch\n*** Update File: missing-file.txt\n@@\n-a\n+b\n*** End Patch\n";
        Assert.Equal("workspace_path_not_found", (await ShouldFailAsync(apply, $$"""{"patch":{{JsonSerializer.Serialize(escaped)}}}""", cancellationToken))
            .Code);

        // .git stays denied through patch paths.
        var git = "*** Begin Patch\n*** Delete File: .git/config\n*** End Patch\n";
        Assert.Equal("workspace_path_denied", (await ShouldFailAsync(apply, $$"""{"patch":{{JsonSerializer.Serialize(git)}}}""", cancellationToken))
            .Code);
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
        Assert.Equal("workspace_patch_context_not_found", (await ShouldFailAsync(apply, $$"""{"patch":{{JsonSerializer.Serialize(patch)}}}""", cancellationToken))
            .Code);
    }

    [Fact]
    public void ApplyPatchParserRejectsMalformedDocuments()
    {
        Assert.Equal("workspace_patch_invalid", Assert.ThrowsAny<WorkspaceException>(() => ApplyPatchParser.Parse("no sentinels")).Code);
        Assert.Equal("workspace_patch_invalid", Assert.ThrowsAny<WorkspaceException>(() => ApplyPatchParser.Parse("*** Begin Patch\n*** Add File: a.txt\n+one\n")).Code);
        Assert.Equal("workspace_patch_invalid", Assert.ThrowsAny<WorkspaceException>(() => ApplyPatchParser.Parse(
            "*** Begin Patch\n*** Update File: a.txt\nplain text\n*** End Patch\n")).Code);
        Assert.Equal("workspace_patch_invalid", Assert.ThrowsAny<WorkspaceException>(() => ApplyPatchParser.Parse("*** Begin Patch\n*** End Patch\n")).Code);
    }

    [Fact]
    public void ApplyPatchParserParsesStructuredOperationDiffs()
    {
        var created = ApplyPatchParser.ParseOperationDiff("create_file", "a.txt", "+one\n+two\n");
        Assert.Equal("one\ntwo\n", created.Files[0].Diff);

        // The text after @@ is the anchor line, not a context line.
        var updated = ApplyPatchParser.ParseOperationDiff("update_file", "a.txt", "@@ context\n-old\n+new\n");
        Assert.Single(updated.Files[0].Changes);
        Assert.Equal("context", updated.Files[0].Changes[0].Anchor);
        Assert.Equal(new[] { "old" }, updated.Files[0].Changes[0].OldLines);
        Assert.Equal(new[] { "new" }, updated.Files[0].Changes[0].NewLines);

        var deleted = ApplyPatchParser.ParseOperationDiff("delete_file", "a.txt", null);
        Assert.Equal("delete_file", deleted.Files[0].Kind);
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

        Assert.Equal("updated", result.Files[0].Operation);
        Assert.Equal("first\r\nSECOND\r\n", await File.ReadAllTextAsync(path, cancellationToken));
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
        Assert.NotNull(invocation.Failure);
        return Assert.IsType<AgentToolException>(invocation.Failure);
    }

    private static T Result<T>(ToolInvocation invocation, JsonTypeInfo<T> jsonTypeInfo)
    {
        Assert.Null(invocation.Failure);
        var result = Assert.IsType<JsonElement>(invocation.Result);
        return result.Deserialize(jsonTypeInfo)
               ?? throw new InvalidOperationException("The tool returned an empty JSON result.");
    }

    private sealed record ToolInvocation(JsonElement? Result, AgentToolException? Failure);
}
