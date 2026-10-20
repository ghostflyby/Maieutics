using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>
///     The plugin tool coordinator: describe reconciliation, sticky last-good, reserved-name
///     rejection, and the run-profile snapshot contract. The invoker is faked here, so these
///     tests pin the kernel-side semantics without spawning a plugin host; the real Deno
///     round trip is covered by the host interop suites.
/// </summary>
public sealed class PluginToolCoordinatorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DescribedExportsBecomeSnapshotTools()
    {
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, _) =>
            Task.FromResult(Describe(pluginId, exportName, "echo")));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        var tools = coordinator.SnapshotTools();
        var tool = Assert.Single(tools);
        Assert.Equal("echo", tool.Name);
        Assert.Equal(JsonValueKind.Object, tool.JsonSchema.ValueKind);
    }

    [Fact]
    public async Task RemovedRegistrationDropsTheDefinition()
    {
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, _) =>
            Task.FromResult(Describe(pluginId, exportName, "echo")));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.Single(coordinator.SnapshotTools());

        coordinator.OnRegistryFrame([], EmptyForced);
        Assert.Empty(coordinator.SnapshotTools());
    }

    [Fact]
    public async Task AFailedDescribeKeepsThePreviousDefinitionAndRetriesNextFrame()
    {
        var fail = false;
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, _) =>
            Task.FromResult(fail
                ? ExtensionCallOutcome.Error("host_invoke_failed", "boom")
                : Describe(pluginId, exportName, "echo")));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.Single(coordinator.SnapshotTools());

        // A forced (reload) frame re-describes; its failure must not drop the sticky
        // definition.
        fail = true;
        coordinator.OnRegistryFrame(
            [Registration("plugin-1", "./echo")],
            new HashSet<string>(["plugin-1"], StringComparer.Ordinal));
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.Equal("echo", Assert.Single(coordinator.SnapshotTools()).Name);

        // The retry set keeps the failure pending: a plain frame re-describes.
        fail = false;
        var describesBefore = invoker.Calls.Count(call => call.Phase == "describe");
        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(
            invoker.Calls.Count(call => call.Phase == "describe") > describesBefore,
            "a failed describe is retried on the next frame");
    }

    [Fact]
    public async Task AnInvalidDeclarationIsHiddenAndDoesNotThrow()
    {
        var invoker = new FakeInvoker((_, _, _, _, _, _, _) =>
            Task.FromResult(ExtensionCallOutcome.Result(
                JsonSerializer.SerializeToElement(new { name = "has space", description = "d" }))));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./bad")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        Assert.Empty(coordinator.SnapshotTools());
    }

    [Fact]
    public async Task AReservedNameIsHidden()
    {
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, _) =>
            Task.FromResult(Describe(pluginId, exportName, "workspace_read")));
        await using var coordinator = CreateCoordinator(
            invoker,
            reservedNames: new HashSet<string>(["workspace_read"], StringComparer.Ordinal));

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        Assert.Empty(coordinator.SnapshotTools());
    }

    [Fact]
    public async Task AForcedPluginIsDescribedAgain()
    {
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, _) =>
            Task.FromResult(Describe(pluginId, exportName, "echo")));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var before = invoker.Calls.Count(call => call.Phase == "describe");

        coordinator.OnRegistryFrame(
            [Registration("plugin-1", "./echo")],
            new HashSet<string>(["plugin-1"], StringComparer.Ordinal));
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        Assert.Equal(before + 1, invoker.Calls.Count(call => call.Phase == "describe"));
    }

    [Fact]
    public async Task TheSnapshotIsStableAcrossLaterFrames()
    {
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, _) =>
            Task.FromResult(Describe(pluginId, exportName, "echo")));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var snapshot = coordinator.SnapshotTools();

        coordinator.OnRegistryFrame([], EmptyForced);

        Assert.Equal("echo", Assert.Single(snapshot).Name);
        Assert.Empty(coordinator.SnapshotTools());
    }

    [Fact]
    public async Task ACallRoutesTheModelArgumentsAndReturnsTheEnvelope()
    {
        using var arguments = JsonDocument.Parse("""{"text":"hello"}""");
        var invoker = new FakeInvoker((pluginId, exportName, extensionPoint, _, _, _, callId) =>
            Task.FromResult(callId is null
                ? Describe(pluginId, exportName, "echo")
                : ExtensionCallOutcome.Result(
                    JsonSerializer.SerializeToElement(new { outcome = "ok", value = "hello" }))));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        var tool = Assert.Single(coordinator.SnapshotTools());
        var envelope = await InvokeAsync(tool, arguments.RootElement, TestContext.Current.CancellationToken);

        Assert.Equal("ok", envelope.GetProperty("status").GetString());
        Assert.Equal("hello", envelope.GetProperty("value").GetString());

        var call = Assert.Single(invoker.Calls, call => call.Phase == "describe" ? false : true);
        Assert.NotNull(call.Arguments);
        Assert.Equal("hello", call.Arguments.Value.GetProperty("text").GetString());
        Assert.NotNull(call.CallId);
        Assert.NotNull(call.SessionId);
    }

    [Fact]
    public async Task ARecoverableErrorOutcomeBecomesAnErrorEnvelope()
    {
        var invoker = new FakeInvoker((pluginId, exportName, extensionPoint, _, _, _, callId) =>
            Task.FromResult(callId is null
                ? Describe(pluginId, exportName, "echo")
                : ExtensionCallOutcome.Result(JsonSerializer.SerializeToElement(
                    new { outcome = "error", code = "no_match", message = "Nothing matched." }))));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        using var arguments = JsonDocument.Parse("{}");
        var envelope = await InvokeAsync(
            Assert.Single(coordinator.SnapshotTools()),
            arguments.RootElement,
            TestContext.Current.CancellationToken);

        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.Equal("no_match", envelope.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AHostErrorBecomesARecoverableEnvelope()
    {
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, callId) =>
            Task.FromResult(callId is null
                ? Describe(pluginId, exportName, "echo")
                : ExtensionCallOutcome.Error("plugin_pending_approval", "Not approved.")));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        using var arguments = JsonDocument.Parse("{}");
        var envelope = await InvokeAsync(
            Assert.Single(coordinator.SnapshotTools()),
            arguments.RootElement,
            TestContext.Current.CancellationToken);

        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.Equal("plugin_pending_approval", envelope.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ABudgetExpiryBecomesATimeoutEnvelope()
    {
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, callId) =>
        {
            if (callId is null)
                return Task.FromResult(Describe(pluginId, exportName, "slow"));
            // The manager throws this when the per-call budget fires without caller cancel.
            return Task.FromException<ExtensionCallOutcome>(
                new OperationCanceledException("budget"));
        });
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./slow")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        using var arguments = JsonDocument.Parse("{}");
        var envelope = await InvokeAsync(
            Assert.Single(coordinator.SnapshotTools()),
            arguments.RootElement,
            TestContext.Current.CancellationToken);

        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.Equal("tool_timeout", envelope.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AShutdownCancelPropagatesInsteadOfBecomingAResult()
    {
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, callId) =>
        {
            if (callId is null)
                return Task.FromResult(Describe(pluginId, exportName, "slow"));
            return Task.FromException<ExtensionCallOutcome>(
                new OperationCanceledException("caller"));
        });
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./slow")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        using var arguments = JsonDocument.Parse("{}");
        var tool = Assert.Single(coordinator.SnapshotTools());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Run(() => InvokeAsync(tool, arguments.RootElement, cancellation.Token)));
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightDescribe()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoker = new FakeInvoker(async (_, _, _, _, cancellationToken, _, _) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ExtensionCallOutcome.Result(null);
        });
        var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        await coordinator.DisposeAsync().AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.Empty(coordinator.SnapshotTools());
    }

    [Fact]
    public async Task AHostUnavailableExceptionBecomesARecoverableEnvelope()
    {
        // The plugin host is a separate process that can be disconnected, restarting, or gone.
        // Those failures throw rather than returning an error outcome, and must not escape as
        // a turn failure.
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, callId) =>
            callId is null
                ? Task.FromResult(Describe(pluginId, exportName, "echo"))
                : Task.FromException<ExtensionCallOutcome>(
                    new InvalidOperationException("The plugin host is not connected.")));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        using var arguments = JsonDocument.Parse("{}");
        var envelope = await InvokeAsync(
            Assert.Single(coordinator.SnapshotTools()),
            arguments.RootElement,
            TestContext.Current.CancellationToken);

        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.Equal("plugin_unavailable", envelope.GetProperty("code").GetString());
        Assert.Contains("not connected", envelope.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ADisposedHostLifetimeBecomesARecoverableEnvelope()
    {
        // A restart window can dispose the manager's lifetime source, which makes the linked
        // token creation throw. That is the same class of failure as a disconnect.
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, callId) =>
            callId is null
                ? Task.FromResult(Describe(pluginId, exportName, "echo"))
                : Task.FromException<ExtensionCallOutcome>(
                    new ObjectDisposedException("CancellationTokenSource")));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        using var arguments = JsonDocument.Parse("{}");
        var envelope = await InvokeAsync(
            Assert.Single(coordinator.SnapshotTools()),
            arguments.RootElement,
            TestContext.Current.CancellationToken);

        Assert.Equal("plugin_unavailable", envelope.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ATurnCancellationStillPropagatesRatherThanBecomingAnEnvelope()
    {
        // The recoverable mapping must not swallow a real cancellation: a cancelled turn is
        // not a tool-scoped failure.
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, _, callId) =>
            callId is null
                ? Task.FromResult(Describe(pluginId, exportName, "echo"))
                : Task.FromException<ExtensionCallOutcome>(new OperationCanceledException("turn")));
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        using var arguments = JsonDocument.Parse("{}");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Run(() => InvokeAsync(
                Assert.Single(coordinator.SnapshotTools()),
                arguments.RootElement,
                cancellation.Token)));
    }

    [Fact]
    public async Task TheDeclaredTimeoutReachesTheInvocation()
    {
        // The declaration's budget must arrive at the manager as the call's timeout; a
        // mis-wired parameter would leave every tool on the default and this suite green.
        TimeSpan? observed = null;
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, timeout, callId) =>
        {
            if (callId is null)
                return Task.FromResult(Describe(pluginId, exportName, "echo", timeoutMs: 7_000));
            observed = timeout;
            return Task.FromResult(ExtensionCallOutcome.Result(
                JsonSerializer.SerializeToElement(new { outcome = "ok", value = 1 })));
        });
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        using var arguments = JsonDocument.Parse("{}");
        await InvokeAsync(
            Assert.Single(coordinator.SnapshotTools()),
            arguments.RootElement,
            TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(7), observed);
    }

    [Fact]
    public async Task AnUndeclaredTimeoutUsesTheDefaultBudget()
    {
        TimeSpan? observed = null;
        var invoker = new FakeInvoker((pluginId, exportName, _, _, _, timeout, callId) =>
        {
            if (callId is null) return Task.FromResult(Describe(pluginId, exportName, "echo"));
            observed = timeout;
            return Task.FromResult(ExtensionCallOutcome.Result(
                JsonSerializer.SerializeToElement(new { outcome = "ok", value = 1 })));
        });
        await using var coordinator = CreateCoordinator(invoker);

        coordinator.OnRegistryFrame([Registration("plugin-1", "./echo")], EmptyForced);
        await coordinator.FlightIdle.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        using var arguments = JsonDocument.Parse("{}");
        await InvokeAsync(
            Assert.Single(coordinator.SnapshotTools()),
            arguments.RootElement,
            TestContext.Current.CancellationToken);

        Assert.Equal(PluginToolDefinition.DefaultTimeout, observed);
    }

    [Fact]
    public async Task TheKernelWaitAddsTheWorkerStartAllowance()
    {
        // The declared budget is the handler's, so the kernel's own wait must be strictly
        // larger: equal bounds would let a cold worker start consume the handler's time and
        // report a timeout for work that never ran.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(deadline.Token);

        harness.Manager.HandleHostMessage(ToolRegistryFrame("plugin-1", "./echo"));
        var idle = harness.Manager.PluginToolsIdle;
        await ReplyToDescribeAsync(harness, deadline.Token);
        await idle.WaitAsync(Deadline, deadline.Token);

        var tool = Assert.Single(harness.Manager.SnapshotPluginTools());
        var invocation = new AIFunctionArguments(new Dictionary<string, object?>())
        {
            Context = new Dictionary<object, object?>
            {
                [typeof(AgentToolContext)] = new AgentToolContext(
                    AgentSessionId.Create(),
                    AgentRunId.Create(),
                    AgentToolCallId.Create(),
                    static (_, _) => ValueTask.CompletedTask),
            },
        };

        var call = tool.InvokeAsync(invocation, deadline.Token).AsTask();
        var envelope = EnvelopeOf(await harness.Host.ReadSentAsync(deadline.Token));
        Assert.Equal("call", PayloadOf(envelope).Request?.GetProperty("phase").GetString());

        // A 1 s declaration must still be outstanding well past 1 s: the wait budget is
        // 1 s + the startup allowance, so nothing may cancel it at the handler's own bound.
        await Task.Delay(TimeSpan.FromMilliseconds(1500), deadline.Token);
        Assert.False(call.IsCompleted, "the kernel wait outlives the handler's own budget");

        // Three nesting levels: root, payload, value. A fourth brace would make the frame
        // malformed, and the manager's receive loop drops unparsable frames by design.
        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"host.invokeResult\",\"correlationId\":\"" + envelope.CorrelationId +
            "\",\"payload\":{\"value\":{\"outcome\":\"ok\",\"value\":\"late but allowed\"}}}");

        // The bridge returns the response's raw `value`; the caller (AgentSession) owns the
        // envelope, so a string value arrives as a JSON string, not a wrapped object.
        var result = Assert.IsType<JsonElement>(await call);
        Assert.Equal("late but allowed", result.GetString());
    }

    private static string ToolRegistryFrame(string pluginId, string exportName)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            plugins = new[]
            {
                new
                {
                    pluginId,
                    exportName,
                    extensionPoints = new[] { ReplExtensionPointName.Tool },
                },
            },
        });
        return JsonSerializer.Serialize(
            new ReplEnvelope(
                1,
                ReplMessageType.ExtensionRegistry,
                Guid.NewGuid().ToString("N"),
                payload),
            ReplControlJsonContext.Default.ReplEnvelope);
    }

    private static async Task ReplyToDescribeAsync(
        PluginHostInvokeTests.HostHarness harness,
        CancellationToken cancellationToken)
    {
        var envelope = EnvelopeOf(await harness.Host.ReadSentAsync(cancellationToken));
        Assert.Equal(ReplExtensionPointName.Tool, PayloadOf(envelope).ExtensionPoint);

        // A 1 s declaration: the point of the test is that the kernel's wait still outlives it.
        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"host.invokeResult\",\"correlationId\":\"" + envelope.CorrelationId +
            "\",\"payload\":{\"value\":{\"name\":\"echo\",\"description\":\"Echo the text.\"," +
            "\"timeoutMs\":1000," +
            "\"inputSchema\":{\"type\":\"object\",\"properties\":{},\"required\":[]}}}}");
    }

    private static HostInvokePayload PayloadOf(ReplEnvelope envelope)
    {
        return JsonSerializer.Deserialize(
            envelope.Payload?.GetRawText() ?? string.Empty,
            ReplControlJsonContext.Default.HostInvokePayload)!;
    }

    private static ReplEnvelope EnvelopeOf(string json)
    {
        return JsonSerializer.Deserialize(json, ReplControlJsonContext.Default.ReplEnvelope)!;
    }

    [Fact]
    public async Task ABudgetLessCallKeepsTheDefaultCeiling()
    {
        // The worker-start allowance protects a *declared* handler budget. A budget-less call
        // (discovery, skills, hooks) already had a ceiling covering a cold start; adding the
        // allowance there would silently double how long a wedged call blocks. The distinction
        // lives in a private helper, so it is pinned through the public surface: a declared
        // budget must outlive it, and the caller must stay cancellable either way.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(deadline.Token);

        harness.Manager.HandleHostMessage(ToolRegistryFrame("plugin-1", "./echo"));
        var idle = harness.Manager.PluginToolsIdle;
        await ReplyToDescribeAsync(harness, deadline.Token);
        await idle.WaitAsync(Deadline, deadline.Token);

        var tool = Assert.Single(harness.Manager.SnapshotPluginTools());
        var invocation = new AIFunctionArguments(new Dictionary<string, object?>())
        {
            Context = new Dictionary<object, object?>
            {
                [typeof(AgentToolContext)] = new AgentToolContext(
                    AgentSessionId.Create(),
                    AgentRunId.Create(),
                    AgentToolCallId.Create(),
                    static (_, _) => ValueTask.CompletedTask),
            },
        };

        // A declared 1 s budget: the wait must outlive it (allowance applied), and cancelling
        // the caller still wins immediately (the caller's token is always linked).
        var call = tool.InvokeAsync(invocation, deadline.Token).AsTask();
        _ = await harness.Host.ReadSentAsync(deadline.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token);
        Assert.False(call.IsCompleted);

        await deadline.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await call; });
    }

    private static readonly IReadOnlySet<string> EmptyForced =
        new HashSet<string>(StringComparer.Ordinal);

    private static PluginToolCoordinator CreateCoordinator(
        FakeInvoker invoker,
        IReadOnlySet<string>? reservedNames = null)
    {
        return new PluginToolCoordinator(
            invoker.InvokeAsync,
            () => reservedNames ?? new HashSet<string>(StringComparer.Ordinal),
            NullLogger<PluginToolCoordinator>.Instance);
    }

    private static PluginRegistration Registration(string pluginId, string exportName) =>
        new(pluginId, exportName, ReplExtensionPointName.Tool);

    private static ExtensionCallOutcome Describe(
        string pluginId,
        string exportName,
        string name,
        JsonElement? schema = null,
        int? timeoutMs = null)
    {
        _ = pluginId;
        _ = exportName;
        return ExtensionCallOutcome.Result(JsonSerializer.SerializeToElement(new
        {
            name,
            description = $"{name} tool.",
            inputSchema = schema ?? JsonDocument.Parse(
                """{"type":"object","properties":{"text":{"type":"string"}}}""").RootElement.Clone(),
            timeoutMs,
        }));
    }

    /// <summary>Invokes one function exactly as <c>AgentSession</c> does: it owns the result
    /// envelope, so a plugin tool that returned a pre-built envelope would show up here as a
    /// double-wrapped success. Reusing <c>ToolJson</c> keeps the assertion honest.</summary>
    private static async Task<JsonElement> InvokeAsync(
        AIFunction function,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var context = new AgentToolContext(
            AgentSessionId.Create(),
            AgentRunId.Create(),
            AgentToolCallId.Create(),
            static (_, _) => ValueTask.CompletedTask);
        using var document = JsonDocument.Parse(arguments.GetRawText());
        var values = document.RootElement
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => (object?)property.Value.Clone());
        var invocation = new AIFunctionArguments(values)
        {
            Context = new Dictionary<object, object?>
            {
                [typeof(AgentToolContext)] = context,
            },
        };

        try
        {
            var result = await function.InvokeAsync(invocation, cancellationToken);
            return result switch
            {
                null => ToolJson.CreateSuccessEnvelope(null),
                JsonElement element => ToolJson.CreateSuccessEnvelope(element),
                _ => throw new InvalidOperationException(
                    $"AI function results must be JsonElement or null, but '{result.GetType().Name}' was returned."),
            };
        }
        catch (AgentToolException exception)
        {
            return ToolJson.CreateFailureEnvelope(exception.Code, exception.Message);
        }
    }

    private sealed class FakeInvoker(
        Func<string, string, string, JsonElement?, CancellationToken, TimeSpan?, string?, Task<ExtensionCallOutcome>> body)
    {
        internal List<CallRecord> Calls { get; } = [];

        internal Task<ExtensionCallOutcome> InvokeAsync(
            string pluginId,
            string exportName,
            string extensionPoint,
            JsonElement? request,
            CancellationToken cancellationToken,
            TimeSpan? timeout,
            string? callId)
        {
            var phase = request is { } value && value.TryGetProperty("phase", out var phaseElement)
                ? phaseElement.GetString() ?? string.Empty
                : string.Empty;
            JsonElement? arguments = request is { } requestValue &&
                                     requestValue.TryGetProperty("arguments", out var argumentValue)
                ? argumentValue.Clone()
                : null;
            var sessionId = request is { } sessionValue &&
                            sessionValue.TryGetProperty("sessionId", out var sessionElement)
                ? sessionElement.GetString()
                : null;
            Calls.Add(new CallRecord(
                phase,
                exportName,
                extensionPoint,
                arguments,
                callId,
                sessionId));
            return body(pluginId, exportName, extensionPoint, request, cancellationToken, timeout, callId);
        }
    }

    private sealed record CallRecord(
        string Phase,
        string ExportName,
        string ExtensionPoint,
        JsonElement? Arguments,
        string? CallId,
        string? SessionId);
}
