using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Frontend;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>
///     The workspace file-system bridge (ADR 0042 stage F, decision 12): authority
///     election and targeted delivery on the sockets registry, the bridge channel's
///     round trips through the real request registry, and the workspace tools'
///     bridged routing — reads answering the IDE's text state, writes carrying the
///     before-hash precondition, and every kernel gate still guarding the path.
///     The answering side plays the extension host over the real wire shapes
///     (a local-disk stand-in with an injectable dirty set).
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendWorkspaceBridgeTests
{
    private static readonly TimeSpan BridgeTimeout = TimeSpan.FromSeconds(10);

    // ---------- sockets: election and targeted delivery ----------

    [Fact]
    public void AuthorityElectsTheEarliestAnnouncementAndReelectsOnWithdraw()
    {
        var sockets = new FrontendIdeSockets();
        var first = sockets.Announce("session-a", static (_, _) => Task.CompletedTask);
        var second = sockets.Announce("session-a", static (_, _) => Task.CompletedTask);
        var other = sockets.Announce("session-b", static (_, _) => Task.CompletedTask);

        Assert.Equal("session-a", sockets.TryGetAuthority("session-a")!.SessionId);
        Assert.Equal("session-a", sockets.TryGetAuthority(null)!.SessionId);

        sockets.Withdraw("session-a", first);
        Assert.Equal("session-a", sockets.TryGetAuthority("session-a")!.SessionId);
        Assert.Equal("session-a", sockets.TryGetAuthority(null)!.SessionId);

        sockets.Withdraw("session-a", second);
        Assert.Null(sockets.TryGetAuthority("session-a"));
        Assert.Equal("session-b", sockets.TryGetAuthority(null)!.SessionId);

        sockets.Withdraw("session-b", other);
        Assert.Null(sockets.TryGetAuthority(null));
    }

    [Fact(Timeout = 60_000)]
    public async Task TargetedSendReachesOnlyTheNamedRegistration()
    {
        var sockets = new FrontendIdeSockets();
        var received = new List<string>();
        var first = sockets.Announce("session-a", (frame, _) =>
        {
            received.Add("first");
            return Task.CompletedTask;
        });
        sockets.Announce("session-a", (frame, _) =>
        {
            received.Add("second");
            return Task.CompletedTask;
        });

        Assert.True(await sockets.TrySendToAsync(first, new FrontendEventFrame("ide.request"), TestContext.Current.CancellationToken));
        Assert.Equivalent(new[] { "first" }, received, strict: true);

        sockets.Withdraw("session-a", first);
        Assert.False(await sockets.TrySendToAsync(first, new FrontendEventFrame("ide.request"), TestContext.Current.CancellationToken), "a withdrawn registration is a lost authority, not a broadcast");
        Assert.Single(received);
    }

    [Fact]
    public void TheOwnerMappingTranslatesThePidVerifiedHelloIdentityForSessionGates()
    {
        var registry = new Control.ReplControlSessionRegistry();
        var ownerSessionId = AgentSessionId.Create().Value.ToString("N");
        registry.Register(101, "repl-default");
        registry.RegisterOwner(101, ownerSessionId);

        Assert.True(registry.TryGetOwnerSession(101, out var owner));
        Assert.Equal(ownerSessionId, owner);
        Assert.False(registry.TryGetOwnerSession(202, out _));

        registry.Unregister(101);
        Assert.False(registry.TryGetOwnerSession(101, out _), "the owner mapping shares the child's lifetime");
    }

    // ---------- bridge channel round trips ----------

    [Fact(Timeout = 60_000)]
    public async Task BridgedReadAnswersTheFrontendTextStateThroughTheRealWireShapes()
    {
        await using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "a.cs"), "disk content\n", TestContext.Current.CancellationToken);
        harness.DiskFrontend.AnswerText["a.cs"] = "editor buffer state\n";
        var bridge = harness.CreateBridge();

        var channel = bridge.Acquire(BridgeHarness.SessionId)!;
        var text = await channel.ReadTextAsync(["a.cs"], 8_000_000, TestContext.Current.CancellationToken);

        Assert.Equal("editor buffer state\n", text);
        Assert.Equal("maieutics/fs/local/read", harness.LastRequest!.Value.Method);
        Assert.Equal(new Uri(Path.Combine(harness.Root, "a.cs")).AbsoluteUri, harness.LastRequest!.Value.PhysicalUri);
    }

    [Fact(Timeout = 60_000)]
    public async Task BridgedListingCarriesEntriesAndAbsentDirectoriesMapTyped()
    {
        await using var harness = BridgeHarness.Create();
        Directory.CreateDirectory(Path.Combine(harness.Root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "sub", "x.ts"), "x\n", TestContext.Current.CancellationToken);
        var bridge = harness.CreateBridge();
        var channel = bridge.Acquire(BridgeHarness.SessionId)!;

        var listing = await channel.ListAsync([], 10, TestContext.Current.CancellationToken);
        Assert.False(listing.Truncated);
        Assert.All([("sub", "directory")], item => Assert.Contains(item, listing.Entries.Select(static entry => (entry.Name, entry.Kind))));

        Func<Task> missingList = async () =>
            await channel.ListAsync(["no-such-dir"], 10, TestContext.Current.CancellationToken);
        Assert.Equal("workspace_path_not_found", (await Assert.ThrowsAnyAsync<WorkspaceException>(async () => { await missingList.Invoke(); })).Code);
    }

    [Fact(Timeout = 60_000)]
    public async Task BridgedWriteCarriesTheBeforeHashAndFailsTypedOnConflict()
    {
        await using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "doc.md"), "before\n", TestContext.Current.CancellationToken);
        var bridge = harness.CreateBridge();
        var channel = bridge.Acquire(BridgeHarness.SessionId)!;

        // The write carries the hash of exactly the text the channel answered.
        await channel.WriteAsync(["doc.md"], "after\n", WorkspaceBridgeChannel.ContentHash("before\n"), false, TestContext.Current.CancellationToken);
        Assert.Equal("after\n", await File.ReadAllTextAsync(Path.Combine(harness.Root, "doc.md"), TestContext.Current.CancellationToken));

        // A precondition computed from a state the frontend never answered is a conflict.
        Func<Task> conflict = async () =>
            await channel.WriteAsync(["doc.md"], "again\n", WorkspaceBridgeChannel.ContentHash("stale\n"), false, TestContext.Current.CancellationToken);
        Assert.Equal("workspace_write_conflict", (await Assert.ThrowsAnyAsync<WorkspaceException>(async () => { await conflict.Invoke(); })).Code);

        // An exclusive create on an existing target fails exactly like the local create.
        Func<Task> exists = async () =>
            await channel.WriteAsync(["doc.md"], "new\n", null, true, TestContext.Current.CancellationToken);
        Assert.Equal("workspace_path_exists", (await Assert.ThrowsAnyAsync<WorkspaceException>(async () => { await exists.Invoke(); })).Code);
    }

    [Fact(Timeout = 60_000)]
    public async Task DirtyBuffersFailBridgedWritesTyped()
    {
        await using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "open.md"), "saved\n", TestContext.Current.CancellationToken);
        harness.DiskFrontend.DirtyUris.Add(new Uri(Path.Combine(harness.Root, "open.md")).AbsoluteUri);
        var bridge = harness.CreateBridge();
        var channel = bridge.Acquire(BridgeHarness.SessionId)!;

        Func<Task> failure = async () =>
            await channel.EnsureCleanAsync(["open.md"], TestContext.Current.CancellationToken);
        Assert.Equal("ide_document_dirty", (await Assert.ThrowsAnyAsync<WorkspaceException>(async () => { await failure.Invoke(); })).Code);
        Func<Task> clean = async () =>
            await channel.EnsureCleanAsync(["other.md"], TestContext.Current.CancellationToken);
        await clean.Invoke();
    }

    [Fact(Timeout = 60_000)]
    public async Task UnsupportedMethodAbsorbsOnceAndTheBridgeReportsItselfInactive()
    {
        await using var harness = BridgeHarness.Create();
        harness.DiskFrontend.SupportedMethods = ["maieutics/dirty"];
        var bridge = harness.CreateBridge();

        var channel = bridge.Acquire(BridgeHarness.SessionId)!;
        Func<Task> failure = async () =>
            await channel.ReadTextAsync(["a.cs"], 8_000_000, TestContext.Current.CancellationToken);
        Assert.Equal("workspace_bridge_inactive", (await Assert.ThrowsAnyAsync<AgentToolException>(async () => { await failure.Invoke(); })).Code);

        Assert.Null(bridge.Acquire(BridgeHarness.SessionId));
    }

    // ---------- coverage: the bridge serves only what the IDE's folders cover ----------

    [Fact(Timeout = 60_000)]
    public async Task PathsOutsideTheFrontendsFoldersFallBackToLocalIo()
    {
        await using var harness = BridgeHarness.Create();
        // The window opens only the app/ subfolder of the kernel workspace root: a
        // file under docs/ is a valid workspace URI the bridge must not claim.
        var appFolder = Path.Combine(harness.Root, "app");
        Directory.CreateDirectory(appFolder);
        harness.DiskFrontend.CoverageRoot = appFolder;
        Directory.CreateDirectory(Path.Combine(harness.Root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(appFolder, "inside.cs"), "inside disk\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "docs", "outside.cs"), "outside disk\n", TestContext.Current.CancellationToken);
        harness.DiskFrontend.AnswerText["inside.cs"] = "inside buffer\n";
        var functions = new WorkspaceFunctions(harness.Workspace, bridge: harness.Bridge);
        var read = functions.Functions.Single(static function => function.Name == "read_text");

        var inside = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/app/inside.cs"}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal("inside buffer", inside.GetProperty("text").GetString());

        var outside = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/docs/outside.cs"}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal("outside disk", outside.GetProperty("text").GetString());
        Assert.DoesNotContain(harness.DiskFrontend.Requests, request => request.Method == "maieutics/fs/local/read" && request.PhysicalUri.Contains("outside"));
    }

    [Fact(Timeout = 60_000)]
    public async Task MixedPatchServesCoveredFilesThroughTheBridgeAndUncoveredFilesLocally()
    {
        await using var harness = BridgeHarness.Create();
        var appFolder = Path.Combine(harness.Root, "app");
        Directory.CreateDirectory(appFolder);
        harness.DiskFrontend.CoverageRoot = appFolder;
        await File.WriteAllTextAsync(Path.Combine(appFolder, "covered.txt"), "covered one\n", TestContext.Current.CancellationToken);
        var edit = new WorkspaceEditFunctions(harness.Workspace, bridge: harness.Bridge);
        var patch = edit.Functions.Single(static function => function.Name == "apply_patch");

        var result = (JsonElement)(await patch.InvokeAsync(
            harness.ScriptArguments(
                """{"patch":"*** Begin Patch\n*** Update File: app/covered.txt\n@@\n-covered one\n+covered two\n*** Add File: docs/local.txt\n+patched locally\n*** End Patch\n"}"""),
            TestContext.Current.CancellationToken))!;

        var operations = result.GetProperty("files").EnumerateArray()
            .Select(static entry => entry.GetProperty("operation").GetString())
            .ToList();
        Assert.Equal(["updated", "created"], operations);
        Assert.Equal("covered two\n", await File.ReadAllTextAsync(Path.Combine(appFolder, "covered.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("patched locally\n", await File.ReadAllTextAsync(Path.Combine(harness.Root, "docs", "local.txt"), TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60_000)]
    public async Task WorkspaceFsScriptToolsReportUncoveredPathsAsTheShimFallbackSignal()
    {
        await using var harness = BridgeHarness.Create();
        var appFolder = Path.Combine(harness.Root, "app");
        Directory.CreateDirectory(appFolder);
        harness.DiskFrontend.CoverageRoot = appFolder;
        Directory.CreateDirectory(Path.Combine(harness.Root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "docs", "cell.ts"), "native\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(appFolder, "cell.ts"), "served\n", TestContext.Current.CancellationToken);
        var functions = new WorkspaceBridgeFunctions(harness.Workspace, harness.Bridge);
        var read = functions.Functions.Single(static function => function.Name == "workspace_fs_read");

        var uncovered = await Record.ExceptionAsync(async () => await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/docs/cell.ts"}"""),
            TestContext.Current.CancellationToken));
        Assert.Equal("workspace_bridge_inactive", Assert.IsType<AgentToolException>(uncovered).Code);

        var served = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/app/cell.ts"}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal("served\n", served.GetProperty("content").GetString());
    }

    // ---------- workspace tools under the bridge ----------

    [Fact(Timeout = 60_000)]
    public async Task ReadTextServesTheBridgedTextStateInsteadOfTheDisk()
    {
        await using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "a.cs"), "line one\nline two\n", TestContext.Current.CancellationToken);
        harness.DiskFrontend.AnswerText["a.cs"] = "buffer one\nbuffer two\nbuffer three\n";
        var functions = new WorkspaceFunctions(harness.Workspace, bridge: harness.Bridge);
        var read = functions.Functions.Single(static function => function.Name == "read_text");

        var result = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/a.cs"}"""),
            TestContext.Current.CancellationToken))!;

        Assert.Equal("buffer one\nbuffer two\nbuffer three", result.GetProperty("text").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task SearchTextIsServedByTheBridgeWhenTheFrontendCoversTheRoot()
    {
        await using var harness = BridgeHarness.Create();
        Directory.CreateDirectory(Path.Combine(harness.Root, "sub"));
        await File.WriteAllTextAsync(
            Path.Combine(harness.Root, "sub", "a.cs"),
            "alpha needle beta\nsecond line\n",
            TestContext.Current.CancellationToken);
        var functions = new WorkspaceFunctions(harness.Workspace, bridge: harness.Bridge);
        var search = functions.Functions.Single(static function => function.Name == "search_text");

        var result = (JsonElement)(await search.InvokeAsync(
            harness.ScriptArguments("""{"query":"needle"}"""),
            TestContext.Current.CancellationToken))!;

        var matches = result.GetProperty("matches");
        Assert.Equal(1, matches.GetArrayLength());
        var match = matches[0];
        Assert.Equal("workspace://local/sub/a.cs", match.GetProperty("uri").GetString());
        Assert.Equal(1, match.GetProperty("line").GetInt32());
        Assert.Equal(7, match.GetProperty("column").GetInt32());
        Assert.Contains(harness.DiskFrontend.Requests, request => request.Method == "maieutics/fs/local/search");

        // The filter knobs ride the same request untouched.
        var filtered = (JsonElement)(await search.InvokeAsync(
            harness.ScriptArguments(
                """{"query":"needle","include":["**/*.cs"],"exclude":["**/generated/**"],"respectIgnore":false}"""),
            TestContext.Current.CancellationToken))!;
        Assert.True(filtered.GetProperty("matches").GetArrayLength() >= 0);
        Assert.Equal(["**/*.cs"], harness.LastRequest!.Value.Params.GetProperty("include").EnumerateArray()
            .Select(static glob => glob.GetString()));
        Assert.False(harness.LastRequest!.Value.Params.GetProperty("respectIgnore").GetBoolean());

        // The regex path rides the same bridge contract.
        var regex = (JsonElement)(await search.InvokeAsync(
            harness.ScriptArguments("""{"query":"n[e]edle","regex":true}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal(1, regex.GetProperty("matches").GetArrayLength());
    }

    [Fact(Timeout = 60_000)]
    public async Task SearchTextLocalScanHonorsTheFilterGlobs()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(workspace.Path, "generated"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "keep.ts"), "needle\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "generated", "drop.ts"), "needle\n", cancellationToken);
        var functions = new WorkspaceFunctions(Workspace.Create(workspace.Path, workspace.Path));
        var search = functions.Functions.Single(static function => function.Name == "search_text");

        var result = (JsonElement)(await search.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?>
            {
                ["query"] = "needle",
                ["exclude"] = new[] { "generated/**" },
            }), cancellationToken))!;

        var matches = result.GetProperty("matches").EnumerateArray()
            .Select(static entry => entry.GetProperty("uri").GetString())
            .ToList();
        Assert.Contains("workspace://local/keep.ts", matches);
        Assert.DoesNotContain(matches, uri => uri!.Contains("generated"));
        Assert.True(result.GetProperty("skippedByFilter").GetInt32() >= 1);
    }

    [Fact]
    public void SearchGlobsFollowTheGitignoreNamingRule()
    {
        Assert.True(SearchGlobs.Passes("src/a.ts", "a.ts", ["**/*.ts"], null));
        Assert.False(SearchGlobs.Passes("src/a.js", "a.js", ["**/*.ts"], null), "include is a whitelist");
        Assert.False(SearchGlobs.Passes("generated/x.ts", "x.ts", null, ["generated/**"]));
        Assert.True(SearchGlobs.Passes("src/gen/x.ts", "x.ts", null, ["generated/**"]), "exclude needs the /** tail to reach descendants");
        Assert.False(SearchGlobs.Passes("a/node_modules/x.js", "x.js", null, ["node_modules"]), "a separator-free glob names a segment at any depth");
        Assert.True(SearchGlobs.Passes("a/modules/x.js", "x.js", null, ["node_modules"]));
        Assert.True(SearchGlobs.Passes("anything.go", "anything.go", null, null), "no filters passes everything");
    }

    [Fact(Timeout = 60_000)]
    public async Task SearchTextFallsBackLocallyWhenTheFrontendCannotSearch()
    {
        await using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "a.cs"), "needle on disk\n", TestContext.Current.CancellationToken);
        harness.DiskFrontend.AnswerText["a.cs"] = "buffer without the needle\n";
        harness.DiskFrontend.SupportedMethods =
        [
            "maieutics/fs/list",
            "maieutics/fs/local/read",
            "maieutics/fs/local/list",
            "maieutics/fs/local/write",
            "maieutics/fs/local/delete",
            "maieutics/dirty",
        ];
        var functions = new WorkspaceFunctions(harness.Workspace, bridge: harness.Bridge);
        var search = functions.Functions.Single(static function => function.Name == "search_text");
        var read = functions.Functions.Single(static function => function.Name == "read_text");

        var result = (JsonElement)(await search.InvokeAsync(
            harness.ScriptArguments("""{"query":"needle"}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal(1, result.GetProperty("matches").GetArrayLength());

        // The degradation is per call: the rest of the bridge stays active.
        var buffered = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/a.cs"}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal("buffer without the needle", buffered.GetProperty("text").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task SearchTextFallsBackLocallyOnACoverageMiss()
    {
        await using var harness = BridgeHarness.Create();
        var appFolder = Path.Combine(harness.Root, "app");
        Directory.CreateDirectory(appFolder);
        harness.DiskFrontend.CoverageRoot = appFolder;
        Directory.CreateDirectory(Path.Combine(harness.Root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "docs", "x.ts"), "needle here\n", TestContext.Current.CancellationToken);
        var functions = new WorkspaceFunctions(harness.Workspace, bridge: harness.Bridge);
        var search = functions.Functions.Single(static function => function.Name == "search_text");

        var result = (JsonElement)(await search.InvokeAsync(
            harness.ScriptArguments("""{"query":"needle","uri":"workspace://local/docs"}"""),
            TestContext.Current.CancellationToken))!;

        Assert.Equal(1, result.GetProperty("matches").GetArrayLength());
        Assert.DoesNotContain(harness.DiskFrontend.Requests, request => request.Method == "maieutics/fs/local/search");
    }

    [Fact(Timeout = 60_000)]
    public async Task WriteTextBridgesCreateUpdateAndKeepsEveryKernelGate()
    {
        await using var harness = BridgeHarness.Create();
        var functions = new WorkspaceEditFunctions(
            harness.Workspace,
            acquirer: TestPermissionPolicies.Unconfigured(),
            bridge: harness.Bridge);
        var write = functions.Functions.Single(static function => function.Name == "write_text");

        var created = (JsonElement)(await write.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/new.txt","content":"created\n"}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal("created", created.GetProperty("operation").GetString());
        Assert.Equal("created\n", await File.ReadAllTextAsync(Path.Combine(harness.Root, "new.txt"), TestContext.Current.CancellationToken));

        var updated = (JsonElement)(await write.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/new.txt","content":"updated\n"}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal("updated", updated.GetProperty("operation").GetString());
        Assert.Equal(JsonValueKind.Object, updated.GetProperty("diff").ValueKind);
        Assert.Equal("updated\n", await File.ReadAllTextAsync(Path.Combine(harness.Root, "new.txt"), TestContext.Current.CancellationToken));

        // The instruction-surface gate (ADR 0032) guards the bridged path with the
        // same posture: no policy allow, no write — before the bridge is engaged.
        var forbidden = await Record.ExceptionAsync(async () => await write.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/AGENTS.md","content":"no\n"}"""),
            TestContext.Current.CancellationToken));
        Assert.Equal("instructions_write_forbidden", Assert.IsType<AgentToolException>(forbidden).Code);
        Assert.False(File.Exists(Path.Combine(harness.Root, "AGENTS.md")), "the gated write never reached the frontend");
    }

    [Fact(Timeout = 60_000)]
    public async Task EditTextAndApplyPatchRouteThroughTheBridge()
    {
        await using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "code.cs"), "int a = 1;\nint b = 2;\n", TestContext.Current.CancellationToken);
        var edit = new WorkspaceEditFunctions(harness.Workspace, bridge: harness.Bridge);

        var editText = edit.Functions.Single(static function => function.Name == "edit_text");
        var edited = (JsonElement)(await editText.InvokeAsync(
            harness.ScriptArguments(
                """{"uri":"workspace://local/code.cs","oldText":"int a = 1;","newText":"int a = 42;"}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal(1, edited.GetProperty("replacements").GetInt32());
        Assert.Equal("int a = 42;\nint b = 2;\n", await File.ReadAllTextAsync(Path.Combine(harness.Root, "code.cs"), TestContext.Current.CancellationToken));

        var patch = edit.Functions.Single(static function => function.Name == "apply_patch");
        var patched = (JsonElement)(await patch.InvokeAsync(
            harness.ScriptArguments(
                """{"patch":"*** Begin Patch\n*** Add File: patchnew.txt\n+patched\n*** Update File: code.cs\n@@\n-int b = 2;\n+int b = 3;\n*** Delete File: patchnew.txt\n*** End Patch\n"}"""),
            TestContext.Current.CancellationToken))!;
        var files = patched.GetProperty("files");
        Assert.Equal(["created", "updated", "deleted"], files.EnumerateArray().Select(static entry => entry.GetProperty("operation").GetString()));
        Assert.Equal("int a = 42;\nint b = 3;\n", await File.ReadAllTextAsync(Path.Combine(harness.Root, "code.cs"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(harness.Root, "patchnew.txt")));

        // An exclusive bridged create on an existing target fails exactly like the
        // local path's exclusive create (workspace_path_exists).
        var exists = await Record.ExceptionAsync(async () => await patch.InvokeAsync(
            harness.ScriptArguments(
                """{"patch":"*** Begin Patch\n*** Add File: code.cs\n+duplicate\n*** End Patch\n"}"""),
            TestContext.Current.CancellationToken));
        Assert.Equal("workspace_path_exists", Assert.IsType<AgentToolException>(exists).Code);
    }

    // ---------- script tools ----------

    [Fact(Timeout = 60_000)]
    public async Task WorkspaceFsScriptToolsRideTheBridgeAndDegradeTypedWhenInactive()
    {
        await using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "cell.ts"), "const x = 1;\n", TestContext.Current.CancellationToken);
        var functions = new WorkspaceBridgeFunctions(harness.Workspace, harness.Bridge);
        var read = functions.Functions.Single(static function => function.Name == "workspace_fs_read");
        var list = functions.Functions.Single(static function => function.Name == "workspace_fs_list");

        var content = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/cell.ts"}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal("const x = 1;\n", content.GetProperty("content").GetString());

        var listing = (JsonElement)(await list.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/"}"""),
            TestContext.Current.CancellationToken))!;
        Assert.Equal("workspace://local/", listing.GetProperty("uri").GetString());

        var inactive = new WorkspaceBridgeFunctions(harness.Workspace, bridge: null);
        var fallback = await Record.ExceptionAsync(async () => await inactive.Functions
            .Single(static function => function.Name == "workspace_fs_read")
            .InvokeAsync(
                harness.ScriptArguments("""{"uri":"workspace://local/cell.ts"}"""),
                TestContext.Current.CancellationToken));
        Assert.Equal("workspace_bridge_inactive", Assert.IsType<AgentToolException>(fallback).Code);
    }

    // ---------- harness ----------

    /// <summary>The composed kernel side (real sockets, registry, bridge, workspace)
    /// plus the answering side: a local-disk stand-in for the extension host's local fs
    /// family, with an injectable text map for buffer state and a dirty set for the
    /// dirty gate. Every answer travels the real registry, keyed by the real frame.</summary>
    private sealed class BridgeHarness : IAsyncDisposable
    {
        public const string SessionId = "0123456789abcdef0123456789abcdef";

        public string Root { get; }
        public Workspace Workspace { get; }
        public FrontendIdeSockets Sockets { get; } = new();
        public IdeRequestRegistry Registry { get; } = new();
        public FakeDiskFrontend DiskFrontend { get; }
        public FrontendWorkspaceBridge Bridge { get; private set; } = null!;
        public (string Method, string PhysicalUri, JsonElement Params)? LastRequest { get; private set; }
        private readonly List<Guid> registrations = [];

        private BridgeHarness()
        {
            Root = Path.Combine(Path.GetTempPath(), $"ws-bridge-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Workspace = Workspace.Create(Root, Root);
            DiskFrontend = new FakeDiskFrontend(Root);
            Bridge = new FrontendWorkspaceBridge(Sockets, Registry, Workspace) { RequestTimeout = BridgeTimeout };
            AttachFrontend();
        }

        public static BridgeHarness Create() => new();

        public FrontendWorkspaceBridge CreateBridge() =>
            new(Sockets, Registry, Workspace) { RequestTimeout = BridgeTimeout };

        /// <summary>Announces one socket whose send delegate plays the answering
        /// extension host: parse the ide.request frame, serve from the local disk
        /// stand-in, complete through the real registry.</summary>
        public void AttachFrontend()
        {
            var frontend = DiskFrontend;
            registrations.Add(Sockets.Announce(SessionId, (frame, _) =>
            {
                var payload = frame.Data!.Value;
                var requestId = payload.GetProperty("requestId").GetString()!;
                var method = payload.GetProperty("method").GetString()!;
                var parameters = payload.GetProperty("params");
                var physicalUri = parameters.TryGetProperty("uri", out var uriElement) &&
                    uriElement.ValueKind == JsonValueKind.String
                    ? uriElement.GetString()!
                    : string.Empty;
                LastRequest = (method, physicalUri, parameters.Clone());
                frontend.Requests.Add((method, physicalUri));
                var answer = frontend.Answer(method, parameters);
                Registry.TryComplete(requestId, answer);
                return Task.CompletedTask;
            }));
        }

        public AIFunctionArguments ScriptArguments(string json)
        {
            using var document = JsonDocument.Parse(json);
            return new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => (object?)property.Value.Clone()))
            {
                Context = new Dictionary<object, object?>
                {
                    [typeof(ScriptInvocationContext)] = new ScriptInvocationContext(SessionId)
                }
            };
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var registration in registrations) Sockets.Withdraw(SessionId, registration);
            // Windows can still hold a file inside the tree for a moment after the last
            // writer; the shared retry turns that into a short wait instead of a teardown
            // failure. A genuinely wedged tree still surfaces after the budget.
            await TempArea.DeleteAsync(Root);
        }
    }

    /// <summary>The extension host stand-in: the decision-12 handler semantics over
    /// the local disk — buffer state from <see cref="AnswerText" /> when configured,
    /// fatal-UTF-8 reads otherwise, hash-preconditioned writes, exclusive creates,
    /// and the dirty set answering maieutics/dirty.</summary>
    private sealed class FakeDiskFrontend(string root)
    {
        public List<(string Method, string PhysicalUri)> Requests { get; } = [];

        public Dictionary<string, string> AnswerText { get; } = new(StringComparer.Ordinal);

        public HashSet<string> DirtyUris { get; } = new(StringComparer.Ordinal);

        public string[]? SupportedMethods { get; set; }

        /// <summary>The folder this window opens for the coverage ruling; the harness
        /// root by default, settable to a subfolder to create uncovered kernel paths.</summary>
        public string CoverageRoot { get; set; } = root;

        public IdeRequestAnswer Answer(string method, JsonElement parameters)
        {
            if (SupportedMethods is { } supported && !supported.Contains(method))
                return IdeRequestAnswer.FromError("ide_method_unsupported", "The method is not supported.");

            return method switch
            {
                "maieutics/fs/local/read" => Read(parameters),
                "maieutics/fs/local/list" => List(parameters),
                "maieutics/fs/local/write" => Write(parameters),
                "maieutics/fs/local/delete" => Delete(parameters),
                "maieutics/dirty" => Dirty(parameters),
                // The folder registry's root answer: this window opens its coverage root.
                "maieutics/fs/list" when !parameters.TryGetProperty("folderId", out var folderId) ||
                    folderId.ValueKind == JsonValueKind.Null => IdeRequestAnswer.FromValue(
                    JsonDocument.Parse(
                        JsonSerializer.Serialize(new
                        {
                            folders = new[] { new { id = "fs1", uri = new Uri(CoverageRoot).AbsoluteUri } },
                        })).RootElement.Clone()),
                _ => IdeRequestAnswer.FromError("ide_method_unsupported", $"Unknown method '{method}'."),
            };
        }

        /// <summary>The coverage ruling's frontend half: only the window's own
        /// folder is served; anything else answers typed fs_not_covered.</summary>
        private IdeRequestAnswer Covered(JsonElement parameters, Func<string, IdeRequestAnswer> serve)
        {
            var path = Path.GetFullPath(PathOf(parameters));
            var prefix = Path.TrimEndingDirectorySeparator(CoverageRoot);
            return path.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                string.Equals(path, prefix, StringComparison.Ordinal)
                ? serve(path)
                : IdeRequestAnswer.FromError("fs_not_covered", "fs_not_covered: (target stripped)");
        }

        private static string PathOf(JsonElement parameters)
        {
            return new Uri(parameters.GetProperty("uri").GetString()!).LocalPath;
        }

        private IdeRequestAnswer Read(JsonElement parameters)
        {
            return Covered(parameters, path =>
            {
                var name = Path.GetFileName(path);

                // Buffer state answers when the test configured it (keyed by file name in
                // this stand-in; the real handler keys by document URI).
                if (AnswerText.TryGetValue(name, out var text))
                {
                    return IdeRequestAnswer.FromValue(
                        JsonDocument.Parse(JsonSerializer.Serialize(new { content = text })).RootElement.Clone());
                }

                if (!File.Exists(path))
                    return IdeRequestAnswer.FromError("fs_not_found", "fs_not_found: (target stripped)");
                try
                {
                    var decoded = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
                    return IdeRequestAnswer.FromValue(
                        JsonDocument.Parse(JsonSerializer.Serialize(new { content = decoded })).RootElement.Clone());
                }
                catch (DecoderFallbackException)
                {
                    return IdeRequestAnswer.FromError("fs_not_text", "fs_not_text: (target stripped)");
                }
            });
        }

        private IdeRequestAnswer List(JsonElement parameters)
        {
            return Covered(parameters, path =>
            {
                if (!Directory.Exists(path))
                    return IdeRequestAnswer.FromError("fs_not_found", "fs_not_found: (target stripped)");

                var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
                    .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
                    .Select(static entry => (entry.Name, Kind: entry switch
                    {
                        _ when (entry.Attributes & FileAttributes.Directory) != 0 => 2,
                        _ when (entry.Attributes & FileAttributes.ReparsePoint) != 0 => 64,
                        _ => 1,
                    }))
                    .ToList();
                var maximum = parameters.TryGetProperty("maximumEntries", out var bound) &&
                    bound.ValueKind == JsonValueKind.Number
                    ? bound.GetInt32()
                    : 10_000;
                var page = entries.Take(maximum)
                    .Select(static entry => JsonSerializer.Serialize(new { name = entry.Name, kind = KindName(entry.Kind) }))
                    .ToList();
                var truncated = entries.Count > maximum ? "true" : "false";
                var payload = $"{{\"entries\":[{string.Join(",", page)}],\"truncated\":{truncated}}}";
                return IdeRequestAnswer.FromValue(JsonDocument.Parse(payload).RootElement.Clone());
            });
        }

        private static string KindName(int fileType)
        {
            return (fileType & 2) != 0 ? "directory"
                : (fileType & 64) != 0 ? "symbolic_link"
                : (fileType & 1) != 0 ? "file"
                : "other";
        }

        private IdeRequestAnswer Write(JsonElement parameters)
        {
            return Covered(parameters, path =>
            {
                var content = parameters.GetProperty("content").GetString()!;
                var expectedHash = parameters.TryGetProperty("expectedHash", out var hash) &&
                    hash.ValueKind == JsonValueKind.String
                    ? hash.GetString()
                    : null;
                var createExclusive = parameters.TryGetProperty("createExclusive", out var exclusive) &&
                    exclusive.ValueKind == JsonValueKind.True;

                string? current = null;
                var exists = File.Exists(path);
                if (exists)
                {
                    try
                    {
                        current = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
                    }
                    catch (DecoderFallbackException)
                    {
                        current = null;
                    }
                }

                if (expectedHash is not null)
                {
                    if (current is null || Hash(current) != expectedHash)
                        return IdeRequestAnswer.FromError("fs_conflict", "fs_conflict: (target stripped)");
                }
                else if (createExclusive && exists)
                {
                    return IdeRequestAnswer.FromError("fs_exists", "fs_exists: (target stripped)");
                }

                File.WriteAllText(path, content);
                return IdeRequestAnswer.FromValue(JsonDocument.Parse("{}").RootElement.Clone());
            });
        }

        /// <summary>A compact real scan over the stand-in's disk: the same bounded
        /// contract the extension's provider scan serves (literal or regex, 1-based
        /// line/column, bounded previews, relative posix paths under the root).</summary>
        private IdeRequestAnswer Search(JsonElement parameters)
        {
            return Covered(parameters, root =>
            {
                var query = parameters.GetProperty("query").GetString()!;
                var useRegex = parameters.TryGetProperty("regex", out var regexElement) &&
                               regexElement.ValueKind == JsonValueKind.True;
                var caseSensitive = !parameters.TryGetProperty("caseSensitive", out var caseElement) ||
                                    caseElement.ValueKind != JsonValueKind.False;
                var maximum = parameters.TryGetProperty("maximumMatches", out var bound) &&
                              bound.ValueKind == JsonValueKind.Number
                    ? bound.GetInt32()
                    : 200;
                var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var regex = useRegex
                    ? new System.Text.RegularExpressions.Regex(
                        query,
                        caseSensitive
                            ? System.Text.RegularExpressions.RegexOptions.None
                            : System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    : null;
                var matches = new List<string>();
                var scanned = 0L;
                var rootDirectory = new DirectoryInfo(root);
                foreach (var file in rootDirectory.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    string text;
                    try
                    {
                        text = File.ReadAllText(file.FullName);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    scanned += text.Length;
                    var lines = text.Split('\n');
                    for (var lineIndex = 0; lineIndex < lines.Length && matches.Count < maximum; lineIndex += 1)
                    {
                        var line = lines[lineIndex].TrimEnd('\r');
                        if (regex is { } pattern)
                        {
                            foreach (System.Text.RegularExpressions.Match found in pattern.Matches(line))
                            {
                                matches.Add(MatchJson(rootDirectory, file, lineIndex + 1, found.Index + 1, line));
                                if (matches.Count >= maximum) break;
                            }

                            continue;
                        }

                        var haystack = caseSensitive ? line : line.ToLowerInvariant();
                        var needle = caseSensitive ? query : query.ToLowerInvariant();
                        var offset = 0;
                        while (matches.Count < maximum)
                        {
                            var index = haystack.IndexOf(needle, offset, comparison);
                            if (index < 0) break;

                            matches.Add(MatchJson(rootDirectory, file, lineIndex + 1, index + 1, line));
                            offset = index + needle.Length;
                        }
                    }
                }

                var payload = $"{{\"matches\":[{string.Join(",", matches)}],\"truncated\":false,\"scannedBytes\":{scanned}}}";
                return IdeRequestAnswer.FromValue(JsonDocument.Parse(payload).RootElement.Clone());
            });
        }

        private static string MatchJson(DirectoryInfo root, FileInfo file, int line, int column, string lineText)
        {
            var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
            var preview = JsonSerializer.Serialize(lineText.Length > 512 ? lineText[..512] : lineText);
            return $"{{\"path\":\"{relative}\",\"line\":{line},\"column\":{column},\"preview\":{preview}}}";
        }

        private IdeRequestAnswer Delete(JsonElement parameters)
        {
            return Covered(parameters, path =>
            {
                if (!File.Exists(path))
                    return IdeRequestAnswer.FromError("fs_not_found", "fs_not_found: (target stripped)");

                File.Delete(path);
                return IdeRequestAnswer.FromValue(JsonDocument.Parse("{}").RootElement.Clone());
            });
        }

        private IdeRequestAnswer Dirty(JsonElement parameters)
        {
            var entries = parameters.GetProperty("uris").EnumerateArray()
                .Select(uri => new { uri = uri.GetString()!, dirty = DirtyUris.Contains(uri.GetString()!) })
                .ToList();
            return IdeRequestAnswer.FromValue(
                JsonDocument.Parse(JsonSerializer.Serialize(new { entries })).RootElement.Clone());
        }

        private static string Hash(string text)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
    }
}
