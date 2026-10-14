using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Execution;
using Maieutics.Frontend;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Maieutics.Product.Tests;

/// <summary>
///     The IDE capability bridge (ADR 0042 stage A): tool-level round trips through the
///     real registry and workspace translation, the answer endpoint's contract, and the
///     per-socket capability announcement that gates the family.
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendIdeBridgeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(500);

    [Fact(Timeout = 30_000)]
    public async Task HoverAnnouncesIdeRequestAndTranslatesTheAnswerIntoAgentConventions()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate) = CreateFunctions(workspace.Path);
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
        invocation.Method.Should().Be("textDocument/hover");
        var outgoing = JsonDocument.Parse(invocation.PayloadJson).RootElement.GetProperty("ideRequest");
        outgoing.GetProperty("requestId").GetString().Should().StartWith("ide-");
        outgoing.GetProperty("params").GetProperty("uri").GetString().Should().Be(
            new Uri(Path.Combine(workspace.Path, "a.cs")).AbsoluteUri);
        outgoing.GetProperty("params").GetProperty("position").GetProperty("line").GetInt32().Should().Be(40);
        outgoing.GetProperty("params").GetProperty("position").GetProperty("character").GetInt32().Should().Be(6);

        // Incoming: results map back to 1-based positions; hover contents pass through.
        var result = invocation.Result;
        result.GetProperty("contents").GetProperty("kind").GetString().Should().Be("markdown");
        result.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32().Should().Be(41);
        result.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32().Should().Be(7);
    }

    [Fact(Timeout = 30_000)]
    public async Task NavigateTranslatesFileUrisInTheResultBackToTheWorkspaceDomain()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate) = CreateFunctions(workspace.Path);
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

        invocation.Method.Should().Be("textDocument/definition");
        var location = invocation.Result[0];
        location.GetProperty("uri").GetString().Should().Be("workspace://local/b.cs");
        location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32().Should().Be(10);
    }

    [Fact(Timeout = 30_000)]
    public async Task UnansweredRequestFailsTypedTimeoutAndRepeatedAnswersRenderAs404()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate) = CreateFunctions(workspace.Path, TestTimeout);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        Func<Task> timed = async () => await hover.InvokeAsync(
            ContextArguments(
                """{"uri":"workspace://local/a.cs","select":{"line":1}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken).AsTask();
        (await timed.Should().ThrowAsync<AgentToolException>()).Which.Code.Should().Be("ide_timeout");

        // The answer endpoint is the only completion path, so the registry's idempotence
        // is the 404 contract: one answer completes, any repeat draws the typed 404 the
        // frontend absorbs.
        var registration = registry.Begin();
        registry.TryComplete(registration.RequestId, IdeRequestAnswer.FromValue(JsonDocument.Parse("1").RootElement.Clone()))
            .Should().BeTrue();
        registry.TryComplete(registration.RequestId, IdeRequestAnswer.FromValue(JsonDocument.Parse("2").RootElement.Clone()))
            .Should().BeFalse("a second answer for the same id is the endpoint's typed 404");
    }

    [Fact(Timeout = 30_000)]
    public async Task CancelledToolCallAbandonsTheRequestImmediately()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate) = CreateFunctions(workspace.Path);
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

        cancelled.Cancel();
        await invocation.Awaiting(static task => task).Should().ThrowAsync<OperationCanceledException>();
        registry.TryComplete(requestId, IdeRequestAnswer.FromValue(JsonDocument.Parse("1").RootElement.Clone()))
            .Should().BeFalse("the cancelled tool call left no resumable request");
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolsRefuseWithoutAnAnnouncingFrontendOnTheSession()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, _, gate) = CreateFunctions(workspace.Path);
        var symbols = Function(functions.Functions, "ide_document_symbols");

        var invocation = await InvokeAsync(
            symbols,
            ContextArguments(
                "{\"uri\":\"workspace://local/a.cs\"}",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);

        invocation.Failure.Should().NotBeNull();
        invocation.Failure!.Code.Should().Be("ide_unavailable");
    }

    [Fact(Timeout = 30_000)]
    public async Task EscapeHatchRejectsWriteMethodsWithoutARoundTripAndPassesBareNumbersVerbatim()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate) = CreateFunctions(workspace.Path, TestTimeout);
        gate.Connected = true;
        var escape = Function(functions.Functions, "ide_request");

        var rejected = await InvokeAsync(
            escape,
            ContextArguments(
                """{"method":"textDocument/rename","parameters":{"kind":"rename"}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);
        rejected.Failure.Should().NotBeNull();
        rejected.Failure!.Code.Should().Be("ide_method_unsupported");

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
        envelope.GetProperty("method").GetString().Should().Be("textDocument/foldingRange");
        envelope.GetProperty("params").GetProperty("uri").GetString().Should().Be(
            new Uri(Path.Combine(workspace.Path, "a.cs")).AbsoluteUri);
        envelope.GetProperty("params").GetProperty("startLine").GetInt32().Should().Be(
            3,
            "bare-number lines pass through as provider values per the tool contract");
        (await invocation.Awaiting(static task => task).Should().ThrowAsync<AgentToolException>())
            .Which.Code.Should().Be("ide_timeout");
    }

    [Fact(Timeout = 30_000)]
    public async Task DiagnosticsAggregateCountsSeveritiesCountsUnmappedAndMarksUnanalyzedState()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, registry, gate) = CreateFunctions(workspace.Path);
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
        analyzed.Method.Should().Be("maieutics/problems");
        var aggregate = JsonSerializer.Deserialize(
            analyzed.Result,
            IdeFunctionJsonContext.Default.WorkspaceDiagnosticsValue)!;
        aggregate.Totals.Error.Should().Be(1);
        aggregate.Totals.Hint.Should().Be(1);
        aggregate.Files.Should().ContainSingle().Which.Uri.Should().Be("workspace://local/a.cs");
        aggregate.UnmappedFiles.Should().Be(1, "the out-of-workspace document drops with a count");
        aggregate.AnalyzedDocuments.Should().Be(7);
        aggregate.Note.Should().BeNull();

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
        empty.Totals.Error.Should().Be(0);
        empty.Note.Should().NotBeNull("empty must not read as clean");

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
        filteredValue.Totals.Warning.Should().Be(0, "minSeverity filters below the threshold");
        filteredValue.Totals.Error.Should().Be(1);
    }

    [Fact(Timeout = 30_000)]
    public async Task SelectorsRejectNonCoordinateFormsAndBadPositions()
    {
        using var workspace = TemporaryWorkspace.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(workspace.Path, "a.cs"), "class C {}\n");
        var (functions, _, gate) = CreateFunctions(workspace.Path);
        gate.Connected = true;
        var hover = Function(functions.Functions, "ide_hover");

        var anchored = await InvokeAsync(
            hover,
            ContextArguments(
                """{"uri":"workspace://local/a.cs","select":{"anchor":["class C {}"]}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);
        anchored.Failure.Should().NotBeNull();
        anchored.Failure!.Code.Should().Be("ide_selector_unsupported");

        var zero = await InvokeAsync(
            hover,
            ContextArguments(
                """{"uri":"workspace://local/a.cs","select":{"line":0}}""",
                static (_, _) => ValueTask.CompletedTask),
            cancellationToken);
        zero.Failure.Should().NotBeNull();
        zero.Failure!.Code.Should().Be("ide_invalid_arguments");
    }

    [Fact(Timeout = 30_000)]
    public void PhysicalPathsMapBackOnlyInsideTheWorkspace()
    {
        using var workspace = TemporaryWorkspace.Create();
        var snapshot = Workspace.Create(workspace.Path, workspace.Path).Capture();
        var root = Path.TrimEndingDirectorySeparator(workspace.Path);

        snapshot.TryMapPhysicalToWorkspaceUri(root, out var rootUri).Should().BeTrue();
        rootUri.Should().Be("workspace://local/");
        snapshot.TryMapPhysicalToWorkspaceUri(Path.Combine(root, "a", "b.cs"), out var nested)
            .Should().BeTrue();
        nested.Should().Be("workspace://local/a/b.cs");
        snapshot.TryMapPhysicalToWorkspaceUri(Path.Combine(root, "a b", "c d.cs"), out var spaces)
            .Should().BeTrue();
        spaces.Should().Be("workspace://local/a%20b/c%20d.cs");
        snapshot.TryMapPhysicalToWorkspaceUri(Path.GetTempPath(), out _).Should().BeFalse();
        snapshot.TryMapPhysicalToWorkspaceUri(root + "-sibling/file.cs", out _)
            .Should().BeFalse("a path sharing the root's prefix but not its containment is outside");
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
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var registration = harness.IdeRequests.Begin();
        var both = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{registration.RequestId}",
            new { value = 1, error = new { code = "x", message = "y" } },
            cancellationToken);
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the answer must carry exactly one side");

        var neither = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{registration.RequestId}",
            new { },
            cancellationToken);
        neither.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var answered = harness.IdeRequests.Begin();
        var post = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{answered.RequestId}",
            new { value = new { hover = "class C" } },
            cancellationToken);
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await answered.Completion;
        answer.Value.Should().NotBeNull();
        answer.Value!.Value.GetProperty("hover").GetString().Should().Be("class C");

        var repeat = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{answered.RequestId}",
            new { value = 1 },
            cancellationToken);
        repeat.StatusCode.Should().Be(HttpStatusCode.NotFound, "a second answer is a typed 404");

        var failing = harness.IdeRequests.Begin();
        var errorPost = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{failing.RequestId}",
            new { error = new { code = "no_language_service", message = "not ready" } },
            cancellationToken);
        errorPost.StatusCode.Should().Be(HttpStatusCode.OK);
        var errorAnswer = await failing.Completion;
        errorAnswer.ErrorCode.Should().Be("no_language_service");
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
        gate.HasIdeFrontend.Should().BeFalse("no socket announced yet");

        var connection = await harness.OpenEventsAsync(sessionId, cancellationToken, announceIde: true);
        try
        {
            await WaitForAsync(() => gate.IsIdeFrontendConnected(sessionId), cancellationToken);
            gate.HasIdeFrontend.Should().BeTrue();
            harness.RuntimeConfiguration.ReservedToolNames.Should()
                .Contain("ide_diagnostics", "the family reserves its names even while hidden");
        }
        finally
        {
            await connection.DisposeAsync();
        }

        await WaitForAsync(() => !gate.IsIdeFrontendConnected(sessionId), cancellationToken);
        gate.HasIdeFrontend.Should().BeFalse("the announcement lapses with the socket");

        // A socket without the parameter never gates the family.
        var plain = await harness.OpenEventsAsync(sessionId, cancellationToken);
        try
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(true);
            gate.IsIdeFrontendConnected(sessionId).Should().BeFalse();
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
        leading.Should().Contain(frame =>
            frame.GetProperty("type").GetString() == "tool.started" &&
            frame.GetProperty("tool").GetString() == "ide_hover");
        var ideFrame = leading.Single(frame => frame.GetProperty("type").GetString() == "tool.progress");
        var ideRequest = ideFrame.GetProperty("content").GetProperty("value").GetProperty("ideRequest");
        ideRequest.GetProperty("method").GetString().Should().Be("textDocument/hover");
        ideFrame.GetProperty("runId").GetString().Should().Be(runId);
        var requestId = ideRequest.GetProperty("requestId").GetString() is { } announcedId
            ? announcedId
            : throw new InvalidOperationException("The tool.progress frame carried no requestId.");
        requestId.Should().StartWith("ide-");
        ideRequest.GetProperty("params").GetProperty("uri").GetString().Should().Be(
            new Uri(Path.Combine(workspace.Path, "a.cs")).AbsoluteUri);
        ideRequest.GetProperty("params").GetProperty("position").GetProperty("line").GetInt32()
            .Should().Be(0, "the kernel rewrites the 1-based selector to the LSP 0-based line");

        // Playing the frontend: the answer completes the pending request through the
        // real endpoint, the tool loop resumes toward the provider's final answer,
        // and the turn commits with the answer's value in the follow-up request
        // (expectedToolResultText).
        var answered = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/ide/{requestId}",
            new { value = new { contents = new { kind = "markdown", value = "class C" } } },
            cancellationToken);
        answered.StatusCode.Should().Be(HttpStatusCode.OK);

        // Waiting for the commit first gives a failed run a named verdict (the run's
        // protocol code) instead of a bare socket-read timeout below.
        await harness.WaitForTurnCommittedAsync(sessionId, cancellationToken, runId);
        var trailing = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            cancellationToken);
        trailing.Should().Contain(frame => frame.GetProperty("type").GetString() == "tool.finished");
        trailing.Where(frame => frame.GetProperty("type").GetString() == "text.delta")
            .Select(frame => frame.GetProperty("text").GetString())
            .Should().Contain("tool-backed answer");
    }

    // ---------- helpers ----------

    private static (IdeFunctions functions, IdeRequestRegistry registry, FakeGate gate) CreateFunctions(
        string root,
        TimeSpan? timeout = null)
    {
        var registry = new IdeRequestRegistry();
        var gate = new FakeGate();
        var functions = new IdeFunctions(Workspace.Create(root, root), registry, gate)
        {
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(10)
        };
        return (functions, registry, gate);
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
        var announced = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = function.InvokeAsync(
            ContextArguments(
                argumentsJson,
                (content, _) =>
                {
                    announced.TrySetResult(Encoding.UTF8.GetString(Assert.IsType<DataContent>(content).Data.Span));
                    return ValueTask.CompletedTask;
                }),
            cancellationToken).AsTask();

        string payloadJson;
        try
        {
            payloadJson = await announced.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
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

    private sealed record IdeInvocation(string Method, string PayloadJson, JsonElement Result);
}
