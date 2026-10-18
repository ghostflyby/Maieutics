using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Execution;
using Maieutics.Frontend;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>
///     The IDE capability bridge (ADR 0042 stage A): tool-level round trips through the
///     real registry and workspace translation, the answer endpoint's contract, and the
///     per-socket capability announcement that gates the family.
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendIdeBridgeTests(ITestOutputHelper output)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(500);

    [Fact(Timeout = 30_000)]
    public async Task HoverAnnouncesIdeRequestAndTranslatesTheAnswerIntoAgentConventions()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        var invocation = await InvokeWithAnswerAsync(
            hover,
            """{"uri":"workspace://local/a.cs","select":{"line":41,"column":7}}""",
            registry,
            static _ => IdeRequestAnswer.FromValue(
                JsonDocument.Parse(
                    """{"contents":{"kind":"markdown","value":"class C"},"range":{"start":{"line":40,"character":6},"end":{"line":40,"character":7}}}"""
                ).RootElement.Clone()),
            cancellationToken);

        // Outgoing: the payload announced on the run stream is the wrapped ide.request
        // envelope, carries the physical file address, and the LSP 0-based position
        // (the kernel is the translation point).
        Assert.Equal("textDocument/hover", invocation.Method);
        var outgoing = JsonDocument.Parse(invocation.PayloadJson).RootElement.GetProperty("ideRequest");
        Assert.StartsWith("ide-", outgoing.GetProperty("requestId").GetString() ?? throw new InvalidOperationException("the assertion subject is null"));
        Assert.Equal(new Uri(Path.Combine(workspace.Path, "a.cs")).AbsoluteUri, outgoing.GetProperty("params").GetProperty("uri").GetString());
        Assert.Equal(40, outgoing.GetProperty("params").GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(6, outgoing.GetProperty("params").GetProperty("position").GetProperty("character").GetInt32());

        // Incoming: results map back to 1-based positions; hover contents pass through.
        var result = invocation.Result;
        Assert.Equal("markdown", result.GetProperty("contents").GetProperty("kind").GetString());
        Assert.Equal(41, result.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(7, result.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
    }

    [Fact(Timeout = 30_000)]
    public async Task NavigateTranslatesFileUrisInTheResultBackToTheWorkspaceDomain()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var navigate = Function(functions.Functions, "ide_navigate");
        var physical = new Uri(Path.Combine(workspace.Path, "b.cs")).AbsoluteUri;
        var definitionAnswer = JsonDocument.Parse(
            "[{\"uri\":\"" + physical + "\"," +
            "\"range\":{\"start\":{\"line\":9,\"character\":0},\"end\":{\"line\":9,\"character\":8}}}]"
        ).RootElement.Clone();

        var invocation = await InvokeWithAnswerAsync(
            navigate,
            """{"kind":"definition","uri":"workspace://local/a.cs","select":{"line":1}}""",
            registry,
            _ => IdeRequestAnswer.FromValue(definitionAnswer),
            cancellationToken);

        Assert.Equal("textDocument/definition", invocation.Method);
        var location = invocation.Result[0];
        Assert.Equal("workspace://local/b.cs", location.GetProperty("uri").GetString());
        Assert.Equal(10, location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact(Timeout = 30_000)]
    public async Task UnansweredRequestFailsTypedTimeoutAndRepeatedAnswersRenderAs404()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path, TestTimeout);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        Func<Task> timed = async () => await hover.InvokeAsync(
            ContextArguments(
                """{"uri":"workspace://local/a.cs","select":{"line":1}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken).AsTask();
        Assert.Equal("ide_timeout", (await Assert.ThrowsAnyAsync<AgentToolException>(async () => { await timed.Invoke(); })).Code);

        // The answer endpoint is the only completion path, so the registry's idempotence
        // is the 404 contract: one answer completes, any repeat draws the typed 404 the
        // frontend absorbs.
        var registration = registry.Begin();
        Assert.True(registry.TryComplete(registration.RequestId, IdeRequestAnswer.FromValue(JsonDocument.Parse("1").RootElement.Clone())));
        Assert.False(registry.TryComplete(registration.RequestId, IdeRequestAnswer.FromValue(JsonDocument.Parse("2").RootElement.Clone())), "a second answer for the same id is the endpoint's typed 404");
    }

    [Fact(Timeout = 30_000)]
    public async Task CancelledToolCallAbandonsTheRequestImmediately()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var announced = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = hover.InvokeAsync(
            ContextArguments(
                """{"uri":"workspace://local/a.cs","select":{"line":1}}""",
                (content, _) =>
                {
                    announced.TrySetResult(Encoding.UTF8.GetString(Assert.IsType<DataContent>(content).Data.Span));
                    return ValueTask.CompletedTask;
                }),
            cancelled.Token).AsTask();
        var payload = JsonDocument.Parse(
            await announced.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(true));
        var requestId = payload.RootElement.GetProperty("ideRequest").GetProperty("requestId").GetString()!;

        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await invocation; });
        Assert.False(registry.TryComplete(requestId, IdeRequestAnswer.FromValue(JsonDocument.Parse("1").RootElement.Clone())), "the cancelled tool call left no resumable request");
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolsRefuseWithoutAnAnnouncingFrontendOnTheSession()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, _, _, _) = CreateFunctions(workspace.Path);
        var symbols = Function(functions.Functions, "ide_document_symbols");

        var invocation = await InvokeAsync(
            symbols,
            ContextArguments(
                "{\"uri\":\"workspace://local/a.cs\"}",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);

        Assert.NotNull(invocation.Failure);
        Assert.Equal("ide_unavailable", invocation.Failure!.Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task EscapeHatchRejectsWriteMethodsWithoutARoundTripAndPassesBareNumbersVerbatim()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, _, gate, _) = CreateFunctions(workspace.Path, TestTimeout);
        gate.Connected = true;
        var escape = Function(functions.Functions, "ide_request");

        var rejected = await InvokeAsync(
            escape,
            ContextArguments(
                """{"method":"textDocument/rename","parameters":{"kind":"rename"}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);
        Assert.NotNull(rejected.Failure);
        Assert.Equal("ide_method_unsupported", rejected.Failure!.Code);

        var announced = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = escape.InvokeAsync(
            ContextArguments(
                """{"method":"textDocument/foldingRange","parameters":{"uri":"workspace://local/a.cs","startLine":3}}""",
                (content, _) =>
                {
                    announced.TrySetResult(Encoding.UTF8.GetString(Assert.IsType<DataContent>(content).Data.Span));
                    return ValueTask.CompletedTask;
                }),
            cancellationToken).AsTask();
        var payload = JsonDocument.Parse(
            await announced.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(true));
        var envelope = payload.RootElement.GetProperty("ideRequest");
        Assert.Equal("textDocument/foldingRange", envelope.GetProperty("method").GetString());
        Assert.Equal(new Uri(Path.Combine(workspace.Path, "a.cs")).AbsoluteUri, envelope.GetProperty("params").GetProperty("uri").GetString());
        Assert.Equal(3, envelope.GetProperty("params").GetProperty("startLine").GetInt32());
        Assert.Equal("ide_timeout", (await Assert.ThrowsAnyAsync<AgentToolException>(async () => { await invocation; })).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task DiagnosticsAggregateCountsSeveritiesCountsUnmappedAndMarksUnanalyzedState()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var diagnostics = Function(functions.Functions, "ide_diagnostics");
        var physical = new Uri(Path.Combine(workspace.Path, "a.cs")).AbsoluteUri;
        var mixedAnswer = JsonDocument.Parse(
            "{\"documents\":[" +
            "{\"uri\":\"" + physical + "\",\"diagnostics\":[" +
            "{\"severity\":0,\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"message\":\"e\",\"source\":\"csc\"}," +
            "{\"severity\":3,\"range\":{\"start\":{\"line\":1,\"character\":0},\"end\":{\"line\":1,\"character\":1}},\"message\":\"h\"}]}" +
            ",{\"uri\":\"file:///outside/b.ts\",\"diagnostics\":[" +
            "{\"severity\":0,\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"message\":\"leak\"}]}" +
            "],\"analyzedDocuments\":7}").RootElement.Clone();

        var analyzed = await InvokeWithAnswerAsync(
            diagnostics,
            "{}",
            registry,
            _ => IdeRequestAnswer.FromValue(mixedAnswer),
            cancellationToken);
        Assert.Equal("maieutics/problems", analyzed.Method);
        var aggregate = JsonSerializer.Deserialize(
            analyzed.Result,
            IdeFunctionJsonContext.Default.WorkspaceDiagnosticsValue)!;
        Assert.Equal(1, aggregate.Totals.Error);
        Assert.Equal(1, aggregate.Totals.Hint);
        Assert.Equal("workspace://local/a.cs", Assert.Single(aggregate.Files).Uri);
        Assert.Equal(1, aggregate.UnmappedFiles);
        Assert.Equal(7, aggregate.AnalyzedDocuments);
        Assert.Null(aggregate.Note);

        var unanalyzed = await InvokeWithAnswerAsync(
            diagnostics,
            "{}",
            registry,
            static _ => IdeRequestAnswer.FromValue(
                JsonDocument.Parse("""{"documents":[],"analyzedDocuments":0}""").RootElement.Clone()),
            cancellationToken);
        var empty = JsonSerializer.Deserialize(
            unanalyzed.Result,
            IdeFunctionJsonContext.Default.WorkspaceDiagnosticsValue)!;
        Assert.Equal(0, empty.Totals.Error);
        Assert.NotNull(empty.Note);

        var filtered = await InvokeWithAnswerAsync(
            diagnostics,
            """{"minSeverity":"error"}""",
            registry,
            _ => IdeRequestAnswer.FromValue(
                JsonDocument.Parse(
                    "{\"documents\":[" +
                    "{\"uri\":\"" + physical + "\",\"diagnostics\":[" +
                    "{\"severity\":0,\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"message\":\"e\"}," +
                    "{\"severity\":1,\"range\":{\"start\":{\"line\":1,\"character\":0},\"end\":{\"line\":1,\"character\":1}},\"message\":\"w\"}]}" +
                    "],\"analyzedDocuments\":1}").RootElement.Clone()),
            cancellationToken);
        var filteredValue = JsonSerializer.Deserialize(
            filtered.Result,
            IdeFunctionJsonContext.Default.WorkspaceDiagnosticsValue)!;
        Assert.Equal(0, filteredValue.Totals.Warning);
        Assert.Equal(1, filteredValue.Totals.Error);
    }

    [Fact(Timeout = 30_000)]
    public async Task SelectorsRejectNonCoordinateFormsAndBadPositions()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, _, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        var ranged = await InvokeAsync(
            hover,
            ContextArguments(
                """{"uri":"workspace://local/a.cs","select":{"line":1,"endLine":2}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);
        Assert.NotNull(ranged.Failure);
        Assert.Equal("ide_selector_unsupported", ranged.Failure!.Code);

        var zero = await InvokeAsync(
            hover,
            ContextArguments(
                """{"uri":"workspace://local/a.cs","select":{"line":0}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);
        Assert.NotNull(zero.Failure);
        Assert.Equal("ide_invalid_arguments", zero.Failure!.Code);
    }

    [Fact(Timeout = 30_000)]
    public void PhysicalPathsMapBackOnlyInsideTheWorkspace()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workspace = TemporaryWorkspace.Create();
        var snapshot = Workspace.Create(workspace.Path, workspace.Path).Capture();
        var root = Path.TrimEndingDirectorySeparator(workspace.Path);

        Assert.True(snapshot.TryMapPhysicalToWorkspaceUri(root, out var rootUri));
        Assert.Equal("workspace://local/", rootUri);
        Assert.True(snapshot.TryMapPhysicalToWorkspaceUri(Path.Combine(root, "a", "b.cs"), out var nested));
        Assert.Equal("workspace://local/a/b.cs", nested);
        Assert.True(snapshot.TryMapPhysicalToWorkspaceUri(Path.Combine(root, "a b", "c d.cs"), out var spaces));
        Assert.Equal("workspace://local/a%20b/c%20d.cs", spaces);
        Assert.False(snapshot.TryMapPhysicalToWorkspaceUri(Path.GetTempPath(), out _));
        Assert.False(snapshot.TryMapPhysicalToWorkspaceUri(root + "-sibling/file.cs", out _), "a path sharing the root's prefix but not its containment is outside");
    }

    // ---------- stage B: anchor and patch selectors ----------

    [Fact(Timeout = 30_000)]
    public async Task AnchorSelectorResolvesTheUniqueMatchToItsBlockStart()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(
            Path.Combine(workspace.Path, "a.cs"),
            "alpha\nbeta\ngamma\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        var invocation = await InvokeWithAnswerAsync(
            hover,
            """{"uri":"workspace://local/a.cs","select":{"anchor":["beta","gamma"]}}""",
            registry,
            static _ => IdeRequestAnswer.FromValue(JsonDocument.Parse("\"h\"").RootElement.Clone()),
            cancellationToken);

        var outgoing = JsonDocument.Parse(invocation.PayloadJson).RootElement;
        Assert.Equal(1, outgoing.GetProperty("ideRequest").GetProperty("params").GetProperty("position").GetProperty("line").GetInt32());
    }

    [Fact(Timeout = 30_000)]
    public async Task AmbiguousAnchorWithoutOccurrenceIsTypedWithCandidates()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(
            Path.Combine(workspace.Path, "a.cs"),
            "dup\nkeep\ndup\nkeep\n");
        var (functions, _, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        var invocation = await InvokeAsync(
            hover,
            ContextArguments(
                """{"uri":"workspace://local/a.cs","select":{"anchor":["dup"]}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);

        Assert.NotNull(invocation.Failure);
        Assert.Equal("ide_selector_ambiguous", invocation.Failure!.Code);
        Assert.Contains("line 1", invocation.Failure.Message);
        Assert.Contains("line 3", invocation.Failure.Message);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnchorOccurrencePicksTheNthMatch()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "dup\nkeep\ndup\nkeep\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        var invocation = await InvokeWithAnswerAsync(
            hover,
            """{"uri":"workspace://local/a.cs","select":{"anchor":["dup"],"occurrence":2}}""",
            registry,
            static _ => IdeRequestAnswer.FromValue(JsonDocument.Parse("\"h\"").RootElement.Clone()),
            cancellationToken);

        var outgoing = JsonDocument.Parse(invocation.PayloadJson).RootElement;
        Assert.Equal(2, outgoing.GetProperty("ideRequest").GetProperty("params").GetProperty("position").GetProperty("line").GetInt32());
    }

    [Fact(Timeout = 30_000)]
    public async Task AnchorWithoutMatchIsTyped()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "alpha\n");
        var (functions, _, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        var invocation = await InvokeAsync(
            hover,
            ContextArguments(
                """{"uri":"workspace://local/a.cs","select":{"anchor":["nope"]}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);

        Assert.NotNull(invocation.Failure);
        Assert.Equal("ide_selector_no_match", invocation.Failure!.Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task V4aPatchSelectorLocatesThroughSectionContext()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "start\nmiddle\nend\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");
        const string patch = "*** Begin Patch\n*** Update File: a.cs\n@@\n-middle\n+middle updated\n*** End Patch";

        var invocation = await InvokeWithAnswerAsync(
            hover,
            "{\"uri\":\"workspace://local/a.cs\",\"select\":{\"patch\":" + JsonSerializer.Serialize(patch) + "}}",
            registry,
            static _ => IdeRequestAnswer.FromValue(JsonDocument.Parse("\"h\"").RootElement.Clone()),
            cancellationToken);

        var outgoing = JsonDocument.Parse(invocation.PayloadJson).RootElement;
        Assert.Equal(1, outgoing.GetProperty("ideRequest").GetProperty("params").GetProperty("position").GetProperty("line").GetInt32());
    }

    [Fact(Timeout = 30_000)]
    public async Task GitPatchSelectorLocatesThroughHunkContext()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "start\nmiddle\nend\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");
        const string patch = "--- a/a.cs\n+++ b/a.cs\n@@ -1,3 +1,3 @@\n start\n-middle\n+middle updated\n end";

        var invocation = await InvokeWithAnswerAsync(
            hover,
            "{\"uri\":\"workspace://local/a.cs\",\"select\":{\"patch\":" + JsonSerializer.Serialize(patch) + "}}",
            registry,
            static _ => IdeRequestAnswer.FromValue(JsonDocument.Parse("\"h\"").RootElement.Clone()),
            cancellationToken);

        var outgoing = JsonDocument.Parse(invocation.PayloadJson).RootElement;
        Assert.Equal(0, outgoing.GetProperty("ideRequest").GetProperty("params").GetProperty("position").GetProperty("line").GetInt32());
    }

    [Fact(Timeout = 30_000)]
    public async Task PatchSelectorRejectsAPathMismatchAsTyped()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "start\n");
        var (functions, _, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");
        const string patch = "--- a/other.cs\n+++ b/other.cs\n@@ -1 +1 @@\n-x\n+y";

        var invocation = await InvokeAsync(
            hover,
            ContextArguments(
                "{\"uri\":\"workspace://local/a.cs\",\"select\":{\"patch\":" + JsonSerializer.Serialize(patch) + "}}",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);

        Assert.NotNull(invocation.Failure);
        Assert.Equal("ide_selector_path_mismatch", invocation.Failure!.Code);
        Assert.Contains("other.cs", invocation.Failure.Message);
    }

    [Fact(Timeout = 30_000)]
    public async Task MalformedPatchIsTyped()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "start\n");
        var (functions, _, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        var invocation = await InvokeAsync(
            hover,
            ContextArguments(
                """{"uri":"workspace://local/a.cs","select":{"patch":"not a diff"}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);

        Assert.NotNull(invocation.Failure);
        Assert.Equal("ide_selector_patch_malformed", invocation.Failure!.Code);
    }

    // ---------- stage C: the write path ----------

    /// <summary>Answers each announced request by its method — the write
    /// tools make several bridge round trips in one call (dirty query, then
    /// the compute request), so the test scripts answers per method.</summary>
    private static async Task<IdeInvocation> InvokeAnsweringByMethodAsync(
        AIFunction function,
        string argumentsJson,
        IdeRequestRegistry registry,
        Func<string, JsonElement, IdeRequestAnswer> answerByMethod,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<string> announcedPayload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastPayloadJson = string.Empty;
        var lastMethod = string.Empty;
        var invocation = function.InvokeAsync(
            ContextArguments(
                argumentsJson,
                (content, _) =>
                {
                    announcedPayload.TrySetResult(Encoding.UTF8.GetString(Assert.IsType<DataContent>(content).Data.Span));
                    return ValueTask.CompletedTask;
                }),
            cancellationToken).AsTask();

        // Write tools make several bridge round trips in one call (the dirty
        // query, then the compute request), so keep answering announcements
        // until the invocation settles. A tool that fails before announcing
        // surfaces its typed failure here instead of a bare timeout.
        while (true)
        {
            var settled = await Task.WhenAny(
                announcedPayload.Task,
                invocation).ConfigureAwait(true);
            if (settled == invocation) break;

            var payloadJson = announcedPayload.Task.Result;
            var payload = JsonDocument.Parse(payloadJson).RootElement;
            var ideRequest = payload.GetProperty("ideRequest");
            var method = ideRequest.GetProperty("method").GetString()!;
            var requestId = ideRequest.GetProperty("requestId").GetString()!;
            if (!registry.TryComplete(requestId, answerByMethod(method, ideRequest.GetProperty("params").Clone())))
                throw new InvalidOperationException($"The announced ide request ({method}) was not pending.");
            lastPayloadJson = payloadJson;
            lastMethod = method;
            announcedPayload = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var result = await invocation.ConfigureAwait(true);
        return new IdeInvocation(lastMethod, lastPayloadJson, Assert.IsType<JsonElement>(result));
    }

    [Fact(Timeout = 30_000)]
    public async Task ApplyEditAppliesMultiFileEditsThroughTheGatedPath()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(workspace.Path, "b.cs"), "x\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var apply = Function(functions.Functions, "ide_apply_edit");
        var aPhysical = new Uri(DenoRealPath(Path.Combine(workspace.Path, "a.cs"))).AbsoluteUri;
        var bPhysical = new Uri(Path.Combine(workspace.Path, "b.cs")).AbsoluteUri;

        const string edit = """{"changes":{"workspace://local/a.cs":[{"range":{"start":{"line":1,"character":1},"end":{"line":1,"character":4}},"newText":"ONE"}],"workspace://local/b.cs":[{"range":{"start":{"line":1,"character":2},"end":{"line":1,"character":2}},"newText":"y"}]}}""";

        var invocation = await InvokeAnsweringByMethodAsync(
            apply,
            "{\"edit\":" + edit + "}",
            registry,
            (method, parameters) =>
            {
                Assert.Equal("maieutics/dirty", method);
                var uris = parameters.GetProperty("uris").EnumerateArray()
                    .Select(static entry => entry.GetString()).ToList();
                Assert.Contains(aPhysical, uris);
                Assert.Contains(bPhysical, uris);
                return IdeRequestAnswer.FromValue(JsonDocument.Parse(
                    """{"entries":[{"uri":"a","dirty":false},{"uri":"b","dirty":false}]}""").RootElement.Clone());
            },
            cancellationToken);

        Assert.Equal("ONE\ntwo\n", File.ReadAllText(Path.Combine(workspace.Path, "a.cs")));
        Assert.Equal("xy\n", File.ReadAllText(Path.Combine(workspace.Path, "b.cs")));
        var envelope = JsonSerializer.Deserialize(invocation.Result, IdeFunctionJsonContext.Default.IdeApplyEditsValue)!;
        Assert.Equal("applied", envelope.Status);
        Assert.Equal(2, envelope.Applied.Count());
        Assert.Contains(envelope.Applied, static entry => entry.Diff != null);
    }

    [Fact(Timeout = 30_000)]
    public async Task ApplyEditRejectsTheWholeEditWhenAnyTargetIsDirty()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "one\n");
        File.WriteAllText(Path.Combine(workspace.Path, "b.cs"), "x\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var apply = Function(functions.Functions, "ide_apply_edit");

        const string edit = """{"changes":{"workspace://local/a.cs":[{"range":{"start":{"line":1,"character":1},"end":{"line":1,"character":4}},"newText":"ONE"}],"workspace://local/b.cs":[{"range":{"start":{"line":1,"character":1},"end":{"line":1,"character":2}},"newText":"y"}]}}""";

        AgentToolException? failure = null;
        try
        {
            await InvokeAnsweringByMethodAsync(
                apply,
                "{\"edit\":" + edit + "}",
                registry,
                (method, parameters) =>
                {
                    Assert.Equal("maieutics/dirty", method);
                    var first = parameters.GetProperty("uris").EnumerateArray().First().GetString()!;
                    return IdeRequestAnswer.FromValue(JsonDocument.Parse(
                        "{\"entries\":[{\"uri\":\"" + first + "\",\"dirty\":true}]}").RootElement.Clone());
                },
                cancellationToken).ConfigureAwait(true);
        }
        catch (AgentToolException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.Equal("ide_document_dirty", failure.Code);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(workspace.Path, "a.cs")));
        Assert.Equal("x\n", File.ReadAllText(Path.Combine(workspace.Path, "b.cs")));
    }

    [Fact(Timeout = 30_000)]
    public async Task ApplyEditRejectsOutOfRangeRangesBeforeAnyMutation()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "one\n");
        File.WriteAllText(Path.Combine(workspace.Path, "b.cs"), "x\n");
        var (functions, _, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var apply = Function(functions.Functions, "ide_apply_edit");

        const string edit = """{"changes":{"workspace://local/a.cs":[{"range":{"start":{"line":99,"character":1},"end":{"line":99,"character":2}},"newText":"nope"}],"workspace://local/b.cs":[{"range":{"start":{"line":1,"character":1},"end":{"line":1,"character":2}},"newText":"y"}]}}""";

        var invocation = await InvokeAsync(
            apply,
            ContextArguments("{\"edit\":" + edit + "}", static (_, _) => ValueTask.CompletedTask),
            cancellationToken);

        Assert.NotNull(invocation.Failure);
        Assert.Equal("ide_edit_rejected", invocation.Failure!.Code);
        Assert.Contains("a.cs", invocation.Failure.Message);
        Assert.Contains("99", invocation.Failure.Message);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(workspace.Path, "a.cs")));
        Assert.Equal("x\n", File.ReadAllText(Path.Combine(workspace.Path, "b.cs")));
    }

    [Fact(Timeout = 30_000)]
    public async Task FormatDocumentComputesAndAppliesThroughTheGatedPath()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "  x\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var format = Function(functions.Functions, "ide_format_document");
        var aPhysical = new Uri(DenoRealPath(Path.Combine(workspace.Path, "a.cs"))).AbsoluteUri;

        var invocation = await InvokeAnsweringByMethodAsync(
            format,
            """{"uri":"workspace://local/a.cs"}""",
            registry,
            (method, parameters) =>
            {
                if (method == "maieutics/dirty")
                {
                    return IdeRequestAnswer.FromValue(JsonDocument.Parse(
                        """{"entries":[{"uri":"a","dirty":false}]}""").RootElement.Clone());
                }

                Assert.Equal("textDocument/formatting", method);
                Assert.Equal(aPhysical, parameters.GetProperty("uri").GetString());
                Assert.Equal(4, parameters.GetProperty("options").GetProperty("tabSize").GetInt32());
                return IdeRequestAnswer.FromValue(JsonDocument.Parse(
                    """[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"newText":"x"}]""").RootElement.Clone());
            },
            cancellationToken);

        Assert.Equal("x\n", File.ReadAllText(Path.Combine(workspace.Path, "a.cs")));
        var envelope = JsonSerializer.Deserialize(invocation.Result, IdeFunctionJsonContext.Default.IdeApplyEditsValue)!;
        Assert.Equal("applied", envelope.Status);
        Assert.NotNull(Assert.Single(envelope.Applied).Diff);
    }

    [Fact(Timeout = 30_000)]
    public async Task CodeActionsReturnDataWithoutApplying()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "x\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var actions = Function(functions.Functions, "ide_code_actions");
        var aPhysical = new Uri(DenoRealPath(Path.Combine(workspace.Path, "a.cs"))).AbsoluteUri;

        var invocation = await InvokeAnsweringByMethodAsync(
            actions,
            """{"uri":"workspace://local/a.cs","select":{"line":1,"column":1}}""",
            registry,
            (method, _) =>
            {
                if (method == "maieutics/dirty")
                {
                    return IdeRequestAnswer.FromValue(JsonDocument.Parse("{\"entries\":[]}").RootElement.Clone());
                }

                var answer = "[{\"title\":\"Fix\",\"kind\":\"quickfix\",\"edit\":{\"changes\":{\"" +
                    aPhysical + "\": [{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"newText\":\"y\"}]}}}]" +
                    "";
                return IdeRequestAnswer.FromValue(JsonDocument.Parse(answer).RootElement.Clone());
            },
            cancellationToken);

        Assert.Equal("textDocument/codeAction", invocation.Method);
        Assert.Equal("x\n", File.ReadAllText(Path.Combine(workspace.Path, "a.cs")));
        var action = Assert.Single(invocation.Result.EnumerateArray());
        var edit = Assert.Single(action.GetProperty("edit").GetProperty("changes").EnumerateObject());
        Assert.Equal("workspace://local/a.cs", edit.Name);
        Assert.Equal("y", Assert.Single(edit.Value.EnumerateArray()).GetProperty("newText").GetString());
        var range = edit.Value.EnumerateArray().First().GetProperty("range");
        Assert.Equal(1, range.GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact(Timeout = 30_000)]
    public async Task RenameReturnsDataOnlyThroughTheBridge()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "symbol\n");
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var rename = Function(functions.Functions, "ide_rename");
        var aPhysical = new Uri(DenoRealPath(Path.Combine(workspace.Path, "a.cs"))).AbsoluteUri;

        var invocation = await InvokeAnsweringByMethodAsync(
            rename,
            """{"uri":"workspace://local/a.cs","select":{"line":1,"column":1},"newName":"renamed"}""",
            registry,
            (method, parameters) =>
            {
                if (method == "maieutics/dirty")
                {
                    return IdeRequestAnswer.FromValue(JsonDocument.Parse("{\"entries\":[]}").RootElement.Clone());
                }

                Assert.Equal("renamed", parameters.GetProperty("newName").GetString());
                var answer = "{\"changes\":{\"" + aPhysical +
                    "\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":6}},\"newText\":\"renamed\"}]}}";
                return IdeRequestAnswer.FromValue(JsonDocument.Parse(answer).RootElement.Clone());
            },
            cancellationToken);

        Assert.Equal("textDocument/rename", invocation.Method);
        Assert.Equal("symbol\n", File.ReadAllText(Path.Combine(workspace.Path, "a.cs")));
        var changes = invocation.Result.GetProperty("changes");
        Assert.Equal("workspace://local/a.cs", Assert.Single(changes.EnumerateObject()).Name);
    }

    // ---------- stage D: the script/plugin channel (direct push) ----------

    /// <summary>Script-style arguments: no agent tool context in the bag —
    /// exactly what the control-plane invocation path hands the tools.</summary>
    private static AIFunctionArguments NoContextArguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));
    }

    [Fact(Timeout = 30_000)]
    public async Task ScriptChainPushesAStandaloneFrameAndCompletesWithoutAgentContext()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "x\n");
        var (functions, registry, gate, sockets) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        var frames = new List<FrontendEventFrame>();
        var registration = sockets.Announce(
            "script-session",
            (frame, _) =>
            {
                frames.Add(frame);
                var data = frame.Data!.Value;
                var requestId = data.GetProperty("requestId").GetString()!;
                registry.TryComplete(
                    requestId,
                    IdeRequestAnswer.FromValue(JsonDocument.Parse(
                        "[{\"contents\":{\"kind\":\"markdown\",\"value\":\"x\"},\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}}}]"
                    ).RootElement.Clone()));
                return Task.CompletedTask;
            });
        try
        {
            var invocation = await InvokeAsync(
                hover,
                NoContextArguments("""{"uri":"workspace://local/a.cs","select":{"line":1,"column":1}}"""),
                cancellationToken);

            Assert.Null(invocation.Failure);
            var result = Assert.IsType<JsonElement>(invocation.Result);
            Assert.Equal(1, result.GetArrayLength());
            var hover0 = result[0];
            Assert.Equal("markdown", hover0.GetProperty("contents").GetProperty("kind").GetString());
            var frame = Assert.Single(frames);
            Assert.Equal("ide.request", frame.Type);
            Assert.NotEmpty(frame.Data!.Value.GetProperty("requestId").GetString() ?? throw new InvalidOperationException("the assertion subject is null"));
            Assert.Equal("textDocument/hover", frame.Data!.Value.GetProperty("method").GetString());
            Assert.Null(frame.Sequence);
        }
        finally
        {
            sockets.Withdraw("script-session", registration);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ScriptChainWithoutAnnouncersFailsTypedUnavailable()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "x\n");
        var (functions, _, gate, _) = CreateFunctions(workspace.Path, TimeSpan.FromMilliseconds(500));
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        var invocation = await InvokeAsync(
            hover,
            NoContextArguments("""{"uri":"workspace://local/a.cs","select":{"line":1,"column":1}}"""),
            cancellationToken);

        Assert.NotNull(invocation.Failure);
        Assert.Equal("ide_unavailable", invocation.Failure!.Code);
    }

    // ---------- stage E: virtual file systems ----------

    [Fact(Timeout = 30_000)]
    public async Task FsListAndFsReadRoundTripThroughTheDirectChannel()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "remote.ts"), "alpha\nbeta\n");
        var (functions, registry, gate, sockets) = CreateFunctions(workspace.Path);
        gate.Connected = true;

        var seenFolderIds = new List<string>();
        var registration = sockets.Announce(
            "script-session",
            (frame, _) =>
            {
                var data = frame.Data!.Value;
                var method = data.GetProperty("method").GetString()!;
                var requestId = data.GetProperty("requestId").GetString()!;
                IdeRequestAnswer answer;
                if (method == "maieutics/fs/list")
                {
                    var parameters = data.GetProperty("params");
                    var folderId = parameters.GetProperty("folderId").GetString() ?? "";
                    seenFolderIds.Add(folderId);
                    var content = parameters.GetProperty("segments").GetArrayLength() == 0
                        ? "{\"folders\":[{\"id\":\"ab1\",\"uri\":\"file:///repo\"}]}"
                        : "{\"entries\":[{\"name\":\"remote.ts\",\"isDirectory\":false}]}";
                    answer = IdeRequestAnswer.FromValue(JsonDocument.Parse(content).RootElement.Clone());
                }
                else if (method == "maieutics/fs/read")
                {
                    seenFolderIds.Add(
                        data.GetProperty("params").GetProperty("folderId").GetString() ?? "");
                    answer = IdeRequestAnswer.FromValue(JsonDocument.Parse(
                        "{\"content\":\"alpha\\nbeta\\n\",\"totalLines\":2}").RootElement.Clone());
                }
                else
                {
                    answer = IdeRequestAnswer.FromError("unexpected", method);
                }

                registry.TryComplete(requestId, answer);
                return Task.CompletedTask;
            });
        try
        {
            var list = Function(functions.Functions, "ide_fs_list");
            var roots = await InvokeAsync(
                list,
                NoContextArguments("{}"),
                cancellationToken);
            Assert.Null(roots.Failure);
            var rootValue = (JsonElement)roots.Result!;
            Assert.Equal("ab1", rootValue.GetProperty("folders")[0].GetProperty("id").GetString());
            Assert.StartsWith("file://", rootValue.GetProperty("folders")[0].GetProperty("uri").GetString() ?? throw new InvalidOperationException("the assertion subject is null"));

            var read = Function(functions.Functions, "ide_fs_read");
            var invocation = await InvokeAsync(
                read,
                NoContextArguments("""{"uri":"idefs:///ab1/remote.ts","startLine":1,"maxLines":1}"""),
                cancellationToken);

            Assert.Null(invocation.Failure);
            var value = (JsonElement)invocation.Result!;
            Assert.Equal("idefs:///ab1/remote.ts", value.GetProperty("uri").GetString());
            Assert.Equal("alpha", value.GetProperty("content").GetString());
            Assert.Equal(2, value.GetProperty("nextStartLine").GetInt32());
            Assert.Contains("ab1", seenFolderIds);
        }
        finally
        {
            sockets.Withdraw("script-session", registration);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task IdeFsRejectsForeignIdsAndTraversalBeforeTheBridge()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (functions, _, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var read = Function(functions.Functions, "ide_fs_read");

        var foreign = await InvokeAsync(
            read,
            NoContextArguments("{\"uri\":\"idefs:///ZZ9!/remote.ts\"}"),
            cancellationToken);
        Assert.NotNull(foreign.Failure);
        Assert.Equal("ide_invalid_arguments", foreign.Failure!.Code);

        // A shape-valid id cannot be verified locally (resolution is the
        // frontend's duty), so it fails typed unavailable once the call
        // reaches the empty announcer set.
        var shapeValid = await InvokeAsync(
            read,
            NoContextArguments("{\"uri\":\"idefs:///zz9nope/remote.ts\"}"),
            cancellationToken);
        Assert.NotNull(shapeValid.Failure);
        Assert.Equal("ide_unavailable", shapeValid.Failure!.Code);

        var traversal = await InvokeAsync(
            read,
            NoContextArguments("{\"uri\":\"idefs:///ab1/../secret\"}"),
            cancellationToken);
        Assert.NotNull(traversal.Failure);
        Assert.Equal("ide_invalid_arguments", traversal.Failure!.Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task IdeFsSearchMapsRelativePathsOntoTheIdefsDomain()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (functions, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var search = Function(functions.Functions, "ide_fs_search");

        var invocation = await InvokeWithAnswerAsync(
            search,
            """{"uri":"idefs:///aaa1111/1/src","query":"needle","regex":true}""",
            registry,
            static _ => IdeRequestAnswer.FromValue(JsonDocument.Parse(
                """{"matches":[{"path":"a.ts","line":2,"column":3,"preview":"the needle here"}],"truncated":true,"scannedBytes":42}""").RootElement.Clone()),
            cancellationToken);

        Assert.Equal("maieutics/fs/search", invocation.Method);
        var outgoing = JsonDocument.Parse(invocation.PayloadJson).RootElement.GetProperty("ideRequest");
        Assert.Equal("aaa1111", outgoing.GetProperty("params").GetProperty("folderId").GetString());
        Assert.Equal(["1", "src"], outgoing.GetProperty("params").GetProperty("segments").EnumerateArray()
            .Select(static segment => segment.GetString()));
        Assert.True(outgoing.GetProperty("params").GetProperty("regex").GetBoolean());
        Assert.Equal(200, outgoing.GetProperty("params").GetProperty("maximumMatches").GetInt32());

        var matches = invocation.Result.GetProperty("matches");
        Assert.Equal(1, matches.GetArrayLength());
        var match = matches[0];
        Assert.Equal("idefs:///aaa1111/1/src/a.ts", match.GetProperty("uri").GetString());
        Assert.Equal(2, match.GetProperty("line").GetInt32());
        Assert.Equal(3, match.GetProperty("column").GetInt32());
        Assert.True(invocation.Result.GetProperty("truncated").GetBoolean());
        Assert.Equal(42, invocation.Result.GetProperty("scannedBytes").GetInt64());
    }

    [Fact(Timeout = 30_000)]
    public async Task IdeFsSearchRejectsBadArgumentsWithoutARoundTrip()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (functions, _, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var search = Function(functions.Functions, "ide_fs_search");

        var empty = await InvokeAsync(
            search,
            NoContextArguments("""{"uri":"idefs:///aaa1111","query":""}"""),
            cancellationToken);
        Assert.NotNull(empty.Failure);
        Assert.Equal("ide_invalid_arguments", empty.Failure!.Code);

        var over = await InvokeAsync(
            search,
            NoContextArguments("""{"uri":"idefs:///aaa1111","query":"x","regex":true,"maxResults":201}"""),
            cancellationToken);
        Assert.NotNull(over.Failure);
        Assert.Equal("ide_invalid_arguments", over.Failure!.Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task IdeDebugStartRequiresExactlyOneOfNameOrConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, _, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var debugRegistry = new IdeRequestRegistry();
        var debugSockets = new FrontendIdeSockets();
        var debugFunctions = new IdeDebugTools(
            debugRegistry,
            gate,
            debugSockets,
            Workspace.Create(workspace.Path, workspace.Path));
        var start = debugFunctions.Functions.Single(static function => function.Name == "ide_debug_start");

        const string bothKinds = """{"launchName":"probe","configuration":{"type":"node","request":"launch"}}""";

        var neither = await InvokeAsync(
            start,
            NoContextArguments("{}"),
            cancellationToken);
        Assert.NotNull(neither.Failure);
        Assert.Equal("ide_invalid_arguments", neither.Failure!.Code);

        var both = await InvokeAsync(
            start,
            NoContextArguments(bothKinds),
            cancellationToken);
        Assert.NotNull(both.Failure);
        Assert.Equal("ide_invalid_arguments", both.Failure!.Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task IdeDebugStartRidesTheBridgeWithTheLaunchNameAndOverride()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var debugSockets = new FrontendIdeSockets();
        var debugFunctions = new IdeDebugTools(
            registry,
            gate,
            debugSockets,
            Workspace.Create(workspace.Path, workspace.Path));
        var start = debugFunctions.Functions.Single(static function => function.Name == "ide_debug_start");
        debugSockets.Announce("probe", (frame, _) => Task.CompletedTask);

        var invocation = await InvokeWithAnswerAsync(
            start,
            """{"launchName":"probe","stopOnEntry":true}""",
            registry,
            static _ => IdeRequestAnswer.FromValue(JsonDocument.Parse(
                """{"sessionId":"dbg-1","state":"running"}""").RootElement.Clone()),
            cancellationToken);

        Assert.Equal("maieutics/debug/start", invocation.Method);
        var outgoing = JsonDocument.Parse(invocation.PayloadJson).RootElement.GetProperty("ideRequest");
        Assert.Equal("probe", outgoing.GetProperty("params").GetProperty("launchName").GetString());
        Assert.True(outgoing.GetProperty("params").GetProperty("stopOnEntry").GetBoolean());
        Assert.Equal("dbg-1", invocation.Result.GetProperty("sessionId").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task IdeDebugStateClampsWaitAndIdeDebugControlValidatesAction()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, registry, gate, _) = CreateFunctions(workspace.Path, TestTimeout);
        gate.Connected = true;
        var debugSockets = new FrontendIdeSockets();
        var debugFunctions = new IdeDebugTools(
            registry,
            gate,
            debugSockets,
            Workspace.Create(workspace.Path, workspace.Path));
        var state = debugFunctions.Functions.Single(static function => function.Name == "ide_debug_state");
        debugSockets.Announce("probe", (frame, _) => Task.CompletedTask);
        var control = debugFunctions.Functions.Single(static function => function.Name == "ide_debug_control");

        var invocation = await InvokeWithAnswerAsync(
            state,
            """{"sessionId":"dbg-1","waitSeconds":999}""",
            registry,
            static _ => IdeRequestAnswer.FromValue(JsonDocument.Parse(
                """{"sessionId":"dbg-1","version":3,"state":"paused","reason":"breakpoint","threadId":5}""").RootElement.Clone()),
            cancellationToken);
        Assert.Equal("paused", invocation.Result.GetProperty("state").GetString());
        Assert.Equal(5, invocation.Result.GetProperty("threadId").GetInt32());

        var badAction = await InvokeAsync(
            control,
            NoContextArguments("""{"sessionId":"dbg-1","action":"detonate"}"""),
            cancellationToken);
        Assert.NotNull(badAction.Failure);
        Assert.Equal("ide_invalid_arguments", badAction.Failure!.Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task IdeDebugVariablesRequiresATarget()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, _, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var debugRegistry = new IdeRequestRegistry();
        var debugSockets = new FrontendIdeSockets();
        var debugFunctions = new IdeDebugTools(
            debugRegistry,
            gate,
            debugSockets,
            Workspace.Create(workspace.Path, workspace.Path));
        var variables = debugFunctions.Functions.Single(static function => function.Name == "ide_debug_variables");

        var failure = await InvokeAsync(
            variables,
            NoContextArguments("""{"sessionId":"dbg-1"}"""),
            cancellationToken);
        Assert.NotNull(failure.Failure);
        Assert.Equal("ide_invalid_arguments", failure.Failure!.Code);
    }

    /// <summary>The composed-host integration the debug tools need: the
    /// IdeToolFamily's concat must actually put the debug tools into a run
    /// profile (a broken registration would leave every unit round trip green
    /// while the model never sees the tools), and the frame/answer loop must
    /// carry the debug methods like any other family member.</summary>
    [Fact(Timeout = 120_000)]
    public async Task RunLoopServesIdeDebugToolsThroughTheBridge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            toolFlow: true,
            toolName: "ide_debug_configurations",
            toolArgumentsJson: "{}",
            expectedToolResultText: "probe launch");
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            cancellationToken,
            provider,
            hanging: false);

        var sessionId = await harness.GetSessionIdAsync(cancellationToken);
        await using var events = await harness.OpenEventsAsync(sessionId, cancellationToken, announceIde: true);
        await WaitForAsync(() => harness.IdeGate.IsIdeFrontendConnected(sessionId), cancellationToken);

        var runId = await harness.SubmitTurnAsync(sessionId, "list launch configs", cancellationToken);

        // A run-context call announces inside a sequenced tool.progress content
        // item (decision 1) — not the standalone frame the context-less path uses.
        using var collectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        collectDeadline.CancelAfter(TimeSpan.FromSeconds(45));
        var seen = new List<string>();
        string? requestId = null;
        try
        {
            while (requestId is null)
            {
                var frame = await events.ReceiveFrameAsync(collectDeadline.Token).ConfigureAwait(true);
                var type = frame.GetProperty("type").GetString();
                string? described = type;
                if (frame.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("value", out var value) &&
                    value.TryGetProperty("ideRequest", out var request))
                {
                    described = $"{type}:{request.GetProperty("method").GetString()}";
                    if (request.GetProperty("method").GetString() == "maieutics/debug/configurations")
                    {
                        requestId = request.GetProperty("requestId").GetString();
                    }
                }

                seen.Add(described ?? type ?? "?");
                if (type == "run.failed") {
                    output.WriteLine("run.failed frame: " + frame.GetRawText());
                }
            }
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("debug e2e frames seen: " + string.Join(" | ", seen.TakeLast(40)));
            throw;
        }

        output.WriteLine("debug e2e matched the configurations request");
        var answered = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{requestId}",
            new { value = new { configs = new[] { new { name = "probe launch", type = "node", request = "launch" } } } },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        await harness.WaitForTurnCommittedAsync(sessionId, cancellationToken, runId);
        var trailing = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            cancellationToken);
        Assert.Contains(trailing, frame => frame.GetProperty("type").GetString() == "tool.finished");
    }

    [Fact(Timeout = 30_000)]
    public async Task IdeDebugBreakpointsResolveSelectorsKernelSide()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(workspace.Path, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "src", "app.ts"),
            "const a = 1;\nconst b = 2;\nconst c = 3;\n",
            cancellationToken);
        var (_, registry, gate, _) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var debugFunctions = new IdeDebugTools(
            registry,
            gate,
            new FrontendIdeSockets(),
            Workspace.Create(workspace.Path, workspace.Path));
        var breakpoints = debugFunctions.Functions.Single(static function => function.Name == "ide_debug_breakpoints");

        const string patch = "*** Begin Patch\n*** Update File: src/app.ts\n@@\n-const b = 2;\n+const b = 20;\n*** End Patch\n";
        var pointsJson = JsonSerializer.Serialize(new
        {
            sessionId = "dbg-1",
            uri = "workspace://local/src/app.ts",
            points = new object[]
            {
                new { line = 1 },
                new { anchor = new[] { "const b = 2;" } },
                new { patch },
            },
        });
        var invocation = await InvokeWithAnswerAsync(
            breakpoints,
            pointsJson,
            registry,
            static _ => IdeRequestAnswer.FromValue(JsonDocument.Parse(
                """{"breakpoints":[{"line":0},{"line":1},{"line":1}]}""").RootElement.Clone()),
            cancellationToken);

        Assert.Equal("maieutics/debug/breakpoints", invocation.Method);
        var outgoing = JsonDocument.Parse(invocation.PayloadJson).RootElement.GetProperty("ideRequest");
        Assert.Equal([1, 2, 2], outgoing.GetProperty("params").GetProperty("lines").EnumerateArray()
            .Select(static line => line.GetInt32()));

        // Anchors failing resolution fail typed at the true site, before
        // anything crosses to the frontend.
        var missing = await InvokeAsync(
            breakpoints,
            NoContextArguments(
                """{"sessionId":"dbg-1","uri":"workspace://local/src/app.ts","points":[{"anchor":["no such line"]}]}"""),
            cancellationToken);
        Assert.NotNull(missing.Failure);
        Assert.Equal("ide_selector_no_match", missing.Failure!.Code);
    }

    // ---------- endpoint + announcement (harness) ----------







    [Fact(Timeout = 120_000)]
    public async Task AnswerEndpointCompletesPendingRequestsAndTypesEveryRejection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            cancellationToken,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);

        var unknown = await harness.Client.PostAsJsonAsync("/v1/agent/ide/ide-none", new { value = 1 }, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var registration = harness.IdeRequests.Begin();
        var both = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{registration.RequestId}",
            new { value = 1, error = new { code = "x", message = "y" } },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        var neither = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{registration.RequestId}",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);

        var answered = harness.IdeRequests.Begin();
        var post = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{answered.RequestId}",
            new { value = new { hover = "class C" } },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var answer = await answered.Completion;
        Assert.NotNull(answer.Value);
        Assert.Equal("class C", answer.Value!.Value.GetProperty("hover").GetString());

        var repeat = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{answered.RequestId}",
            new { value = 1 },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, repeat.StatusCode);

        var failing = harness.IdeRequests.Begin();
        var errorPost = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{failing.RequestId}",
            new { error = new { code = "no_language_service", message = "not ready" } },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, errorPost.StatusCode);
        var errorAnswer = await failing.Completion;
        Assert.Equal("no_language_service", errorAnswer.ErrorCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task EventsIdeAnnouncementGatesTheFamilyPerSessionAndLapsesOnDisconnect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            cancellationToken,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);
        var sessionId = await harness.GetSessionIdAsync(cancellationToken);
        var gate = harness.IdeGate;
        Assert.False(gate.HasIdeFrontend, "no socket announced yet");

        var connection = await harness.OpenEventsAsync(sessionId, cancellationToken, announceIde: true);
        try
        {
            await WaitForAsync(() => gate.IsIdeFrontendConnected(sessionId), cancellationToken);
            Assert.True(gate.HasIdeFrontend);
            Assert.Contains("ide_diagnostics", harness.RuntimeConfiguration.ReservedToolNames);
        }
        finally
        {
            await connection.DisposeAsync();
        }

        await WaitForAsync(() => !gate.IsIdeFrontendConnected(sessionId), cancellationToken);
        Assert.False(gate.HasIdeFrontend, "the announcement lapses with the socket");

        // A socket without the parameter never gates the family.
        var plain = await harness.OpenEventsAsync(sessionId, cancellationToken);
        try
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(true);
            Assert.False(gate.IsIdeFrontendConnected(sessionId));
        }
        finally
        {
            await plain.DisposeAsync();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task RunLoopAnnouncesIdeRequestAsJsonProgressAndTheEndpointAnswerCompletesTheTurn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            toolFlow: true,
            toolName: "ide_hover",
            toolArgumentsJson: """{"uri":"workspace://local/a.cs","select":{"line":1,"column":1}}""",
            expectedToolResultText: "class C");
        // Pin the composed workspace root to the throwaway directory so the model's
        // workspace://local/a.cs call resolves against a file this test owns.
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            cancellationToken,
            provider,
            hanging: false,
        transformConfiguration: body => body.Replace(
            "\"Maieutics\": {",
            "\"Maieutics\": {\n    \"Workspace\": { \"Root\": " +
            JsonSerializer.Serialize(workspace.Path) + " },"));

        var sessionId = await harness.GetSessionIdAsync(cancellationToken);
        await using var events = await harness.OpenEventsAsync(sessionId, cancellationToken, announceIde: true);
        await WaitForAsync(() => harness.IdeGate.IsIdeFrontendConnected(sessionId), cancellationToken);

        var runId = await harness.SubmitTurnAsync(sessionId, "hover a.cs", cancellationToken);

        // The run parks inside ide_hover until the frontend answers, so the
        // announcement must surface on the events socket first: a JSON tool.progress
        // item whose value wraps the ide.request envelope — the exact bytes the
        // extension host's extractIdeRequest reads.
        var leading = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "tool.progress" &&
                     frame.TryGetProperty("content", out var content) &&
                     content.GetProperty("kind").GetString() == "json" &&
                     content.TryGetProperty("value", out var value) &&
                     value.TryGetProperty("ideRequest", out _),
            cancellationToken);
        Assert.Contains(leading, frame =>
            frame.GetProperty("type").GetString() == "tool.started" &&
            frame.GetProperty("tool").GetString() == "ide_hover");
        var ideFrame = leading.Single(frame => frame.GetProperty("type").GetString() == "tool.progress");
        var ideRequest = ideFrame.GetProperty("content").GetProperty("value").GetProperty("ideRequest");
        Assert.Equal("textDocument/hover", ideRequest.GetProperty("method").GetString());
        Assert.Equal(runId, ideFrame.GetProperty("runId").GetString());
        var requestId = ideRequest.GetProperty("requestId").GetString() is { } announcedId
            ? announcedId
            : throw new InvalidOperationException("The tool.progress frame carried no requestId.");
        Assert.StartsWith("ide-", requestId);
        Assert.Equal(new Uri(Path.Combine(workspace.Path, "a.cs")).AbsoluteUri, ideRequest.GetProperty("params").GetProperty("uri").GetString());
        Assert.Equal(0, ideRequest.GetProperty("params").GetProperty("position").GetProperty("line").GetInt32());

        // Playing the frontend: the answer completes the pending request through the
        // real endpoint, the tool loop resumes toward the provider's final answer,
        // and the turn commits with the answer's value in the follow-up request
        // (expectedToolResultText).
        var answered = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{requestId}",
            new { value = new { contents = new { kind = "markdown", value = "class C" } } },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        // Waiting for the commit first gives a failed run a named verdict (the run's
        // protocol code) instead of a bare socket-read timeout below.
        await harness.WaitForTurnCommittedAsync(sessionId, cancellationToken, runId);
        var trailing = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            cancellationToken);
        Assert.Contains(trailing, frame => frame.GetProperty("type").GetString() == "tool.finished");
        Assert.Contains("tool-backed answer", trailing.Where(frame => frame.GetProperty("type").GetString() == "text.delta")
            .Select(frame => frame.GetProperty("text").GetString()));
    }

    [Fact(Timeout = 120_000)]
    public async Task RunLoopForwardsWorkspaceReadsThroughTheBridgeAndTheAnswerCarriesTheBufferState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "a.txt"), "disk state\n");
        var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            toolFlow: true,
            toolName: "read_text",
            toolArgumentsJson: """{"uri":"workspace://local/a.txt"}""",
            expectedToolResultText: "buffer state");
        // Pin the composed workspace root to the throwaway directory so the model's
        // workspace://local/a.txt call resolves against a file this test owns.
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            cancellationToken,
            provider,
            hanging: false,
        transformConfiguration: body => body.Replace(
            "\"Maieutics\": {",
            "\"Maieutics\": {\n    \"Workspace\": { \"Root\": " +
            JsonSerializer.Serialize(workspace.Path) + " },"));

        var sessionId = await harness.GetSessionIdAsync(cancellationToken);
        await using var events = await harness.OpenEventsAsync(sessionId, cancellationToken, announceIde: true);
        await WaitForAsync(() => harness.IdeGate.IsIdeFrontendConnected(sessionId), cancellationToken);

        var runId = await harness.SubmitTurnAsync(sessionId, "read a.txt", cancellationToken);

        // The bridge's targeted frames arrive on this events socket (other announcers
        // never see them). First the coverage fetch — the authority's folder roots —
        // then the read itself; each answered through the real endpoint like the
        // extension host would.
        var coverageFrames = await events.CollectUntilAsync(
            frame => IsIdeRequestMethod(frame, "maieutics/fs/list"), cancellationToken);
        await AnswerIdeRequestAsync(
            harness,
            IdeRequestIdOf(coverageFrames[^1]),
            new { folders = new[] { new { id = "f1", uri = new Uri(workspace.Path).AbsoluteUri } } },
            cancellationToken);

        var readFrames = await events.CollectUntilAsync(
            frame => IsIdeRequestMethod(frame, "maieutics/fs/local/read"), cancellationToken);
        var readRequest = readFrames[^1].GetProperty("data");
        Assert.Equal(new Uri(Path.Combine(workspace.Path, "a.txt")).AbsoluteUri, readRequest.GetProperty("params").GetProperty("uri").GetString());
        await AnswerIdeRequestAsync(
            harness,
            readRequest.GetProperty("requestId").GetString()!,
            new { content = "buffer state\n" },
            cancellationToken);

        await harness.WaitForTurnCommittedAsync(sessionId, cancellationToken, runId);
        var trailing = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            cancellationToken);
        Assert.Contains(trailing, frame => frame.GetProperty("type").GetString() == "tool.finished");
    }

    private static bool IsIdeRequestMethod(JsonElement frame, string method)
    {
        return frame.GetProperty("type").GetString() == "ide.request" &&
               frame.TryGetProperty("data", out var data) &&
               data.ValueKind == JsonValueKind.Object &&
               data.TryGetProperty("method", out var methodElement) &&
               methodElement.GetString() == method;
    }

    private static string IdeRequestIdOf(JsonElement frame)
    {
        return frame.GetProperty("data").GetProperty("requestId").GetString()!;
    }

    private static async Task AnswerIdeRequestAsync(
        FrontendApiIntegrationTests.FrontendHarness harness,
        string requestId,
        object value,
        CancellationToken cancellationToken)
    {
        var answered = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{requestId}",
            new { value },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
    }

    // ---------- helpers ----------

    private static (IdeFunctions functions, IdeRequestRegistry registry, FakeGate gate, FrontendIdeSockets sockets) CreateFunctions(
        string root,
        TimeSpan? timeout = null)
    {
        var sockets = new FrontendIdeSockets();
        var registry = new IdeRequestRegistry();
        var gate = new FakeGate();
        var functions = new IdeFunctions(
            Workspace.Create(root, root),
            registry,
            gate,
            new WorkspaceEditFunctions(Workspace.Create(root, root)),
            sockets)
        {
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(10)
        };
        return (functions, registry, gate, sockets);
    }

    private static AIFunction Function(IReadOnlyList<AIFunction> functions, string name)
    {
        return functions.Single(function => function.Name == name);
    }

    private static AIFunctionArguments ContextArguments(
        string json,
        Func<AIContent, CancellationToken, ValueTask> report)
    {
        using var document = JsonDocument.Parse(json);
        return new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()))
        {
            Context = new Dictionary<object, object?>
            {
                [typeof(AgentToolContext)] = new AgentToolContext(
                    new AgentSessionId(Guid.NewGuid()),
                    AgentRunId.Create(),
                    AgentToolCallId.Create(),
                    report)
            }
        };
    }

    private static async Task<WorkspaceToolInvocations.ToolInvocation> InvokeAsync(
        AIFunction function,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return new WorkspaceToolInvocations.ToolInvocation(
                await function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(true),
                null);
        }
        catch (AgentToolException exception)
        {
            return new WorkspaceToolInvocations.ToolInvocation(null, exception);
        }
    }

    /// <summary>Invokes one ide tool and completes its announced request from the test
    /// side, playing the frontend: the payload is read from the captured progress item,
    /// the answer is driven through the real registry, and the tool's result comes back
    /// through its ordinary invocation.</summary>
    private static async Task<IdeInvocation> InvokeWithAnswerAsync(
        AIFunction function,
        string argumentsJson,
        IdeRequestRegistry registry,
        Func<string, IdeRequestAnswer> answer,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<string> announcedPayload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = function.InvokeAsync(
            ContextArguments(
                argumentsJson,
                (content, _) =>
                {
                    announcedPayload.TrySetResult(Encoding.UTF8.GetString(Assert.IsType<DataContent>(content).Data.Span));
                    return ValueTask.CompletedTask;
                }),
            cancellationToken).AsTask();

        string payloadJson;
        try
        {
            payloadJson = await announcedPayload.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            // Surface the invocation's own failure instead of a bare announce timeout.
            _ = await invocation.ConfigureAwait(true);
            throw;
        }

        var payload = JsonDocument.Parse(payloadJson).RootElement.GetProperty("ideRequest");
        var requestId = payload.GetProperty("requestId").GetString()!;
        if (!registry.TryComplete(requestId, answer(requestId)))
            throw new InvalidOperationException("The announced ide request was not pending.");

        var result = await invocation.ConfigureAwait(true);
        return new IdeInvocation(
            payload.GetProperty("method").GetString()!,
            payloadJson,
            Assert.IsType<JsonElement>(result));
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(50, wait.Token).ConfigureAwait(true);
        }
    }

    private sealed class FakeGate : IIdeFrontendGate
    {
        public bool Connected { get; set; }

        public bool HasIdeFrontend => Connected;

        public bool IsIdeFrontendConnected(string sessionId) => Connected;
    }

    /// <summary>The canonical physical path of a scratch file — macOS /tmp is a
    /// symlink to /private/tmp, and the bridge's answers carry the canonical
    /// form the editor reports.</summary>
    private static string DenoRealPath(string path)
    {
        return Path.GetFullPath(path);
    }

    private sealed record IdeInvocation(string Method, string PayloadJson, JsonElement Result);
}
