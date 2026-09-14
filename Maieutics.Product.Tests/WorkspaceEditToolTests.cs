using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
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

        functions.Select(static function => function.Name)
            .Should().Equal("write_text", "edit_text", "apply_patch");
        functions.Should().OnlyContain(static function =>
            function.JsonSchema.ValueKind == JsonValueKind.Object &&
            function.JsonSchema.GetProperty("type").GetString() == "object");

        var write = Function(functions, "write_text");
        await write.Awaiting(w => w.InvokeAsync(
                Arguments("""{"uri":"workspace://local/a.txt","content":"x","extra":1}"""),
                cancellationToken).AsTask())
            .Should().ThrowAsync<Exception>()
            .Where(static exception =>
                exception.GetType() == typeof(ArgumentException) ||
                exception.GetType() == typeof(JsonException));
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

        result.Uri.Should().Be("workspace://local/deep/nested/a.txt");
        result.Operation.Should().Be("created");
        result.BeforeBytes.Should().BeNull();
        result.AfterBytes.Should().Be(8);
        result.Diff.Should().NotBeNull();
        result.Diff.Unified.Should().Be(
            "--- a/deep/nested/a.txt\n+++ b/deep/nested/a.txt\n@@ -0,0 +1,2 @@\n+one\n+two\n");
        result.Diff.Additions.Should().Be(2);
        result.Diff.Deletions.Should().Be(0);
        result.Diff.Truncated.Should().BeFalse();
        (await File.ReadAllTextAsync(
                Path.Combine(workspace.Path, "deep", "nested", "a.txt"),
                cancellationToken))
            .Should().Be("one\ntwo\n");
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
        updated.Operation.Should().Be("updated");
        updated.BeforeBytes.Should().Be(11);
        updated.Diff.Should().NotBeNull();
        updated.Diff.Unified.Should().Be(
            "--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1,2 @@\n alpha\n-beta\n+delta\n");
        updated.Diff.Deletions.Should().Be(1);
        updated.Diff.Additions.Should().Be(1);

        var unchanged = Result<WriteTextResult>(await InvokeAsync(
                write,
                """{"uri":"workspace://local/a.txt","content":"alpha\ndelta\n"}""",
                cancellationToken),
            WorkspaceEditJsonSerializerContext.Default.WriteTextResult);
        unchanged.Operation.Should().Be("unchanged");
        unchanged.Diff.Should().BeNull();
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

        result.Replacements.Should().Be(1);
        result.Diff.Unified.Should().Contain("-gamma\n+GAMMA\n");
        (await File.ReadAllBytesAsync(marked, cancellationToken))
            .Should().Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("alpha\nGAMMA\n")]);
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

        (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/dupe.txt","oldText":"absent","newText":"new"}""",
                cancellationToken)).Code.Should().Be("workspace_edit_target_not_found");

        (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/dupe.txt","oldText":"x","newText":"new"}""",
                cancellationToken)).Code.Should().Be("workspace_edit_target_not_unique");

        (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/dupe.txt","oldText":"x","newText":"x"}""",
                cancellationToken)).Code.Should().Be("workspace_invalid_arguments");

        (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/bin.dat","oldText":"x","newText":"y"}""",
                cancellationToken)).Code.Should().Be("workspace_binary_file");

        (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/missing.txt","oldText":"x","newText":"y"}""",
                cancellationToken)).Code.Should().Be("workspace_path_not_found");
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
        result.Replacements.Should().Be(1);
        (await File.ReadAllTextAsync(crlf, cancellationToken)).Should().Be("FIRST\r\nSECOND\r\n");

        await File.WriteAllTextAsync(
            workspace.Path + Path.DirectorySeparatorChar + "dupe.txt",
            "x\ny\nx\n",
            cancellationToken);
        var all = Result<EditTextResult>(await InvokeAsync(
                edit,
                """{"uri":"workspace://local/dupe.txt","oldText":"x","newText":"z","replaceAll":true}""",
                cancellationToken),
            WorkspaceEditJsonSerializerContext.Default.EditTextResult);
        all.Replacements.Should().Be(2);
        (await File.ReadAllTextAsync(
                workspace.Path + Path.DirectorySeparatorChar + "dupe.txt",
                cancellationToken))
            .Should().Be("z\ny\nz\n");
    }

    [Fact(Timeout = 30_000)]
    public async Task EditToolsRefuseGitPathsDirectoriesOversizedAndNonUtf8Content()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(workspace.Path, "sub"));
        var write = Function(CreateFunctions(workspace.Path, maximumWriteBytes: 8).Functions, "write_text");
        var edit = Function(CreateFunctions(workspace.Path, maximumWriteBytes: 8).Functions, "edit_text");

        (await ShouldFailAsync(write,
                """{"uri":"workspace://local/.git/config","content":"x"}""",
                cancellationToken)).Code.Should().Be("workspace_path_denied");
        (await ShouldFailAsync(write,
                """{"uri":"workspace://local/","content":"x"}""",
                cancellationToken)).Code.Should().Be("workspace_invalid_uri");
        (await ShouldFailAsync(write,
                """{"uri":"workspace://local/sub","content":"x"}""",
                cancellationToken)).Code.Should().Be("workspace_not_regular_file");
        (await ShouldFailAsync(write,
                """{"uri":"workspace://local/big.txt","content":"0123456789"}""",
                cancellationToken)).Code.Should().Be("workspace_file_too_large");
        (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/sub","oldText":"a","newText":"b"}""",
                cancellationToken)).Code.Should().Be("workspace_not_regular_file");
        (await ShouldFailAsync(edit,
                """{"uri":"workspace://local/missing.txt","oldText":"a","newText":"b"}""",
                cancellationToken)).Code.Should().Be("workspace_path_not_found");
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

        (await ShouldFailAsync(write, """{"uri":"workspace://local/link.txt","content":"y\n"}""", cancellationToken))
            .Code.Should().Be("workspace_symbolic_link_not_allowed");
        (await ShouldFailAsync(edit, """{"uri":"workspace://local/link.txt","oldText":"x","newText":"y"}""", cancellationToken))
            .Code.Should().Be("workspace_symbolic_link_not_allowed");
    }

    [Fact]
    public void UnifiedDiffRendersCreationDeletionAndTrailingNewlineChanges()
    {
        // Creation from nothing.
        var creation = UnifiedDiff.Create(null, "one\ntwo\n", "a.txt", 64 * 1_024, 400)!;
        creation.Unified.Should().Be(
            "--- a/a.txt\n+++ b/a.txt\n@@ -0,0 +1,2 @@\n+one\n+two\n");
        creation.Additions.Should().Be(2);
        creation.Deletions.Should().Be(0);

        // Deletion to nothing.
        var deletion = UnifiedDiff.Create("one\ntwo\n", "", "a.txt", 64 * 1_024, 400)!;
        deletion.Unified.Should().Be(
            "--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +0,0 @@\n-one\n-two\n");
        deletion.Deletions.Should().Be(2);

        // Equal texts.
        UnifiedDiff.Create("same\n", "same\n", "a.txt", 64 * 1_024, 400).Should().BeNull();

        // A trailing-newline-only change still renders as a final-line edit.
        var marker = UnifiedDiff.Create("one\ntwo", "one\ntwo\n", "a.txt", 64 * 1_024, 400)!;
        marker.Unified.Should().Contain("-two");
        marker.Unified.Should().Contain("+two\n");
        marker.Unified.Should().Contain(@"\ No newline at end of file");
        marker.Additions.Should().Be(1);
        marker.Deletions.Should().Be(1);
    }

    [Fact]
    public void UnifiedDiffSeparatesDistantChangesAndHonorsOutputBounds()
    {
        var before = string.Join("\n", Enumerable.Range(0, 20).Select(static i => $"line {i}")) + "\n";
        var after = before.Replace("line 2", "LINE 2").Replace("line 15", "LINE 15");
        var spread = UnifiedDiff.Create(before, after, "a.txt", 64 * 1_024, 400)!;
        spread.Unified.Should().Contain("@@ -1,6 +1,6 @@");
        spread.Unified.Should().Contain("@@ -13,7 +13,7 @@");
        spread.Additions.Should().Be(2);
        spread.Deletions.Should().Be(2);
        spread.Truncated.Should().BeFalse();

        // Close changes merge into one hunk.
        var close = UnifiedDiff.Create("a\nb\nc\nd\ne\nf\ng\n", "a\nb\nC\nd\nE\nf\ng\n", "a.txt", 64 * 1_024, 400)!;
        close.Unified.Should().Contain("@@ -1,7 +1,7 @@");

        var bounded = UnifiedDiff.Create(before, after, "a.txt", 64 * 1_024, 5)!;
        bounded.Truncated.Should().BeTrue();
        bounded.Unified.Should().Contain("diff output bound reached");
    }

    [Fact]
    public void UnifiedDiffFallsBackWhenTheEditDistanceExceedsTheSearchBound()
    {
        var before = string.Join("\n", Enumerable.Range(0, 600).Select(static i => $"a {i}")) + "\n";
        var after = string.Join("\n", Enumerable.Range(0, 600).Select(static i => $"b {i}")) + "\n";
        var fallback = UnifiedDiff.Create(before, after, "a.txt", 4 * 1_024 * 1_024, 10_000)!;

        fallback.Additions.Should().Be(600);
        fallback.Deletions.Should().Be(600);
        fallback.Truncated.Should().BeFalse();
        fallback.Unified.Should().Contain("-a 0\n");
        fallback.Unified.Should().Contain("+b 599\n");
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
            return new ToolInvocation((JsonElement?)null, exception);
        }
    }

    private static async Task<AgentToolException> ShouldFailAsync(
        AIFunction function,
        string json,
        CancellationToken cancellationToken)
    {
        var invocation = await InvokeAsync(function, json, cancellationToken);
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
