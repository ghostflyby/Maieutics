using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class WorkspaceEditToolTests
{
    [Fact(Timeout = 30_000)]
    public async Task EditFunctionsExposeSeparateStrictSchemasAndTypedResults()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var functions = CreateFunctions(workspace.Path).Functions;

        Assert.Equal(new string[] { "write_text", "edit_text", "apply_patch" }, functions.Select(static function => function.Name));
        Assert.All(functions, static function => Assert.True(function.JsonSchema.ValueKind == JsonValueKind.Object &&
            function.JsonSchema.GetProperty("type").GetString() == "object"));

        var write = Function(functions, "write_text");
        var assertion40 = await Assert.ThrowsAnyAsync<Exception>(async () => { await ((write).InvokeAsync(
                Arguments("""{"uri":"workspace://local/a.txt","content":"x","extra":1}"""),
                cancellationToken).AsTask()); });
        Assert.True(((Func<Exception, bool>)(static exception => exception.GetType() == typeof(ArgumentException) ||
                exception.GetType() == typeof(JsonException)))(assertion40));
    }

    [Fact(Timeout = 30_000)]
    public async Task WriteTextCreatesNestedFilesAndReportsTheCreatedDiff()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var write = Function(CreateFunctions(workspace.Path).Functions, "write_text");

        var result = Result<WriteTextResult>(await InvokeAsync(
                write,
                """{"uri":"workspace://local/deep/nested/a.txt","content":"one\ntwo\n"}""",
                cancellationToken),
            WorkspaceEditJsonSerializerContext.Default.WriteTextResult);

        Assert.Equal("workspace://local/deep/nested/a.txt", result.Uri);
        Assert.Equal("created", result.Operation);
        Assert.Null(result.BeforeBytes);
        Assert.Equal(8, result.AfterBytes);
        Assert.NotNull(result.Diff);
        Assert.Equal("--- a/deep/nested/a.txt\n+++ b/deep/nested/a.txt\n@@ -0,0 +1,2 @@\n+one\n+two\n", result.Diff.Unified);
        Assert.Equal(2, result.Diff.Additions);
        Assert.Equal(0, result.Diff.Deletions);
        Assert.False(result.Diff.Truncated);
        Assert.Equal("one\ntwo\n", (await File.ReadAllTextAsync(
                Path.Combine(workspace.Path, "deep", "nested", "a.txt"),
                cancellationToken)));
    }

    [Fact(Timeout = 30_000)]
    public async Task WriteTextOverwritesWithAnUpdatedDiffAndSkipsUnchangedWrites()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(workspace.Path, "a.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\n", cancellationToken);
        var write = Function(CreateFunctions(workspace.Path).Functions, "write_text");

        var updated = Result<WriteTextResult>(await InvokeAsync(
                write,
                """{"uri":"workspace://local/a.txt","content":"alpha\ndelta\n"}""",
                cancellationToken),
            WorkspaceEditJsonSerializerContext.Default.WriteTextResult);
        Assert.Equal("updated", updated.Operation);
        Assert.Equal(11, updated.BeforeBytes);
        Assert.NotNull(updated.Diff);
        Assert.Equal("--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1,2 @@\n alpha\n-beta\n+delta\n", updated.Diff.Unified);
        Assert.Equal(1, updated.Diff.Deletions);
        Assert.Equal(1, updated.Diff.Additions);

        var unchanged = Result<WriteTextResult>(await InvokeAsync(
                write,
                """{"uri":"workspace://local/a.txt","content":"alpha\ndelta\n"}""",
                cancellationToken),
            WorkspaceEditJsonSerializerContext.Default.WriteTextResult);
        Assert.Equal("unchanged", unchanged.Operation);
        Assert.Null(unchanged.Diff);
    }

    [Fact(Timeout = 30_000)]
    public async Task EditTextReplacesUniqueTargetsAndPreservesByteOrderMarks()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var marked = Path.Combine(workspace.Path, "bom.txt");
        await File.WriteAllBytesAsync(
            marked,
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("alpha\ngamma\n")],
            cancellationToken);

        var edit = Function(CreateFunctions(workspace.Path).Functions, "edit_text");
        var result = Result<EditTextResult>(await InvokeAsync(
                edit,
                """{"uri":"workspace://local/bom.txt","oldText":"gamma","newText":"GAMMA"}""",
                cancellationToken),
            WorkspaceEditJsonSerializerContext.Default.EditTextResult);

        Assert.Equal(1, result.Replacements);
        Assert.Contains("-gamma\n+GAMMA\n", result.Diff.Unified);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("alpha\nGAMMA\n")], (await File.ReadAllBytesAsync(marked, cancellationToken)));
    }

    [Fact(Timeout = 30_000)]
    public async Task EditTextRejectsMissingAmbiguousIdenticalTargetsAndBinaryFiles()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "dupe.txt",
            "x\ny\nx\n",
            cancellationToken);
        await File.WriteAllBytesAsync(
            workspace.Path + Path.DirectorySeparatorChar + "bin.dat",
            [0x00, 0x01],
            cancellationToken);
        var edit = Function(CreateFunctions(workspace.Path).Functions, "edit_text");

        Assert.Equal("workspace_edit_target_not_found", (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/dupe.txt","oldText":"absent","newText":"new"}""",
                cancellationToken)).Code);

        Assert.Equal("workspace_edit_target_not_unique", (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/dupe.txt","oldText":"x","newText":"new"}""",
                cancellationToken)).Code);

        Assert.Equal("workspace_invalid_arguments", (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/dupe.txt","oldText":"x","newText":"x"}""",
                cancellationToken)).Code);

        Assert.Equal("workspace_binary_file", (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/bin.dat","oldText":"x","newText":"y"}""",
                cancellationToken)).Code);

        Assert.Equal("workspace_path_not_found", (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/missing.txt","oldText":"x","newText":"y"}""",
                cancellationToken)).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task EditTextReplacesAllOccurrencesAndMatchesLfTargetsInCrlfFiles()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var crlf = Path.Combine(workspace.Path, "crlf.txt");
        await File.WriteAllTextAsync(crlf, "first\r\nsecond\r\n", cancellationToken);
        var edit = Function(CreateFunctions(workspace.Path).Functions, "edit_text");

        // read_text normalizes CRLF away, so LF-only targets still match.
        var result = Result<EditTextResult>(await InvokeAsync(
                edit,
                """{"uri":"workspace://local/crlf.txt","oldText":"first\nsecond","newText":"FIRST\nSECOND"}""",
                cancellationToken),
            WorkspaceEditJsonSerializerContext.Default.EditTextResult);
        Assert.Equal(1, result.Replacements);
        Assert.Equal("FIRST\r\nSECOND\r\n", (await File.ReadAllTextAsync(crlf, cancellationToken)));

        await File.WriteAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "dupe.txt",
            "x\ny\nx\n",
            cancellationToken);
        var all = Result<EditTextResult>(await InvokeAsync(
                edit,
                """{"uri":"workspace://local/dupe.txt","oldText":"x","newText":"z","replaceAll":true}""",
                cancellationToken),
            WorkspaceEditJsonSerializerContext.Default.EditTextResult);
        Assert.Equal(2, all.Replacements);
        Assert.Equal("z\ny\nz\n", (await File.ReadAllTextAsync(
                workspace.Path + Path.DirectorySeparatorChar + "dupe.txt",
                cancellationToken)));
    }

    [Fact(Timeout = 30_000)]
    public async Task EditToolsRefuseGitPathsDirectoriesOversizedAndNonUtf8Content()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(workspace.Path, "sub"));
        var write = Function(CreateFunctions(workspace.Path, maximumWriteBytes: 8).Functions, "write_text");
        var edit = Function(CreateFunctions(workspace.Path, maximumWriteBytes: 8).Functions, "edit_text");

        Assert.Equal("workspace_path_denied", (await ShouldFailAsync(write,
                """{"uri":"workspace://local/.git/config","content":"x"}""",
                cancellationToken)).Code);
        Assert.Equal("workspace_invalid_uri", (await ShouldFailAsync(write,
                """{"uri":"workspace://local/","content":"x"}""",
                cancellationToken)).Code);
        Assert.Equal("workspace_not_regular_file", (await ShouldFailAsync(write,
                """{"uri":"workspace://local/sub","content":"x"}""",
                cancellationToken)).Code);
        Assert.Equal("workspace_file_too_large", (await ShouldFailAsync(write,
                """{"uri":"workspace://local/big.txt","content":"0123456789"}""",
                cancellationToken)).Code);
        Assert.Equal("workspace_not_regular_file", (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/sub","oldText":"a","newText":"b"}""",
                cancellationToken)).Code);
        Assert.Equal("workspace_path_not_found", (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/missing.txt","oldText":"a","newText":"b"}""",
                cancellationToken)).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task EditToolsRefuseSymbolicLinkTargets()
    {
        if (OperatingSystem.IsWindows()) return;

        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "real.txt",
            "x\n",
            cancellationToken);
        File.CreateSymbolicLink(
            workspace.Path + Path.DirectorySeparatorChar + "link.txt",
            workspace.Path + Path.DirectorySeparatorChar + "real.txt");
        var functions = CreateFunctions(workspace.Path);
        var write = Function(functions.Functions, "write_text");
        var edit = Function(functions.Functions, "edit_text");

        Assert.Equal("workspace_symbolic_link_not_allowed", (await ShouldFailAsync(write, """{"uri":"workspace://local/link.txt","content":"y\n"}""", cancellationToken))
            .Code);
        Assert.Equal("workspace_symbolic_link_not_allowed", (await ShouldFailAsync(edit, """{"uri":"workspace://local/link.txt","oldText":"x","newText":"y"}""", cancellationToken))
            .Code);
    }

    [Fact]
    public void UnifiedDiffRendersCreationDeletionAndTrailingNewlineChanges()
    {
        // Creation from nothing.
        var creation = UnifiedDiff.Create(null, "one\ntwo\n", "a.txt", 64 * 1_024, 400)!;
        Assert.Equal("--- a/a.txt\n+++ b/a.txt\n@@ -0,0 +1,2 @@\n+one\n+two\n", creation.Unified);
        Assert.Equal(2, creation.Additions);
        Assert.Equal(0, creation.Deletions);

        // Deletion to nothing.
        var deletion = UnifiedDiff.Create("one\ntwo\n", "", "a.txt", 64 * 1_024, 400)!;
        Assert.Equal("--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +0,0 @@\n-one\n-two\n", deletion.Unified);
        Assert.Equal(2, deletion.Deletions);

        // Equal texts.
        Assert.Null(UnifiedDiff.Create("same\n", "same\n", "a.txt", 64 * 1_024, 400));

        // A trailing-newline-only change still renders as a final-line edit.
        var marker = UnifiedDiff.Create("one\ntwo", "one\ntwo\n", "a.txt", 64 * 1_024, 400)!;
        Assert.Contains("-two", marker.Unified);
        Assert.Contains("+two\n", marker.Unified);
        Assert.Contains(@"\ No newline at end of file", marker.Unified);
        Assert.Equal(1, marker.Additions);
        Assert.Equal(1, marker.Deletions);
    }

    [Fact]
    public void UnifiedDiffSeparatesDistantChangesAndHonorsOutputBounds()
    {
        var before = string.Join("\n", Enumerable.Range(0, 20).Select(static i => $"line {i}")) + "\n";
        var after = before.Replace("line 2", "LINE 2").Replace("line 15", "LINE 15");
        var spread = UnifiedDiff.Create(before, after, "a.txt", 64 * 1_024, 400)!;
        Assert.Contains("@@ -1,6 +1,6 @@", spread.Unified);
        Assert.Contains("@@ -13,7 +13,7 @@", spread.Unified);
        Assert.Equal(2, spread.Additions);
        Assert.Equal(2, spread.Deletions);
        Assert.False(spread.Truncated);

        // Close changes merge into one hunk.
        var close = UnifiedDiff.Create("a\nb\nc\nd\ne\nf\ng\n", "a\nb\nC\nd\nE\nf\ng\n", "a.txt", 64 * 1_024, 400)!;
        Assert.Contains("@@ -1,7 +1,7 @@", close.Unified);

        var bounded = UnifiedDiff.Create(before, after, "a.txt", 64 * 1_024, 5)!;
        Assert.True(bounded.Truncated);
        Assert.Contains("diff output bound reached", bounded.Unified);
    }

    [Fact]
    public void UnifiedDiffFallsBackWhenTheEditDistanceExceedsTheSearchBound()
    {
        var before = string.Join("\n", Enumerable.Range(0, 600).Select(static i => $"a {i}")) + "\n";
        var after = string.Join("\n", Enumerable.Range(0, 600).Select(static i => $"b {i}")) + "\n";
        var fallback = UnifiedDiff.Create(before, after, "a.txt", 4 * 1_024 * 1_024, 10_000)!;

        Assert.Equal(600, fallback.Additions);
        Assert.Equal(600, fallback.Deletions);
        Assert.False(fallback.Truncated);
        Assert.Contains("-a 0\n", fallback.Unified);
        Assert.Contains("+b 599\n", fallback.Unified);
    }

    private static WorkspaceEditFunctions CreateFunctions(
        string root,
        int maximumWriteBytes = 2 * 1_024 * 1_024,
        int maximumEditedFileBytes = 2 * 1_024 * 1_024,
        int maximumDiffBytes = 16 * 1_024,
        int maximumDiffLines = 400)
    {
        return new WorkspaceEditFunctions(
            Workspace.Create(root, root),
            acquirer: null,
            maximumWriteBytes,
            maximumEditedFileBytes,
            maximumDiffBytes,
            maximumDiffLines);
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
        string json,
        CancellationToken cancellationToken)
    {
        try
        {
            return new ToolInvocation(
                await function.InvokeAsync(Arguments(json), cancellationToken) as JsonElement?,
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
        var invocation = await InvokeAsync(function, json, cancellationToken);
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

    [Fact(Timeout = 30_000)]
    public async Task InstructionSurfacesRequireExplicitAllowWhileOrdinaryFilesDoNot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"wsedit-instr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var workspace = Workspace.Create(root, root);
        var sessionId = AgentSessionId.Create();
        var registry = new PermissionOverrideRegistry();
        var acquirer = new PermissionPolicyAcquirer(
            () => LayerSource(registry), new EmptyVariableSource());
        var functions = new WorkspaceEditFunctions(workspace, acquirer: acquirer);
        var write = functions.Functions.Single(static function => function.Name == "write_text");

        async Task<JsonElement> invokeWrite(string uri)
        {
            var result = await write.InvokeAsync(new AIFunctionArguments(
                new Dictionary<string, object?> { ["uri"] = uri, ["content"] = "# hi" })
            { Context = ContextArguments(sessionId).Context }, TestContext.Current.CancellationToken);
            return (JsonElement)result!;
        }

        // Default posture: instruction surfaces deny model-initiated writes even though the
        // file sits inside the workspace root (ADR 0032 decision 1).
        var gateForbidden = await Record.ExceptionAsync(
            () => invokeWrite("workspace://local/AGENTS.md"));
        Assert.Equal("instructions_write_forbidden", Assert.IsType<AgentToolException>(gateForbidden).Code);

        var gateForbiddenSkill = await Record.ExceptionAsync(
            () => invokeWrite("workspace://local/.agents/skills/x/SKILL.md"));
        Assert.Equal("instructions_write_forbidden", Assert.IsType<AgentToolException>(gateForbiddenSkill).Code);

        // Ordinary workspace files are unaffected by the instruction gate.
        Assert.Null((await Record.ExceptionAsync(
            () => invokeWrite("workspace://local/docs/notes.md"))));

        // A session override allow admits the instruction path explicitly (the migration path).
        registry.Set(sessionId, new PermissionLayer
        {
            Kinds = new Dictionary<PermissionKind, PermissionKindRules>
            {
                [PermissionKind.Write] = new PermissionKindRules { Allow = ["AGENTS.md"] }
            }
        });

        Assert.Null((await Record.ExceptionAsync(
            () => invokeWrite("workspace://local/AGENTS.md"))));
    }

    private sealed record ToolInvocation(JsonElement? Result, AgentToolException? Failure);
    private static IPermissionLayerSource LayerSource(PermissionOverrideRegistry registry) =>
        new RegistrySource(registry);

    private sealed class RegistrySource(PermissionOverrideRegistry registry) : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => null;

        public PermissionLayer? WorkspaceProfile => null;

        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => registry.TryGet(sessionId);
    }

    private sealed class EmptyVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name) => null;
    }

    private static AIFunctionArguments ContextArguments(AgentSessionId sessionId) =>
        new(new Dictionary<string, object?>())
        {
            Context = new Dictionary<object, object?>
            {
                [typeof(AgentToolContext)] = new AgentToolContext(
                    sessionId, AgentRunId.Create(), AgentToolCallId.Create(),
                    static (_, _) => ValueTask.CompletedTask)
            }
        };
}
