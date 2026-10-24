using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using ScriptedWorkspaceChatClient = Maieutics.Testing.ScriptedChatClient;
namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class WorkspaceToolsTests
{
    [Fact(Timeout = 30_000)]
    public async Task WorkspaceFunctionsExposeSeparateStrictSchemasAndTypedJsonResults()
    {
        using var workspace = TemporaryWorkspace.Create();
        var functions = CreateFunctions(workspace.Path).Functions;

        Assert.Equal(["list_directory", "read_text", "search_text"], functions.Select(static function => function.Name));
        Assert.All(functions, static function => Assert.True(function.JsonSchema.ValueKind == JsonValueKind.Object &&
            function.JsonSchema.GetProperty("type").GetString() == "object"));

        var list = functions.Single(static function => function.Name == "list_directory");
        var result = await list.InvokeAsync(
            Arguments("{}"),
            TestContext.Current.CancellationToken);
        Assert.IsType<JsonElement>(result);

        var assertion44 = await Assert.ThrowsAnyAsync<Exception>(async () => { await list.InvokeAsync(
            Arguments("""{"unexpected":true}"""),
            TestContext.Current.CancellationToken).AsTask(); });
        Assert.True(((Func<Exception, bool>)(static exception => exception.GetType() == typeof(ArgumentException) ||
                exception.GetType() == typeof(JsonException)))(assertion44));
    }

    [Fact(Timeout = 30_000)]
    public async Task ListDirectorySortsPagesAndBoundsEntryCount()
    {
        using var workspace = TemporaryWorkspace.Create();
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "c.txt"), "ccc",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "a.txt"), "a", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(workspace.Path, "b"));
        var functions = CreateFunctions(workspace.Path, maximumDirectoryEntries: 3);
        var tool = Function(functions, "list_directory");

        var first = Result<ListDirectoryResult>(await InvokeAsync(
            tool,
            """{"pageSize":2}"""), WorkspaceJsonSerializerContext.Default.ListDirectoryResult);

        Assert.Equal("workspace://local/", first.Uri);
        Assert.Equal(["a.txt", "b"], first.Entries.Select(static entry => entry.Name));
        Assert.Equivalent(new
        {
            Uri = "workspace://local/a.txt",
            Kind = "file",
            SizeBytes = 1L
        }, first.Entries[0]);
        Assert.NotNull(first.NextCursor);

        Directory.Delete(Path.Combine(workspace.Path, "b"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "ab"));

        var second = Result<ListDirectoryResult>(await InvokeAsync(
                tool,
                $$"""{"cursor":{{JsonSerializer.Serialize(first.NextCursor)}}}"""),
            WorkspaceJsonSerializerContext.Default.ListDirectoryResult);
        Assert.Equal(["c.txt"], second.Entries.Select(static entry => entry.Name));
        Assert.Null(second.NextCursor);

        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "d.txt"), "d", TestContext.Current.CancellationToken);
        var failure = await InvokeAsync(tool, "{}");
        Assert.Equal("workspace_directory_too_large", ShouldFailure(failure).Code);
    }

    [Theory]
    [InlineData("file:///tmp/value")]
    [InlineData("workspace://other/value")]
    [InlineData("workspace://user@local/value")]
    [InlineData("workspace://local:80/value")]
    [InlineData("workspace://local/value?query")]
    [InlineData("workspace://local/value#fragment")]
    [InlineData("workspace://local/%")]
    [InlineData("workspace://local/%2")]
    [InlineData("workspace://local/%GG")]
    [InlineData("workspace://local/%2e")]
    [InlineData("workspace://local/%2E%2E/value")]
    [InlineData("workspace://local/value%2fchild")]
    [InlineData("workspace://local/value%5Cchild")]
    public void WorkspaceUrisRejectNonCanonicalOrEscapingValues(string uri)
    {
        using var workspace = TemporaryWorkspace.Create();
        var snapshot = Workspace.Create(workspace.Path, workspace.Path).Capture();

        Assert.Equal("workspace_invalid_uri", Assert.ThrowsAny<WorkspaceException>(() => snapshot.Resolve(uri)).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkspaceUrisDecodeOnceAndRejectGitAndSymbolicLinkTraversal()
    {
        using var workspace = TemporaryWorkspace.Create();
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "%2e%2e"), "literal percent name",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".git"));
        var functions = CreateFunctions(workspace.Path);
        var read = Function(functions, "read_text");

        var literal = Result<ReadTextResult>(await InvokeAsync(
                read,
                """{"uri":"workspace://local/%252e%252e"}"""),
            WorkspaceJsonSerializerContext.Default.ReadTextResult);
        Assert.Equal("literal percent name", literal.Text);

        var git = await InvokeAsync(read, """{"uri":"workspace://local/.git/config"}""");
        Assert.Equal("workspace_path_denied", ShouldFailure(git).Code);

        if (!OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(workspace.ParentPath, "outside.txt");
            await File.WriteAllTextAsync(outside, "outside", TestContext.Current.CancellationToken);
            var outsideDirectory = Path.Combine(workspace.ParentPath, "outside-directory");
            Directory.CreateDirectory(outsideDirectory);
            await File.WriteAllTextAsync(Path.Combine(outsideDirectory, "nested.txt"), "outside nested",
                TestContext.Current.CancellationToken);
            File.CreateSymbolicLink(Path.Combine(workspace.Path, "final-link"), outside);
            Directory.CreateSymbolicLink(Path.Combine(workspace.Path, "directory-link"), outsideDirectory);

            var listed = Result<ListDirectoryResult>(await InvokeAsync(
                    Function(functions, "list_directory"),
                    "{}"),
                WorkspaceJsonSerializerContext.Default.ListDirectoryResult);
            Assert.All(listed.Entries.Where(static entry => entry.Name.EndsWith("-link", StringComparison.Ordinal)), static entry => Assert.True(entry.Kind == "symbolic_link"));

            Assert.Equal("workspace_symbolic_link_not_allowed", ShouldFailure(await InvokeAsync(
                    read,
                    """{"uri":"workspace://local/final-link"}"""))
                .Code);
            Assert.Equal("workspace_symbolic_link_not_allowed", ShouldFailure(await InvokeAsync(
                    read,
                    """{"uri":"workspace://local/directory-link/nested.txt"}"""))
                .Code);
        }
    }

    [Fact]
    public void UnixSafeOpenRejectsPathComponentsReplacedWithSymbolicLinks()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Unix safe-open rejects symbolic-link-swapped path components; the contract is Unix-only.");

        using var workspace = TemporaryWorkspace.Create();
        var outside = Path.Combine(workspace.ParentPath, "outside.txt");
        File.WriteAllText(outside, "outside");
        var directory = Directory.CreateDirectory(Path.Combine(workspace.Path, "directory")).FullName;
        var file = Path.Combine(directory, "value.txt");
        File.WriteAllText(file, "inside");
        var snapshot = Workspace.Create(workspace.Path, workspace.Path).Capture();
        var resolved = snapshot.Resolve("workspace://local/directory/value.txt", false);

        File.Delete(file);
        File.CreateSymbolicLink(file, outside);

        Assert.Equal("workspace_symbolic_link_not_allowed", Assert.ThrowsAny<WorkspaceException>(() => snapshot.OpenVerifiedRead(resolved)).Code);

        File.Delete(file);
        Directory.Delete(directory);
        Directory.CreateSymbolicLink(directory, workspace.ParentPath);

        Assert.ThrowsAny<IOException>(() => snapshot.OpenVerifiedRead(resolved));
    }

    [Fact]
    public void WorkspaceRejectsSymbolicLinksAndResolvesRelativePathsAtStartup()
    {
        using var workspace = TemporaryWorkspace.Create();
        var child = Directory.CreateDirectory(Path.Combine(workspace.Path, "child")).FullName;

        Assert.Equal(child, Workspace.Create("child", workspace.Path).Capture().RootPath);
        Assert.ThrowsAny<ArgumentException>(() => Workspace.Create(" ", workspace.Path));

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(workspace.ParentPath, "workspace-link");
            Directory.CreateSymbolicLink(link, workspace.Path);
            Assert.ThrowsAny<ArgumentException>(() => Workspace.Create(link, workspace.ParentPath));
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ReadTextUsesLineContinuationAndRejectsInvalidOrUnboundedFiles()
    {
        using var workspace = TemporaryWorkspace.Create();
        var tool = Function(CreateFunctions(workspace.Path), "read_text");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "lines.txt"), "零\none\ntwo\nthree",
            new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var page = Result<ReadTextResult>(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/lines.txt","startLine":2,"maxLines":2}"""),
            WorkspaceJsonSerializerContext.Default.ReadTextResult);
        Assert.Equivalent(new
        {
            Uri = "workspace://local/lines.txt",
            StartLine = (int?)2,
            EndLine = (int?)3,
            Text = "one\ntwo",
            Truncated = true,
            NextStartLine = (int?)4
        }, page);

        var continuation = Result<ReadTextResult>(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/lines.txt","startLine":4}"""),
            WorkspaceJsonSerializerContext.Default.ReadTextResult);
        Assert.Equal("three", continuation.Text);
        Assert.False(continuation.Truncated);

        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "byte-pages.txt"),
            $"{new string('a', 40_000)}\n{new string('b', 40_000)}", new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var bytePage = Result<ReadTextResult>(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/byte-pages.txt"}"""),
            WorkspaceJsonSerializerContext.Default.ReadTextResult);
        Assert.Equal(40_000, bytePage.Text.Length);
        Assert.Equal(2, bytePage.NextStartLine);

        await File.WriteAllBytesAsync(
            Path.Combine(workspace.Path, "invalid.txt"),
            [0xc3, 0x28],
            TestContext.Current.CancellationToken);
        Assert.Equal("workspace_invalid_utf8", ShouldFailure(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/invalid.txt"}"""))
            .Code);

        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "binary.txt"), "text\0data",
            TestContext.Current.CancellationToken);
        Assert.Equal("workspace_binary_file", ShouldFailure(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/binary.txt"}"""))
            .Code);

        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "long-line.txt"), new string('x', 64 * 1_024 + 1),
            TestContext.Current.CancellationToken);
        Assert.Equal("workspace_line_too_long", ShouldFailure(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/long-line.txt"}"""))
            .Code);

        await using (var oversized = File.Create(Path.Combine(workspace.Path, "oversized.txt")))
        {
            var block = new byte[64 * 1_024];
            for (var index = 0; index < block.Length; index += 2)
            {
                block[index] = (byte)'a';
                block[index + 1] = (byte)'\n';
            }

            var remaining = 8 * 1_024 * 1_024 + 2;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, block.Length);
                await oversized.WriteAsync(block.AsMemory(0, count), TestContext.Current.CancellationToken);
                remaining -= count;
            }
        }

        Assert.Equal("workspace_file_too_large", ShouldFailure(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/oversized.txt","startLine":5000000}"""))
            .Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task SearchTextBoundsFilesSkipsUnsafeContentAndLimitsPreviews()
    {
        using var workspace = TemporaryWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "nested"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "a.txt"), "Needle\nneedle",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "nested", "b.txt"), "Needle again",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "preview.txt"),
            $"{new string('p', 550)}Needle{new string('q', 100)}", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(workspace.Path, "binary.bin"),
            [0, 1, 2],
            TestContext.Current.CancellationToken);
        await using (var large = File.Create(Path.Combine(workspace.Path, "large.txt")))
        {
            large.SetLength(2L * 1_024 * 1_024 + 1);
        }

        if (!OperatingSystem.IsWindows())
            File.CreateSymbolicLink(
                Path.Combine(workspace.Path, "linked.txt"),
                Path.Combine(workspace.Path, "a.txt"));

        var tool = Function(CreateFunctions(workspace.Path), "search_text");
        var result = Result<SearchTextResult>(await InvokeAsync(
                tool,
                """{"query":"Needle"}"""),
            WorkspaceJsonSerializerContext.Default.SearchTextResult);

        Assert.Equal(3, result.Matches.Count());
        Assert.Equal(1, result.SkippedBinaryFiles);
        Assert.Equal(1, result.SkippedLargeFiles);
        Assert.All(result.Matches, static match => Assert.True(match.Preview.Length <= 512));
        Assert.Equal(551, result.Matches.Single(static match => match.Uri.EndsWith("preview.txt", StringComparison.Ordinal))
            .Column);
        if (!OperatingSystem.IsWindows()) Assert.Equal(1, result.SkippedSymbolicLinks);

        var regex = Result<SearchTextResult>(await InvokeAsync(
                tool,
                """{"query":"n[e]+dle","regex":true,"caseSensitive":false}"""),
            WorkspaceJsonSerializerContext.Default.SearchTextResult);
        Assert.Equal(4, regex.Matches.Count());
    }

    [Fact(Timeout = 30_000)]
    public async Task SearchTextEnforcesFileDirectoryAndCancellationLimits()
    {
        using var workspace = TemporaryWorkspace.Create();
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "a.txt"), "none",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "b.txt"), "target",
            TestContext.Current.CancellationToken);
        var fileLimited = Function(
            CreateFunctions(workspace.Path, 1, 10),
            "search_text");
        var limited = Result<SearchTextResult>(await InvokeAsync(
                fileLimited,
                """{"query":"target"}"""),
            WorkspaceJsonSerializerContext.Default.SearchTextResult);
        Assert.Empty(limited.Matches);
        Assert.True(limited.Truncated);

        var directoryLimited = Function(
            CreateFunctions(workspace.Path, 10, 1),
            "search_text");
        Assert.Equal("workspace_directory_too_large", ShouldFailure(await InvokeAsync(directoryLimited, """{"query":"target"}"""))
            .Code);

        var invalidRegex = await InvokeAsync(
            Function(CreateFunctions(workspace.Path), "search_text"),
            JsonSerializer.Serialize(new
            {
                query = new string('x', 513),
                regex = true
            }));
        Assert.Equal("workspace_invalid_arguments", ShouldFailure(invalidRegex).Code);

        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "b.txt"), "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "c.txt"), "x", TestContext.Current.CancellationToken);
        var scanLimited = Function(CreateFunctions(
            workspace.Path,
            10,
            10,
            4,
            5), "search_text");
        var scanResult = Result<SearchTextResult>(await InvokeAsync(
                scanLimited,
                """{"query":"x"}"""),
            WorkspaceJsonSerializerContext.Default.SearchTextResult);
        Assert.EndsWith("/b.txt", Assert.Single(scanResult.Matches).Uri);
        Assert.Equal(5, scanResult.ScannedBytes);
        Assert.True(scanResult.Truncated);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var token = canceled.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await fileLimited.InvokeAsync(
            Arguments("""{"query":"target"}"""),
            token).AsTask(); });
    }

    [Fact(Timeout = 10_000)]
    public async Task TextToolsRejectNonRegularFilesWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Creates a fifo with mkfifo to prove text tools reject non-regular files; mkfifo is Unix-only.");

        using var workspace = TemporaryWorkspace.Create();
        var fifo = Path.Combine(workspace.Path, "input.fifo");
        using (var process = Process.Start(new ProcessStartInfo("mkfifo")
        {
            UseShellExecute = false,
            ArgumentList = { fifo }
        }) ?? throw new InvalidOperationException("mkfifo did not start."))
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }

        var functions = CreateFunctions(workspace.Path);
        Assert.Equal("workspace_not_regular_file", ShouldFailure(await InvokeAsync(
                Function(functions, "read_text"),
                """{"uri":"workspace://local/input.fifo"}"""))
            .Code);
        Assert.Equal("workspace_not_regular_file", ShouldFailure(await InvokeAsync(
                Function(functions, "search_text"),
                """{"query":"value","uri":"workspace://local/input.fifo"}"""))
            .Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task AgentUsesProductionWorkspaceToolsForDiscoveryAndRead()
    {
        using var workspace = TemporaryWorkspace.Create();
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "note.txt"), "workspace body",
            TestContext.Current.CancellationToken);
        var functions = CreateFunctions(workspace.Path);
        var client = new ScriptedWorkspaceChatClient(
            (_, _) => StreamUpdateAsync(ToolCallUpdate("list-call", "list_directory")),
            (messages, _) =>
            {
                Assert.Contains("note.txt", Assert.IsType<JsonElement>(messages.SelectMany(static message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Single(static result => result.CallId == "list-call")
                    .Result).GetRawText());
                return StreamUpdateAsync(ToolCallUpdate(
                    "read-call",
                    "read_text",
                    ("uri", "workspace://local/note.txt")));
            },
            (messages, _) =>
            {
                Assert.Contains("workspace body", Assert.IsType<JsonElement>(messages.SelectMany(static message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Single(static result => result.CallId == "read-call")
                    .Result).GetRawText());
                return StreamUpdateAsync(new ChatResponseUpdate(ChatRole.Assistant, "read workspace body"));
            });
        var session = new AgentSession(
            client,
            tools: functions.Functions);

        await using var run = await session.StartTurnAsync(
            AgentTurn.FromText("inspect the workspace"),
            TestContext.Current.CancellationToken);
        await foreach (var _ in run.Events.WithCancellation(TestContext.Current.CancellationToken))
        {
        }

        var result = await run.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("read workspace body", result.AssistantMessage.Text);
        Assert.Equal(2, Assert.Single(result.Transcript.Turns).Messages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>().Count());
        Assert.Equal(3, client.Requests.Count());
    }

    [Fact(Timeout = 30_000)]
    public async Task OpeningLinksKeepsRootChangesListingsAndInvalidatesCursors()
    {
        using var workspace = TemporaryWorkspace.Create();
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "a.txt"), "startup a",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "b.txt"), "startup b",
            TestContext.Current.CancellationToken);
        var other = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "other workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(other, "other.txt"), "other", TestContext.Current.CancellationToken);
        var home = WorkspaceHome.Ensure(workspace.Path, workspace.Path);
        var context = Workspace.Create(home);
        var tool = Function(new WorkspaceFunctions(context), "list_directory");

        var startup = Result<ListDirectoryResult>(await InvokeAsync(
                tool,
                """{"pageSize":1}"""),
            WorkspaceJsonSerializerContext.Default.ListDirectoryResult);
        Assert.Equal(".maieutics", Assert.Single(startup.Entries).Name);
        Assert.NotNull(startup.NextCursor);

        var opened = context.OpenLink(other, null);
        Assert.Equal(workspace.Path, opened.RootPath);
        var links = opened.Links ?? throw new InvalidOperationException("The snapshot carried no links.");
        Assert.Equal("other workspace", Assert.Single(links.Records).Name);

        var staleCursor = await InvokeAsync(
            tool,
            $$"""{"cursor":{{JsonSerializer.Serialize(startup.NextCursor)}}}""");
        Assert.Equal("workspace_invalid_cursor", ShouldFailure(staleCursor).Code);

        var linkUri = "workspace://local/projects/other%20workspace";
        var linked = Result<ListDirectoryResult>(await InvokeAsync(
                tool,
                $$"""{"uri":{{JsonSerializer.Serialize(linkUri)}}}"""),
            WorkspaceJsonSerializerContext.Default.ListDirectoryResult);
        Assert.Equal(linkUri, linked.Uri);
        Assert.Equivalent(new
        {
            Name = "other.txt",
            Uri = $"{linkUri}/other.txt",
            Kind = "file",
            SizeBytes = 5L
        }, Assert.Single(linked.Entries));

        var closed = context.CloseLink("other workspace");
        Assert.Empty((closed.Links ?? throw new InvalidOperationException("The snapshot carried no links."))
            .Records);
        var afterClose = await InvokeAsync(
            tool,
            $$"""{"uri":{{JsonSerializer.Serialize(linkUri)}}}""");
        Assert.Equal("workspace_path_not_found", ShouldFailure(afterClose).Code);
    }

    [Fact]
    public void HostFreezesWorkspaceStartupConfigurationAndRegistersProductionTools()
    {
        using var workspace = TemporaryWorkspace.Create();
        var jsonRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "json")).FullName;
        var aliasRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "alias")).FullName;
        var standardRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "standard")).FullName;
        var commandRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "command")).FullName;
        var reloadRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "reload")).FullName;
        var configurationFile = Path.Combine(workspace.Path, "maieutics.json");
        WriteWorkspaceConfiguration(configurationFile, jsonRoot);

        using (new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_CONFIG"] = null,
            ["MAIEUTICS_WORKSPACE"] = aliasRoot,
            ["Maieutics__Workspace__Root"] = standardRoot
        }))
        {
            var standardBuilder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            using var standardHost = standardBuilder.Build();
            Assert.Equal(standardRoot, standardHost.Services.GetRequiredService<Workspace>().Capture().RootPath);

            var commandBuilder = MaieuticsHost.CreateApplicationBuilder(
                ["--config", configurationFile, "--workspace", commandRoot]);
            using var commandHost = commandBuilder.Build();
            Assert.Equal(commandRoot, commandHost.Services.GetRequiredService<Workspace>().Capture().RootPath);
            Assert.Equal(["list_directory", "read_text", "search_text", "write_text", "edit_text", "apply_patch", "list_resources", "repl_execute", "repl_create", "repl_list", "repl_restart", "repl_close", "agent_spawn", "task_wait", "task_cancel",
            ], commandHost.Services.GetRequiredService<IReadOnlyList<AIFunction>>()
                .Select(static function => function.Name));
        }

        using (new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_CONFIG"] = null,
            ["MAIEUTICS_WORKSPACE"] = aliasRoot,
            ["Maieutics__Workspace__Root"] = null
        }))
        {
            var aliasBuilder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            using var aliasHost = aliasBuilder.Build();
            Assert.Equal(aliasRoot, aliasHost.Services.GetRequiredService<Workspace>().Capture().RootPath);
        }

        using (new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_CONFIG"] = null,
            ["MAIEUTICS_WORKSPACE"] = null,
            ["Maieutics__Workspace__Root"] = null
        }))
        {
            var frozenBuilder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            WriteWorkspaceConfiguration(configurationFile, reloadRoot);
            ((IConfigurationRoot)frozenBuilder.Configuration).Reload();
            using var frozenHost = frozenBuilder.Build();
            Assert.Equal(jsonRoot, frozenHost.Services.GetRequiredService<Workspace>().Capture().RootPath);
        }

        var path = workspace.Path;

        Assert.ThrowsAny<DirectoryNotFoundException>(() => MaieuticsHost.CreateApplicationBuilder(
                ["--config", configurationFile, "--workspace", Path.Combine(path, "missing")]));
    }

    [Fact]
    public void WorkspaceUsesStartupDirectoryAndRejectsSymbolicLinkRoot()
    {
        using var workspace = TemporaryWorkspace.Create();
        var relative = Directory.CreateDirectory(Path.Combine(workspace.Path, "relative")).FullName;

        Assert.Equal(workspace.Path, Workspace.Create(null, workspace.Path).Capture().RootPath);
        Assert.Equal(relative, Workspace.Create("relative", workspace.Path).Capture().RootPath);

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(workspace.ParentPath, "workspace-link");
            Directory.CreateSymbolicLink(link, workspace.Path);
            Assert.ThrowsAny<ArgumentException>(() => Workspace.Create(link, workspace.ParentPath));
        }
    }

    private static WorkspaceFunctions CreateFunctions(
        string root,
        int maximumFiles = 10_000,
        int maximumDirectoryEntries = 10_000,
        int maximumFileBytes = 2 * 1_024 * 1_024,
        long maximumSearchBytes = 64L * 1_024 * 1_024,
        ResourceRegistry? resources = null)
    {
        return new WorkspaceFunctions(
            Workspace.Create(root, root),
            maximumFiles,
            maximumDirectoryEntries,
            maximumFileBytes,
            maximumSearchBytes,
            resources);
    }

    private static AIFunction Function(WorkspaceFunctions functions, string name)
    {
        return functions.Functions.Single(function => function.Name == name);
    }

    private static AIFunctionArguments Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));
    }

    private static async ValueTask<ToolInvocation> InvokeAsync(AIFunction function, string json)
    {
        try
        {
            return new ToolInvocation(
                await function.InvokeAsync(Arguments(json), TestContext.Current.CancellationToken),
                null);
        }
        catch (AgentToolException exception)
        {
            return new ToolInvocation(null, exception);
        }
    }

    private static ChatResponseUpdate ToolCallUpdate(
        string callId,
        string name,
        params (string Name, object? Value)[] arguments)
    {
        return new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    callId,
                    name,
                    arguments.ToDictionary(static argument => argument.Name, static argument => argument.Value))
            ]);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamUpdateAsync(ChatResponseUpdate update)
    {
        await Task.Yield();
        yield return update;
    }

    private static AgentToolException ShouldFailure(ToolInvocation invocation)
    {
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

    private static void WriteWorkspaceConfiguration(string path, string root)
    {
        File.WriteAllText(path, new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["Workspace"] = new JsonObject
                {
                    ["Root"] = root
                }
            }
        }.ToJsonString());
    }

    private sealed record ToolInvocation(object? Result, AgentToolException? Failure);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> original = new(StringComparer.Ordinal);

        internal EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (name, value) in values)
            {
                original.Add(name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value);
        }
    }

}