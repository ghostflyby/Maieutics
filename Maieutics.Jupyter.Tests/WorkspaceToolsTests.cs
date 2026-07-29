using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class WorkspaceToolsTests
{
    [Fact]
    public async Task ListDirectorySortsPagesAndBoundsEntryCount()
    {
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "c.txt"), "ccc");
        File.WriteAllText(Path.Combine(workspace.Path, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(workspace.Path, "b"));
        var paths = CreateResolver(workspace.Path);
        var tool = new ListDirectoryTool(paths, maximumDirectoryEntries: 3);

        var first = Result<ListDirectoryResult>(await InvokeAsync(
            tool,
            """{"pageSize":2}"""), WorkspaceToolJsonSerializerContext.Default.ListDirectoryResult);

        first.Uri.Should().Be("workspace://local/");
        first.Entries.Select(static entry => entry.Name).Should().Equal("a.txt", "b");
        first.Entries[0].Should().BeEquivalentTo(new
        {
            Uri = "workspace://local/a.txt",
            Kind = "file",
            SizeBytes = 1L
        });
        first.NextCursor.Should().NotBeNull();

        Directory.Delete(Path.Combine(workspace.Path, "b"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "ab"));

        var second = Result<ListDirectoryResult>(await InvokeAsync(
                tool,
                $$"""{"cursor":{{JsonSerializer.Serialize(first.NextCursor)}}}"""),
            WorkspaceToolJsonSerializerContext.Default.ListDirectoryResult);
        second.Entries.Select(static entry => entry.Name).Should().Equal("c.txt");
        second.NextCursor.Should().BeNull();

        File.WriteAllText(Path.Combine(workspace.Path, "d.txt"), "d");
        var failure = await InvokeAsync(tool, "{}");
        Failure(failure).Code.Should().Be("workspace_directory_too_large");
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
        var paths = CreateResolver(workspace.Path);

        var action = () => paths.Resolve(uri);

        action.Should().Throw<WorkspaceToolException>()
            .Which.Code.Should().Be("workspace_invalid_uri");
    }

    [Fact]
    public async Task WorkspaceUrisDecodeOnceAndRejectGitAndSymbolicLinkTraversal()
    {
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "%2e%2e"), "literal percent name");
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".git"));
        var paths = CreateResolver(workspace.Path);
        var read = new ReadTextTool(paths);

        var literal = Result<ReadTextResult>(await InvokeAsync(
                read,
                """{"uri":"workspace://local/%252e%252e"}"""),
            WorkspaceToolJsonSerializerContext.Default.ReadTextResult);
        literal.Text.Should().Be("literal percent name");

        var git = await InvokeAsync(read, """{"uri":"workspace://local/.git/config"}""");
        Failure(git).Code.Should().Be("workspace_path_denied");

        if (!OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(workspace.ParentPath, "outside.txt");
            File.WriteAllText(outside, "outside");
            var outsideDirectory = Path.Combine(workspace.ParentPath, "outside-directory");
            Directory.CreateDirectory(outsideDirectory);
            File.WriteAllText(Path.Combine(outsideDirectory, "nested.txt"), "outside nested");
            File.CreateSymbolicLink(Path.Combine(workspace.Path, "final-link"), outside);
            Directory.CreateSymbolicLink(Path.Combine(workspace.Path, "directory-link"), outsideDirectory);

            var listed = Result<ListDirectoryResult>(await InvokeAsync(
                    new ListDirectoryTool(paths),
                    "{}"),
                WorkspaceToolJsonSerializerContext.Default.ListDirectoryResult);
            listed.Entries.Where(static entry => entry.Name.EndsWith("-link", StringComparison.Ordinal))
                .Should().OnlyContain(static entry => entry.Kind == "symbolic_link");

            Failure(await InvokeAsync(
                    read,
                    """{"uri":"workspace://local/final-link"}"""))
                .Code.Should().Be("workspace_symbolic_link_not_allowed");
            Failure(await InvokeAsync(
                    read,
                    """{"uri":"workspace://local/directory-link/nested.txt"}"""))
                .Code.Should().Be("workspace_symbolic_link_not_allowed");
        }
    }

    [Fact]
    public void WorkspaceRootRejectsSymbolicLinksAndResolvesRelativePathsAtStartup()
    {
        using var workspace = TemporaryWorkspace.Create();
        var child = Directory.CreateDirectory(Path.Combine(workspace.Path, "child")).FullName;

        WorkspaceRoot.Create("child", workspace.Path).Path.Should().Be(child);
        var empty = () => WorkspaceRoot.Create(" ", workspace.Path);
        empty.Should().Throw<ArgumentException>();

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(workspace.ParentPath, "workspace-link");
            Directory.CreateSymbolicLink(link, workspace.Path);
            var action = () => WorkspaceRoot.Create(link, workspace.ParentPath);
            action.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public async Task ReadTextUsesLineContinuationAndRejectsInvalidOrUnboundedFiles()
    {
        using var workspace = TemporaryWorkspace.Create();
        var paths = CreateResolver(workspace.Path);
        var tool = new ReadTextTool(paths);
        File.WriteAllText(Path.Combine(workspace.Path, "lines.txt"), "零\none\ntwo\nthree", new UTF8Encoding(false));

        var page = Result<ReadTextResult>(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/lines.txt","startLine":2,"maxLines":2}"""),
            WorkspaceToolJsonSerializerContext.Default.ReadTextResult);
        page.Should().BeEquivalentTo(new
        {
            Uri = "workspace://local/lines.txt",
            StartLine = (int?)2,
            EndLine = (int?)3,
            Text = "one\ntwo",
            Truncated = true,
            NextStartLine = (int?)4
        });

        var continuation = Result<ReadTextResult>(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/lines.txt","startLine":4}"""),
            WorkspaceToolJsonSerializerContext.Default.ReadTextResult);
        continuation.Text.Should().Be("three");
        continuation.Truncated.Should().BeFalse();

        File.WriteAllText(
            Path.Combine(workspace.Path, "byte-pages.txt"),
            $"{new string('a', 40_000)}\n{new string('b', 40_000)}",
            new UTF8Encoding(false));
        var bytePage = Result<ReadTextResult>(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/byte-pages.txt"}"""),
            WorkspaceToolJsonSerializerContext.Default.ReadTextResult);
        bytePage.Text.Should().HaveLength(40_000);
        bytePage.NextStartLine.Should().Be(2);

        await File.WriteAllBytesAsync(
            Path.Combine(workspace.Path, "invalid.txt"),
            [0xc3, 0x28],
            TestContext.Current.CancellationToken);
        Failure(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/invalid.txt"}"""))
            .Code.Should().Be("workspace_invalid_utf8");

        File.WriteAllText(Path.Combine(workspace.Path, "binary.txt"), "text\0data");
        Failure(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/binary.txt"}"""))
            .Code.Should().Be("workspace_binary_file");

        File.WriteAllText(Path.Combine(workspace.Path, "long-line.txt"), new string('x', 64 * 1_024 + 1));
        Failure(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/long-line.txt"}"""))
            .Code.Should().Be("workspace_line_too_long");

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

        Failure(await InvokeAsync(
                tool,
                """{"uri":"workspace://local/oversized.txt","startLine":5000000}"""))
            .Code.Should().Be("workspace_file_too_large");
    }

    [Fact]
    public async Task SearchTextBoundsFilesSkipsUnsafeContentAndLimitsPreviews()
    {
        using var workspace = TemporaryWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "nested"));
        File.WriteAllText(Path.Combine(workspace.Path, "a.txt"), "Needle\nneedle");
        File.WriteAllText(Path.Combine(workspace.Path, "nested", "b.txt"), "Needle again");
        File.WriteAllText(
            Path.Combine(workspace.Path, "preview.txt"),
            $"{new string('p', 550)}Needle{new string('q', 100)}");
        await File.WriteAllBytesAsync(
            Path.Combine(workspace.Path, "binary.bin"),
            [0, 1, 2],
            TestContext.Current.CancellationToken);
        await using (var large = File.Create(Path.Combine(workspace.Path, "large.txt")))
        {
            large.SetLength(2L * 1_024 * 1_024 + 1);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(
                Path.Combine(workspace.Path, "linked.txt"),
                Path.Combine(workspace.Path, "a.txt"));
        }

        var tool = new SearchTextTool(CreateResolver(workspace.Path));
        var result = Result<SearchTextResult>(await InvokeAsync(
                tool,
                """{"query":"Needle"}"""),
            WorkspaceToolJsonSerializerContext.Default.SearchTextResult);

        result.Matches.Should().HaveCount(3);
        result.SkippedBinaryFiles.Should().Be(1);
        result.SkippedLargeFiles.Should().Be(1);
        result.Matches.Should().OnlyContain(static match => match.Preview.Length <= 512);
        result.Matches.Single(static match => match.Uri.EndsWith("preview.txt", StringComparison.Ordinal))
            .Column.Should().Be(551);
        if (!OperatingSystem.IsWindows())
        {
            result.SkippedSymbolicLinks.Should().Be(1);
        }

        var regex = Result<SearchTextResult>(await InvokeAsync(
                tool,
                """{"query":"n[e]+dle","regex":true,"caseSensitive":false}"""),
            WorkspaceToolJsonSerializerContext.Default.SearchTextResult);
        regex.Matches.Should().HaveCount(4);
    }

    [Fact]
    public async Task SearchTextEnforcesFileDirectoryAndCancellationLimits()
    {
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "a.txt"), "none");
        File.WriteAllText(Path.Combine(workspace.Path, "b.txt"), "target");
        var paths = CreateResolver(workspace.Path);

        var fileLimited = new SearchTextTool(paths, maximumFiles: 1, maximumDirectoryEntries: 10);
        var limited = Result<SearchTextResult>(await InvokeAsync(
                fileLimited,
                """{"query":"target"}"""),
            WorkspaceToolJsonSerializerContext.Default.SearchTextResult);
        limited.Matches.Should().BeEmpty();
        limited.Truncated.Should().BeTrue();

        var directoryLimited = new SearchTextTool(paths, maximumFiles: 10, maximumDirectoryEntries: 1);
        Failure(await InvokeAsync(directoryLimited, """{"query":"target"}"""))
            .Code.Should().Be("workspace_directory_too_large");

        var invalidRegex = await InvokeAsync(
            new SearchTextTool(paths),
            JsonSerializer.Serialize(new
            {
                query = new string('x', 513),
                regex = true
            }));
        Failure(invalidRegex).Code.Should().Be("workspace_invalid_arguments");

        File.WriteAllText(Path.Combine(workspace.Path, "b.txt"), "x");
        File.WriteAllText(Path.Combine(workspace.Path, "c.txt"), "x");
        var scanLimited = new SearchTextTool(
            paths,
            maximumFiles: 10,
            maximumDirectoryEntries: 10,
            maximumFileBytes: 4,
            maximumScanBytes: 5);
        var scanResult = Result<SearchTextResult>(await InvokeAsync(
                scanLimited,
                """{"query":"x"}"""),
            WorkspaceToolJsonSerializerContext.Default.SearchTextResult);
        scanResult.Matches.Should().ContainSingle().Which.Uri.Should().EndWith("/b.txt");
        scanResult.ScannedBytes.Should().Be(5);
        scanResult.Truncated.Should().BeTrue();

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var action = () => fileLimited.InvokeAsync(
            null!,
            Arguments("""{"query":"target"}"""),
            canceled.Token).AsTask();
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Timeout = 10_000)]
    public async Task TextToolsRejectNonRegularFilesWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create();
        var fifo = Path.Combine(workspace.Path, "input.fifo");
        using (var process = Process.Start(new ProcessStartInfo("mkfifo")
               {
                   UseShellExecute = false,
                   ArgumentList = { fifo }
               }) ?? throw new InvalidOperationException("mkfifo did not start."))
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            process.ExitCode.Should().Be(0);
        }

        var paths = CreateResolver(workspace.Path);
        Failure(await InvokeAsync(
                new ReadTextTool(paths),
                """{"uri":"workspace://local/input.fifo"}"""))
            .Code.Should().Be("workspace_not_regular_file");
        Failure(await InvokeAsync(
                new SearchTextTool(paths),
                """{"query":"value","uri":"workspace://local/input.fifo"}"""))
            .Code.Should().Be("workspace_not_regular_file");
    }

    [Fact]
    public async Task AgentUsesProductionWorkspaceToolsForDiscoveryAndRead()
    {
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "note.txt"), "workspace body");
        var paths = CreateResolver(workspace.Path);
        var client = new ScriptedWorkspaceChatClient(
            (_, _) => StreamUpdateAsync(ToolCallUpdate("list-call", "list_directory")),
            (messages, _) =>
            {
                messages.SelectMany(static message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Single(static result => result.CallId == "list-call")
                    .Result.Should().BeOfType<JsonElement>().Which.GetRawText().Should().Contain("note.txt");
                return StreamUpdateAsync(ToolCallUpdate(
                    "read-call",
                    "read_text",
                    ("uri", "workspace://local/note.txt")));
            },
            (messages, _) =>
            {
                messages.SelectMany(static message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Single(static result => result.CallId == "read-call")
                    .Result.Should().BeOfType<JsonElement>().Which.GetRawText().Should().Contain("workspace body");
                return StreamUpdateAsync(new ChatResponseUpdate(ChatRole.Assistant, "read workspace body"));
            });
        var session = new AgentSession(
            client,
            tools:
            [
                new ListDirectoryTool(paths),
                new ReadTextTool(paths),
                new SearchTextTool(paths)
            ]);

        await using var run = await session.StartTurnAsync(
            AgentTurn.FromText("inspect the workspace"),
            TestContext.Current.CancellationToken);
        await foreach (var _ in run.Events.WithCancellation(TestContext.Current.CancellationToken))
        {
        }

        var result = await run.Completion.WaitAsync(TestContext.Current.CancellationToken);
        result.AssistantMessage.Text.Should().Be("read workspace body");
        result.Transcript.Turns.Should().ContainSingle().Which.Messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>()
            .Should().HaveCount(2);
        client.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task WorkspaceContextSwitchesFutureToolInvocationsAndInvalidatesCursors()
    {
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "a.txt"), "startup a");
        File.WriteAllText(Path.Combine(workspace.Path, "b.txt"), "startup b");
        var other = Directory.CreateDirectory(Path.Combine(workspace.ParentPath, "other workspace")).FullName;
        File.WriteAllText(Path.Combine(other, "other.txt"), "other");
        var context = new WorkspaceContext(WorkspaceRoot.Create(workspace.Path, workspace.Path));
        var tool = new ListDirectoryTool(new WorkspacePathResolver(context));

        var startup = Result<ListDirectoryResult>(await InvokeAsync(
                tool,
                """{"pageSize":1}"""),
            WorkspaceToolJsonSerializerContext.Default.ListDirectoryResult);
        startup.Entries.Should().ContainSingle().Which.Name.Should().Be("a.txt");
        startup.NextCursor.Should().NotBeNull();

        var selected = context.Use("../other workspace");
        selected.Root.Path.Should().Be(other);
        selected.HasSessionOverride.Should().BeTrue();
        var switched = Result<ListDirectoryResult>(await InvokeAsync(
                tool,
                "{}"),
            WorkspaceToolJsonSerializerContext.Default.ListDirectoryResult);
        switched.Entries.Should().ContainSingle().Which.Name.Should().Be("other.txt");

        var staleCursor = await InvokeAsync(
            tool,
            $$"""{"cursor":{{JsonSerializer.Serialize(startup.NextCursor)}}}""");
        Failure(staleCursor).Code.Should().Be("workspace_invalid_cursor");

        var reset = context.Reset();
        reset.Root.Path.Should().Be(workspace.Path);
        reset.HasSessionOverride.Should().BeFalse();
        var restored = Result<ListDirectoryResult>(await InvokeAsync(
                tool,
                "{}"),
            WorkspaceToolJsonSerializerContext.Default.ListDirectoryResult);
        restored.Entries.Select(static entry => entry.Name).Should().Equal("a.txt", "b.txt");
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
            standardHost.Services.GetRequiredService<WorkspaceRoot>().Path.Should().Be(standardRoot);
            standardHost.Services.GetRequiredService<WorkspaceContext>().GetSnapshot().Root.Path.Should()
                .Be(standardRoot);

            var commandBuilder = MaieuticsHost.CreateApplicationBuilder(
                ["--config", configurationFile, "--workspace", commandRoot]);
            using var commandHost = commandBuilder.Build();
            commandHost.Services.GetRequiredService<WorkspaceRoot>().Path.Should().Be(commandRoot);
            commandHost.Services.GetRequiredService<IReadOnlyList<IAgentTool>>()
                .Select(static tool => tool.Descriptor.Name)
                .Should().Equal("list_directory", "read_text", "search_text");
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
            aliasHost.Services.GetRequiredService<WorkspaceRoot>().Path.Should().Be(aliasRoot);
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
            frozenHost.Services.GetRequiredService<WorkspaceRoot>().Path.Should().Be(jsonRoot);
        }

        var invalid = () => MaieuticsHost.CreateApplicationBuilder(
            ["--config", configurationFile, "--workspace", Path.Combine(workspace.Path, "missing")]);
        invalid.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void WorkspaceRootUsesStartupDirectoryAndRejectsSymbolicLinkRoot()
    {
        using var workspace = TemporaryWorkspace.Create();
        var relative = Directory.CreateDirectory(Path.Combine(workspace.Path, "relative")).FullName;

        WorkspaceRoot.Create(null, workspace.Path).Path.Should().Be(workspace.Path);
        WorkspaceRoot.Create("relative", workspace.Path).Path.Should().Be(relative);

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(workspace.ParentPath, "workspace-link");
            Directory.CreateSymbolicLink(link, workspace.Path);
            var action = () => WorkspaceRoot.Create(link, workspace.ParentPath);
            action.Should().Throw<ArgumentException>();
        }
    }

    private static WorkspacePathResolver CreateResolver(string root) =>
        new(WorkspaceRoot.Create(root, root));

    private static AgentToolArguments Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AgentToolArguments(document.RootElement);
    }

    private static ValueTask<AgentToolOutcome> InvokeAsync(
        IAgentTool tool,
        string json) =>
        tool.InvokeAsync(null!, Arguments(json), TestContext.Current.CancellationToken);

    private static ChatResponseUpdate ToolCallUpdate(
        string callId,
        string name,
        params (string Name, object? Value)[] arguments) =>
        new(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    callId,
                    name,
                    arguments.ToDictionary(static argument => argument.Name, static argument => argument.Value))
            ]);

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamUpdateAsync(ChatResponseUpdate update)
    {
        await Task.Yield();
        yield return update;
    }

    private static AgentToolFailure Failure(AgentToolOutcome outcome) =>
        outcome.Should().BeOfType<AgentToolFailure>().Which;

    private static T Result<T>(AgentToolOutcome outcome, JsonTypeInfo<T> jsonTypeInfo)
    {
        var success = outcome.Should().BeOfType<AgentToolSuccess>().Which;
        var data = success.Contents.Should().ContainSingle().Which
            .Should().BeOfType<DataContent>().Which;
        data.MediaType.Should().Be("application/json");
        return JsonSerializer.Deserialize(data.Data.Span, jsonTypeInfo)
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

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string parentPath, string path)
        {
            ParentPath = parentPath;
            Path = path;
        }

        internal string ParentPath { get; }

        internal string Path { get; }

        internal static TemporaryWorkspace Create()
        {
            var parent = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"maieutics-workspace-tests-{Guid.NewGuid():N}");
            var path = System.IO.Path.Combine(parent, "workspace");
            Directory.CreateDirectory(path);
            return new TemporaryWorkspace(parent, path);
        }

        public void Dispose()
        {
            Directory.Delete(ParentPath, recursive: true);
        }
    }

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
            foreach (var (name, value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private sealed class ScriptedWorkspaceChatClient(
        params Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] responses)
        : IChatClient
    {
        private readonly Lock gate = new();

        private readonly Queue<Func<IReadOnlyList<ChatMessage>, CancellationToken,
            IAsyncEnumerable<ChatResponseUpdate>>> responses = new(responses);

        internal List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new NotSupportedException());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var request = messages.Select(static message => message.Clone()).ToArray();
            Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> response;
            lock (gate)
            {
                Requests.Add(request);
                response = responses.Dequeue();
            }

            return response(request, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}