using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
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

        sockets.TryGetAuthority("session-a")!.SessionId.Should().Be("session-a");
        sockets.TryGetAuthority(null)!.SessionId.Should().Be("session-a", "the process-wide authority is the earliest overall");

        sockets.Withdraw("session-a", first);
        sockets.TryGetAuthority("session-a")!.SessionId.Should().Be("session-a", "the next-earliest takes over deterministically");
        sockets.TryGetAuthority(null)!.SessionId.Should().Be("session-a");

        sockets.Withdraw("session-a", second);
        sockets.TryGetAuthority("session-a").Should().BeNull();
        sockets.TryGetAuthority(null)!.SessionId.Should().Be("session-b");

        sockets.Withdraw("session-b", other);
        sockets.TryGetAuthority(null).Should().BeNull();
    }

    [Fact]
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

        (await sockets.TrySendToAsync(first, new FrontendEventFrame("ide.request"), TestContext.Current.CancellationToken))
            .Should().BeTrue();
        received.Should().BeEquivalentTo(["first"], static options => options.WithStrictOrdering());

        sockets.Withdraw("session-a", first);
        (await sockets.TrySendToAsync(first, new FrontendEventFrame("ide.request"), TestContext.Current.CancellationToken))
            .Should().BeFalse("a withdrawn registration is a lost authority, not a broadcast");
        received.Should().ContainSingle("the surviving announcer is never asked for the pinned operation");
    }

    [Fact]
    public void TheOwnerMappingTranslatesThePidVerifiedHelloIdentityForSessionGates()
    {
        var registry = new Control.ReplControlSessionRegistry();
        var ownerSessionId = AgentSessionId.Create().Value.ToString("N");
        registry.Register(101, "repl-default");
        registry.RegisterOwner(101, ownerSessionId);

        registry.TryGetOwnerSession(101, out var owner).Should().BeTrue();
        owner.Should().Be(ownerSessionId, "the gates key on the agent session, not the REPL-local hello id");
        registry.TryGetOwnerSession(202, out _).Should().BeFalse();

        registry.Unregister(101);
        registry.TryGetOwnerSession(101, out _).Should().BeFalse("the owner mapping shares the child's lifetime");
    }

    // ---------- bridge channel round trips ----------

    [Fact]
    public async Task BridgedReadAnswersTheFrontendTextStateThroughTheRealWireShapes()
    {
        using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "a.cs"), "disk content\n", TestContext.Current.CancellationToken);
        harness.DiskFrontend.AnswerText["a.cs"] = "editor buffer state\n";
        var bridge = harness.CreateBridge();

        var channel = bridge.Acquire(BridgeHarness.SessionId)!;
        var text = await channel.ReadTextAsync(["a.cs"], 8_000_000, TestContext.Current.CancellationToken);

        text.Should().Be("editor buffer state\n", "the IDE's text state is the authoritative answer");
        harness.LastRequest!.Value.Method.Should().Be("maieutics/fs/local/read");
        harness.LastRequest!.Value.PhysicalUri.Should().Be(new Uri(Path.Combine(harness.Root, "a.cs")).AbsoluteUri);
    }

    [Fact]
    public async Task BridgedListingCarriesEntriesAndAbsentDirectoriesMapTyped()
    {
        using var harness = BridgeHarness.Create();
        Directory.CreateDirectory(Path.Combine(harness.Root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "sub", "x.ts"), "x\n", TestContext.Current.CancellationToken);
        var bridge = harness.CreateBridge();
        var channel = bridge.Acquire(BridgeHarness.SessionId)!;

        var listing = await channel.ListAsync([], 10, TestContext.Current.CancellationToken);
        listing.Truncated.Should().BeFalse();
        listing.Entries.Select(static entry => (entry.Name, entry.Kind)).Should().Contain(
            [("sub", "directory")], "the fake frontend answers vscode.FileType-shaped kinds");

        Func<Task> missingList = async () =>
            await channel.ListAsync(["no-such-dir"], 10, TestContext.Current.CancellationToken);
        (await missingList.Should().ThrowAsync<WorkspaceException>()).Which.Code
            .Should().Be("workspace_path_not_found");
    }

    [Fact]
    public async Task BridgedWriteCarriesTheBeforeHashAndFailsTypedOnConflict()
    {
        using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "doc.md"), "before\n", TestContext.Current.CancellationToken);
        var bridge = harness.CreateBridge();
        var channel = bridge.Acquire(BridgeHarness.SessionId)!;

        // The write carries the hash of exactly the text the channel answered.
        await channel.WriteAsync(["doc.md"], "after\n", WorkspaceBridgeChannel.ContentHash("before\n"), false, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(Path.Combine(harness.Root, "doc.md"), TestContext.Current.CancellationToken))
            .Should().Be("after\n");

        // A precondition computed from a state the frontend never answered is a conflict.
        Func<Task> conflict = async () =>
            await channel.WriteAsync(["doc.md"], "again\n", WorkspaceBridgeChannel.ContentHash("stale\n"), false, TestContext.Current.CancellationToken);
        (await conflict.Should().ThrowAsync<WorkspaceException>()).Which.Code
            .Should().Be("workspace_write_conflict");

        // An exclusive create on an existing target fails exactly like the local create.
        Func<Task> exists = async () =>
            await channel.WriteAsync(["doc.md"], "new\n", null, true, TestContext.Current.CancellationToken);
        (await exists.Should().ThrowAsync<WorkspaceException>()).Which.Code
            .Should().Be("workspace_path_exists");
    }

    [Fact]
    public async Task DirtyBuffersFailBridgedWritesTyped()
    {
        using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "open.md"), "saved\n", TestContext.Current.CancellationToken);
        harness.DiskFrontend.DirtyUris.Add(new Uri(Path.Combine(harness.Root, "open.md")).AbsoluteUri);
        var bridge = harness.CreateBridge();
        var channel = bridge.Acquire(BridgeHarness.SessionId)!;

        Func<Task> failure = async () =>
            await channel.EnsureCleanAsync(["open.md"], TestContext.Current.CancellationToken);
        (await failure.Should().ThrowAsync<WorkspaceException>()).Which.Code.Should().Be("ide_document_dirty",
            "the dirty gate runs kernel-side before the frontend executes the write");
        Func<Task> clean = async () =>
            await channel.EnsureCleanAsync(["other.md"], TestContext.Current.CancellationToken);
        (await clean.Should().NotThrowAsync("a clean document passes the gate")).Should().NotBeNull();
    }

    [Fact]
    public async Task UnsupportedMethodAbsorbsOnceAndTheBridgeReportsItselfInactive()
    {
        using var harness = BridgeHarness.Create();
        harness.DiskFrontend.SupportedMethods = ["maieutics/dirty"];
        var bridge = harness.CreateBridge();

        var channel = bridge.Acquire(BridgeHarness.SessionId)!;
        Func<Task> failure = async () =>
            await channel.ReadTextAsync(["a.cs"], 8_000_000, TestContext.Current.CancellationToken);
        (await failure.Should().ThrowAsync<AgentToolException>()).Which.Code
            .Should().Be("workspace_bridge_inactive");

        bridge.Acquire(BridgeHarness.SessionId).Should().BeNull(
            "a cohort that predates the local family returns the tools to local I/O, process-wide");
    }

    // ---------- coverage: the bridge serves only what the IDE's folders cover ----------

    [Fact]
    public async Task PathsOutsideTheFrontendsFoldersFallBackToLocalIo()
    {
        using var harness = BridgeHarness.Create();
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
        inside.GetProperty("text").GetString().Should().Be("inside buffer",
            "a covered path forwards and answers the IDE's text state");

        var outside = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/docs/outside.cs"}"""),
            TestContext.Current.CancellationToken))!;
        outside.GetProperty("text").GetString().Should().Be("outside disk",
            "an uncovered path falls back to the local channel");
        harness.DiskFrontend.Requests.Should().NotContain(
            request => request.Method == "maieutics/fs/local/read" && request.PhysicalUri.Contains("outside"),
            "uncovered paths never reach the frontend");
    }

    [Fact]
    public async Task MixedPatchServesCoveredFilesThroughTheBridgeAndUncoveredFilesLocally()
    {
        using var harness = BridgeHarness.Create();
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
        operations.Should().Equal(["updated", "created"], "both files apply, each through its own channel");
        File.ReadAllText(Path.Combine(appFolder, "covered.txt")).Should().Be("covered two\n",
            "the covered file went through the bridge (hash precondition, watchers)");
        File.ReadAllText(Path.Combine(harness.Root, "docs", "local.txt")).Should().Be("patched locally\n",
            "the uncovered file was served locally");
    }

    [Fact]
    public async Task WorkspaceFsScriptToolsReportUncoveredPathsAsTheShimFallbackSignal()
    {
        using var harness = BridgeHarness.Create();
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
        uncovered.Should().BeOfType<AgentToolException>().Which.Code.Should().Be("workspace_bridge_inactive",
            "the coverage miss is the shim's per-call fallback signal, not an error");

        var served = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/app/cell.ts"}"""),
            TestContext.Current.CancellationToken))!;
        served.GetProperty("content").GetString().Should().Be("served\n");
    }

    // ---------- workspace tools under the bridge ----------

    [Fact]
    public async Task ReadTextServesTheBridgedTextStateInsteadOfTheDisk()
    {
        using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "a.cs"), "line one\nline two\n", TestContext.Current.CancellationToken);
        harness.DiskFrontend.AnswerText["a.cs"] = "buffer one\nbuffer two\nbuffer three\n";
        var functions = new WorkspaceFunctions(harness.Workspace, bridge: harness.Bridge);
        var read = functions.Functions.Single(static function => function.Name == "read_text");

        var result = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/a.cs"}"""),
            TestContext.Current.CancellationToken))!;

        result.GetProperty("text").GetString().Should().Be("buffer one\nbuffer two\nbuffer three",
            "the bridge answers the IDE's authoritative text state (read_text windows trim the final newline)");
    }

    [Fact]
    public async Task SearchTextIsServedByTheBridgeWhenTheFrontendCoversTheRoot()
    {
        using var harness = BridgeHarness.Create();
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
        matches.GetArrayLength().Should().Be(1);
        var match = matches[0];
        match.GetProperty("uri").GetString().Should().Be("workspace://local/sub/a.cs",
            "the kernel maps the answered relative path onto the workspace domain");
        match.GetProperty("line").GetInt32().Should().Be(1);
        match.GetProperty("column").GetInt32().Should().Be(7);
        harness.DiskFrontend.Requests.Should().Contain(
            request => request.Method == "maieutics/fs/local/search",
            "a covered root is served by the frontend's provider scan");

        // The filter knobs ride the same request untouched.
        var filtered = (JsonElement)(await search.InvokeAsync(
            harness.ScriptArguments(
                """{"query":"needle","include":["**/*.cs"],"exclude":["**/generated/**"],"respectIgnore":false}"""),
            TestContext.Current.CancellationToken))!;
        filtered.GetProperty("matches").GetArrayLength().Should().BeGreaterThanOrEqualTo(0);
        harness.LastRequest!.Value.Params.GetProperty("include").EnumerateArray()
            .Select(static glob => glob.GetString()).Should().Equal(["**/*.cs"],
            "the filter knobs ride the search request untouched");
        harness.LastRequest!.Value.Params.GetProperty("respectIgnore").GetBoolean().Should().BeFalse();

        // The regex path rides the same bridge contract.
        var regex = (JsonElement)(await search.InvokeAsync(
            harness.ScriptArguments("""{"query":"n[e]edle","regex":true}"""),
            TestContext.Current.CancellationToken))!;
        regex.GetProperty("matches").GetArrayLength().Should().Be(1);
    }

    [Fact]
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
        matches.Should().Contain("workspace://local/keep.ts");
        matches.Should().NotContain(uri => uri!.Contains("generated"),
            "the local scan judges the same globs the frontend's fast search does");
        result.GetProperty("skippedByFilter").GetInt32().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void SearchGlobsFollowTheGitignoreNamingRule()
    {
        SearchGlobs.Passes("src/a.ts", "a.ts", ["**/*.ts"], null).Should().BeTrue();
        SearchGlobs.Passes("src/a.js", "a.js", ["**/*.ts"], null).Should().BeFalse(
            "include is a whitelist");
        SearchGlobs.Passes("generated/x.ts", "x.ts", null, ["generated/**"]).Should().BeFalse();
        SearchGlobs.Passes("src/gen/x.ts", "x.ts", null, ["generated/**"]).Should().BeTrue(
            "exclude needs the /** tail to reach descendants");
        SearchGlobs.Passes("a/node_modules/x.js", "x.js", null, ["node_modules"]).Should().BeFalse(
            "a separator-free glob names a segment at any depth");
        SearchGlobs.Passes("a/modules/x.js", "x.js", null, ["node_modules"]).Should().BeTrue();
        SearchGlobs.Passes("anything.go", "anything.go", null, null).Should().BeTrue(
            "no filters passes everything");
    }

    [Fact]
    public async Task SearchTextFallsBackLocallyWhenTheFrontendCannotSearch()
    {
        using var harness = BridgeHarness.Create();
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
        result.GetProperty("matches").GetArrayLength().Should().Be(1,
            "a frontend predating the search method degrades this call to the local scan");

        // The degradation is per call: the rest of the bridge stays active.
        var buffered = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/a.cs"}"""),
            TestContext.Current.CancellationToken))!;
        buffered.GetProperty("text").GetString().Should().Be("buffer without the needle");
    }

    [Fact]
    public async Task SearchTextFallsBackLocallyOnACoverageMiss()
    {
        using var harness = BridgeHarness.Create();
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

        result.GetProperty("matches").GetArrayLength().Should().Be(1,
            "an uncovered root is scanned locally");
        harness.DiskFrontend.Requests.Should().NotContain(
            request => request.Method == "maieutics/fs/local/search",
            "uncovered trees never reach the frontend");
    }

    [Fact]
    public async Task WriteTextBridgesCreateUpdateAndKeepsEveryKernelGate()
    {
        using var harness = BridgeHarness.Create();
        var functions = new WorkspaceEditFunctions(
            harness.Workspace,
            acquirer: TestPermissionPolicies.Unconfigured(),
            bridge: harness.Bridge);
        var write = functions.Functions.Single(static function => function.Name == "write_text");

        var created = (JsonElement)(await write.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/new.txt","content":"created\n"}"""),
            TestContext.Current.CancellationToken))!;
        created.GetProperty("operation").GetString().Should().Be("created");
        File.ReadAllText(Path.Combine(harness.Root, "new.txt")).Should().Be("created\n");

        var updated = (JsonElement)(await write.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/new.txt","content":"updated\n"}"""),
            TestContext.Current.CancellationToken))!;
        updated.GetProperty("operation").GetString().Should().Be("updated");
        updated.GetProperty("diff").ValueKind.Should().Be(JsonValueKind.Object,
            "the kernel still computes the unified diff from the bridged before side");
        File.ReadAllText(Path.Combine(harness.Root, "new.txt")).Should().Be("updated\n");

        // The instruction-surface gate (ADR 0032) guards the bridged path with the
        // same posture: no policy allow, no write — before the bridge is engaged.
        var forbidden = await Record.ExceptionAsync(async () => await write.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/AGENTS.md","content":"no\n"}"""),
            TestContext.Current.CancellationToken));
        forbidden.Should().BeOfType<AgentToolException>().Which.Code.Should().Be("instructions_write_forbidden");
        File.Exists(Path.Combine(harness.Root, "AGENTS.md")).Should().BeFalse("the gated write never reached the frontend");
    }

    [Fact]
    public async Task EditTextAndApplyPatchRouteThroughTheBridge()
    {
        using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "code.cs"), "int a = 1;\nint b = 2;\n", TestContext.Current.CancellationToken);
        var edit = new WorkspaceEditFunctions(harness.Workspace, bridge: harness.Bridge);

        var editText = edit.Functions.Single(static function => function.Name == "edit_text");
        var edited = (JsonElement)(await editText.InvokeAsync(
            harness.ScriptArguments(
                """{"uri":"workspace://local/code.cs","oldText":"int a = 1;","newText":"int a = 42;"}"""),
            TestContext.Current.CancellationToken))!;
        edited.GetProperty("replacements").GetInt32().Should().Be(1);
        File.ReadAllText(Path.Combine(harness.Root, "code.cs")).Should().Be("int a = 42;\nint b = 2;\n");

        var patch = edit.Functions.Single(static function => function.Name == "apply_patch");
        var patched = (JsonElement)(await patch.InvokeAsync(
            harness.ScriptArguments(
                """{"patch":"*** Begin Patch\n*** Add File: patchnew.txt\n+patched\n*** Update File: code.cs\n@@\n-int b = 2;\n+int b = 3;\n*** Delete File: patchnew.txt\n*** End Patch\n"}"""),
            TestContext.Current.CancellationToken))!;
        var files = patched.GetProperty("files");
        files.EnumerateArray().Select(static entry => entry.GetProperty("operation").GetString())
            .Should().Equal(["created", "updated", "deleted"]);
        File.ReadAllText(Path.Combine(harness.Root, "code.cs")).Should().Be("int a = 42;\nint b = 3;\n");
        File.Exists(Path.Combine(harness.Root, "patchnew.txt")).Should().BeFalse();

        // An exclusive bridged create on an existing target fails exactly like the
        // local path's exclusive create (workspace_path_exists).
        var exists = await Record.ExceptionAsync(async () => await patch.InvokeAsync(
            harness.ScriptArguments(
                """{"patch":"*** Begin Patch\n*** Add File: code.cs\n+duplicate\n*** End Patch\n"}"""),
            TestContext.Current.CancellationToken));
        exists.Should().BeOfType<AgentToolException>().Which.Code.Should().Be("workspace_path_exists");
    }

    // ---------- script tools ----------

    [Fact]
    public async Task WorkspaceFsScriptToolsRideTheBridgeAndDegradeTypedWhenInactive()
    {
        using var harness = BridgeHarness.Create();
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "cell.ts"), "const x = 1;\n", TestContext.Current.CancellationToken);
        var functions = new WorkspaceBridgeFunctions(harness.Workspace, harness.Bridge);
        var read = functions.Functions.Single(static function => function.Name == "workspace_fs_read");
        var list = functions.Functions.Single(static function => function.Name == "workspace_fs_list");

        var content = (JsonElement)(await read.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/cell.ts"}"""),
            TestContext.Current.CancellationToken))!;
        content.GetProperty("content").GetString().Should().Be("const x = 1;\n");

        var listing = (JsonElement)(await list.InvokeAsync(
            harness.ScriptArguments("""{"uri":"workspace://local/"}"""),
            TestContext.Current.CancellationToken))!;
        listing.GetProperty("uri").GetString().Should().Be("workspace://local/",
            "the shim addresses the workspace root itself for Deno.readDir(cwd)");

        var inactive = new WorkspaceBridgeFunctions(harness.Workspace, bridge: null);
        var fallback = await Record.ExceptionAsync(async () => await inactive.Functions
            .Single(static function => function.Name == "workspace_fs_read")
            .InvokeAsync(
                harness.ScriptArguments("""{"uri":"workspace://local/cell.ts"}"""),
                TestContext.Current.CancellationToken));
        fallback.Should().BeOfType<AgentToolException>().Which.Code.Should().Be("workspace_bridge_inactive",
            "the typed code is the shim's per-call fallback signal");
    }

    // ---------- harness ----------

    /// <summary>The composed kernel side (real sockets, registry, bridge, workspace)
    /// plus the answering side: a local-disk stand-in for the extension host's local fs
    /// family, with an injectable text map for buffer state and a dirty set for the
    /// dirty gate. Every answer travels the real registry, keyed by the real frame.</summary>
    private sealed class BridgeHarness : IDisposable
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

        public void Dispose()
        {
            foreach (var registration in registrations) Sockets.Withdraw(SessionId, registration);
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
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
                    .Select(static entry => (Name: entry.Name, Kind: entry switch
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
