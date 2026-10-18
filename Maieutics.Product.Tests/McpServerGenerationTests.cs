using System.IO.Pipelines;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Mcp;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maieutics.Product.Tests;

public sealed class McpServerGenerationTests
{
    [Fact]
    public void RestrictedRunPolicyRejectsAnMcpServerCommand()
    {
        var restricted = BuildPolicy(
            (PermissionKind.Run, new PermissionKindRules { Allow = ["/usr/bin/safe-server"] }));

        var check = () => McpServerGeneration.EnsureCommandAllowed("/usr/bin/evil-server", restricted);

        var assertion26 = Assert.ThrowsAny<ArgumentException>(() => check.Invoke());
        Assert.Matches("(?is)^.*not\\ permitted\\ by\\ the\\ effective\\ policy.*$", assertion26.Message);
    }

    [Fact]
    public void RestrictedRunPolicyAllowsAMatchingCommand()
    {
        var restricted = BuildPolicy(
            (PermissionKind.Run, new PermissionKindRules { Allow = ["/usr/bin/safe-server"] }));

        McpServerGeneration.EnsureCommandAllowed("/usr/bin/safe-server", restricted);
    }

    [Fact]
    public void DefaultPolicyAllowsAnyCommand()
    {
        McpServerGeneration.EnsureCommandAllowed("/usr/bin/anything", EffectivePolicy.Default);
    }

    [Fact]
    public void RunGrantDoesNotAdmitSiblingCommands()
    {
        // Prefix matching would let a grant of /usr/bin/safe admit /usr/bin/safe-evil; the
        // path-boundary rule keeps sibling names out.
        var restricted = BuildPolicy(
            (PermissionKind.Run, new PermissionKindRules { Allow = ["/usr/bin/safe"] }));

        var check = () => McpServerGeneration.EnsureCommandAllowed("/usr/bin/safe-evil", restricted);

        var assertion27 = Assert.ThrowsAny<ArgumentException>(() => check.Invoke());
        Assert.Matches("(?is)^.*not\\ permitted\\ by\\ the\\ effective\\ policy.*$", assertion27.Message);
    }

    [Fact]
    public void RunGrantAdmitsCommandsInsideTheGrantedDirectory()
    {
        var restricted = BuildPolicy(
            (PermissionKind.Run, new PermissionKindRules { Allow = ["/opt/servers"] }));

        McpServerGeneration.EnsureCommandAllowed("/opt/servers/safe-server", restricted);
    }

    private static EffectivePolicy BuildPolicy(params (PermissionKind Kind, PermissionKindRules Rules)[] kinds)
    {
        return PermissionLayerStore.Build(
            [new PermissionLayer { Kinds = kinds.ToDictionary(static entry => entry.Kind, static entry => entry.Rules) }],
            new VariableTable(new EmptyVariableSource()));
    }

    private sealed class EmptyVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class FakeToolSurfaceAdjuster(Func<JsonElement, JsonElement?> handler) : IMcpToolSurfaceAdjuster
    {
        private readonly Lock gate = new();
        private int invocations;
        private TaskCompletionSource invoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Invocations => invocations;

        /// <summary>Waits until the adjuster has completed at least <paramref name="count"/>
        /// invocations. Signal-driven: each completed invocation (the handler has run and its
        /// result is known) completes a fresh source, so the wait needs no polling of
        /// <see cref="Invocations"/>.</summary>
        internal async Task WaitForInvocationsAsync(int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Task signal;
                lock (gate)
                {
                    if (invocations >= count) return;

                    signal = invoked.Task;
                }

                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<JsonElement?> AdjustToolsAsync(
            string serverId,
            JsonElement listing,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource completed;
            lock (gate)
            {
                invocations++;
                completed = invoked;
                invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            var result = handler(listing);
            // Completed only after the handler ran, so an awaiter observes the invocation's
            // outcome, not merely its start.
            completed.TrySetResult();
            return result;
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AdjustmentChainRenamesRedescribesAndOverridesSchema()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory();
        var definition = CreateStdioDefinition();
        JsonElement? seenListing = null;
        var adjuster = new FakeToolSurfaceAdjuster(listing =>
        {
            seenListing = listing;
            return ParseJson("""
                [
                  {"aliasOf":"echo","name":"echo_safe","description":"Safe echo",
                   "inputSchema":{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}}
                ]
                """);
        });
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync,
            toolSurfaceAdjuster: adjuster);

        var lease = generation.TryAcquire()
            ?? throw new InvalidOperationException("generation did not expose a lease");
        var tool = Assert.Single(lease.Tools);
        Assert.Equal("echo_safe", tool.Name);
        Assert.Equal("Safe echo", tool.Description);
        // Invocation still routes to the remote echo tool.
        using var argumentsDocument = JsonDocument.Parse("{\"value\":\"hello\"}");
        var arguments = new AIFunctionArguments(argumentsDocument.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));
        var result = await tool.InvokeAsync(arguments, deadline.Token);
        Assert.Equal("hello", Assert.IsType<JsonElement>(result).GetProperty("structuredContent").GetProperty("value").GetString());

        Assert.Equal(new MaieuticsMcpToolInfo("echo", "echo_safe", true), Assert.Single(generation.GetInfo().Tools));
        Assert.NotNull(seenListing);
        Assert.Equal("echo", Assert.Single(seenListing.Value.EnumerateArray()).GetProperty("name").GetString());
        var retirement = generation.Retire();
        await lease.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task AdjustmentChainFailsClosedWithoutAPreviousListing()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory();
        var definition = CreateStdioDefinition();
        var adjuster = new FakeToolSurfaceAdjuster(_ => null);
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync,
            toolSurfaceAdjuster: adjuster);

        var lease = generation.TryAcquire()
            ?? throw new InvalidOperationException("generation did not expose a lease");
        Assert.Empty(lease.Tools);
        Assert.Empty(generation.GetInfo().Tools);
        var retirement = generation.Retire();
        await lease.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task AdjustmentChainAliasExposesBothNamesRoutingToOneRemoteTool()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory();
        var definition = CreateStdioDefinition();
        var adjuster = new FakeToolSurfaceAdjuster(_ => ParseJson("""
            [
              {"aliasOf":"echo"},
              {"aliasOf":"echo","name":"echo_alias"}
            ]
            """));
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync,
            toolSurfaceAdjuster: adjuster);

        var lease = generation.TryAcquire()
            ?? throw new InvalidOperationException("generation did not expose a lease");
        Assert.Equal(["echo", "echo_alias"], lease.Tools.Select(static function => function.Name));
        using var argumentsDocument = JsonDocument.Parse("{\"value\":\"hi\"}");
        var arguments = new AIFunctionArguments(argumentsDocument.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));
        foreach (var tool in lease.Tools)
        {
            var result = await tool.InvokeAsync(arguments, deadline.Token);
            Assert.Equal("hi", Assert.IsType<JsonElement>(result).GetProperty("structuredContent").GetProperty("value").GetString());
        }

        Assert.Equal(new (string RemoteName, string ExposedName)[] { ("echo", "echo"), ("echo", "echo_alias") }, generation.GetInfo().Tools.Select(static info => (info.RemoteName, info.ExposedName)));
        var retirement = generation.Retire();
        await lease.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailingAdjusterFailsClosedOnRefreshWithoutResurrectingTools()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory();
        var definition = CreateStdioDefinition();
        var fail = false;
        var adjuster = new FakeToolSurfaceAdjuster(_ =>
        {
            if (fail) return null;
            return ParseJson("""[{"aliasOf":"echo","name":"echo_safe"}]""");
        });
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync,
            toolSurfaceAdjuster: adjuster);

        var firstLease = generation.TryAcquire()
            ?? throw new InvalidOperationException("generation did not expose a lease");
        Assert.Equal("echo_safe", Assert.Single(firstLease.Tools).Name);
        await firstLease.DisposeAsync();

        // The adjuster stops producing: the refresh fails closed (nothing exposed)
        // instead of resurrecting the raw surface — the sticky-last-good decision
        // belongs to the chain above this layer. The refresh signal — not the bare
        // invocation count — is what proves the failed listing was applied, so the
        // lease below cannot race the refresh chain's application step.
        fail = true;
        var refreshesBefore = generation.ToolRefreshApplications;
        generation.RequestToolRefresh();
        await adjuster.WaitForInvocationsAsync(2, deadline.Token);
        await generation.WaitForToolRefreshApplicationAsync(refreshesBefore, deadline.Token);
        var refreshedLease = generation.TryAcquire()
            ?? throw new InvalidOperationException("generation did not expose a lease");
        Assert.Empty(refreshedLease.Tools);
        var retirement = generation.Retire();
        await refreshedLease.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStickyAdjusterKeepsTheAdjustedSurfaceAcrossRefreshes()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory();
        var definition = CreateStdioDefinition();
        var fail = false;
        JsonElement? lastGood = null;
        // The real chain's sticky semantics, embedded in the fake: a failing backend
        // keeps the last adjusted listing active.
        var adjuster = new FakeToolSurfaceAdjuster(listing =>
        {
            if (fail) return lastGood;
            lastGood = ParseJson("""[{"aliasOf":"echo","name":"echo_safe"}]""");
            return lastGood;
        });
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync,
            toolSurfaceAdjuster: adjuster);

        var firstLease = generation.TryAcquire()
            ?? throw new InvalidOperationException("generation did not expose a lease");
        Assert.Equal("echo_safe", Assert.Single(firstLease.Tools).Name);
        await firstLease.DisposeAsync();

        // As above: await the refresh's application, not just the adjuster call, so the
        // lease below observes the sticky surface the refresh chain actually applied.
        fail = true;
        var refreshesBefore = generation.ToolRefreshApplications;
        generation.RequestToolRefresh();
        await adjuster.WaitForInvocationsAsync(2, deadline.Token);
        await generation.WaitForToolRefreshApplicationAsync(refreshesBefore, deadline.Token);
        var stickyLease = generation.TryAcquire()
            ?? throw new InvalidOperationException("generation did not expose a lease");
        Assert.Equal("echo_safe", Assert.Single(stickyLease.Tools).Name);
        var retirement = generation.Retire();
        await stickyLease.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task OfficialStreamServerDiscoversAndInvokesAllExposedTools()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory();
        var definition = CreateStdioDefinition();
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync);

        var acquired = generation.TryAcquire();
        Assert.NotNull(acquired);
        var lease = acquired;
        Assert.Equal("echo", Assert.Single(lease.Tools).Name);
        using var argumentsDocument = JsonDocument.Parse("{\"value\":\"hello\"}");
        var arguments = new AIFunctionArguments(argumentsDocument.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));

        var result = await lease.Tools.Single().InvokeAsync(arguments, deadline.Token);

        var resultElement = Assert.IsType<JsonElement>(result);
        Assert.False(resultElement.TryGetProperty("isError", out _));
        Assert.Equal("hello", resultElement.GetProperty("structuredContent").GetProperty("value").GetString());
        Assert.Equal(new MaieuticsMcpToolInfo("echo", "echo", true), Assert.Single(generation.GetInfo().Tools));
        var retirement = generation.Retire();
        Assert.False(retirement.IsCompleted);
        await lease.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task ReservedToolNamesAreHiddenAndMarkedUnavailable()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory();
        var definition = CreateStdioDefinition();
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync,
            new HashSet<string>(StringComparer.Ordinal) { "echo" });

        var acquired = generation.TryAcquire();
        Assert.NotNull(acquired);
        Assert.Empty(acquired.Tools);
        Assert.Equal(new MaieuticsMcpToolInfo("echo", "echo", false), Assert.Single(generation.GetInfo().Tools));
        var retirement = generation.Retire();
        await acquired.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task AForcedDisconnectReconnectsAfterAdvancingTheVirtualBackoff()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var clock = new FakeTimeProvider();
        await using var serverFactory = new StreamServerFactory();
        var definition = CreateStdioDefinition();
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            clock,
            deadline.Token,
            serverFactory.CreateTransportAsync);

        // Drop the server side; the supervisor observes the closed client and enters the
        // reconnect wait, registered with the fake clock. Both signals are captured before
        // the drop: a read after the drop can land after the registration already completed,
        // and the property hands that read the (uncompletable) replacement source.
        var reconnected = generation.Reconnected;
        var registered = generation.ReconnectDelayRegistered;
        await serverFactory.DisconnectClientsAsync();
        await registered.WaitAsync(deadline.Token);
        var waiting = generation.GetInfo();
        Assert.Equal(MaieuticsMcpServerState.Reconnecting, waiting.State);
        Assert.Equal(McpServerGeneration.InitialReconnectDelay, waiting.NextReconnectDelay);

        clock.Advance(McpServerGeneration.InitialReconnectDelay);
        await reconnected.WaitAsync(deadline.Token);
        var lease = generation.TryAcquire()
            ?? throw new InvalidOperationException("generation did not expose a reconnected lease");
        Assert.Equal("echo", Assert.Single(lease.Tools).Name);
        Assert.Equal(MaieuticsMcpServerState.Connected, generation.GetInfo().State);
        var retirement = generation.Retire();
        await lease.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailedReconnectAttemptDoublesTheBackoffBeforeTheNextTry()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var clock = new FakeTimeProvider();
        await using var serverFactory = new StreamServerFactory();
        var definition = CreateStdioDefinition();
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            clock,
            deadline.Token,
            serverFactory.CreateTransportAsync);

        // Both signals are captured before the drop: a read after the drop can land after
        // the registration already completed, and the property hands that read the
        // (uncompletable) replacement source. The second capture takes the replacement
        // handed back after the first await consumed the initial registration.
        var reconnected = generation.Reconnected;
        var firstRegistration = generation.ReconnectDelayRegistered;
        await serverFactory.DisconnectClientsAsync();
        serverFactory.FailNextTransport = true;
        await firstRegistration.WaitAsync(deadline.Token);
        var reRegistration = generation.ReconnectDelayRegistered;

        // The failed attempt fired by the advance doubles the delay and re-arms it on the
        // fake clock, completing the re-registration signal; the state flip happens under
        // the same gate before the signal, so the read below observes the doubled delay.
        clock.Advance(McpServerGeneration.InitialReconnectDelay);
        await reRegistration.WaitAsync(deadline.Token);
        var doubled = TimeSpan.FromSeconds(2);
        Assert.Equal(doubled, generation.GetInfo().NextReconnectDelay);

        serverFactory.FailNextTransport = false;
        clock.Advance(doubled);
        await reconnected.WaitAsync(deadline.Token);
        var lease = generation.TryAcquire()
            ?? throw new InvalidOperationException("generation did not expose a reconnected lease");
        Assert.Equal("echo", Assert.Single(lease.Tools).Name);
        var retirement = generation.Retire();
        await lease.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerProgressNotificationsForwardToTheAgentToolContext()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory(reportProgress: true);
        var definition = CreateStdioDefinition();
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync);

        var acquired = generation.TryAcquire();
        Assert.NotNull(acquired);
        var lease = acquired;
        var reported = new List<JsonElement>();
        var bothForwarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = CreateProgressContext((content, _) =>
        {
            var data = Assert.IsType<DataContent>(content);
            Assert.Equal("application/json", data.MediaType);
            using var document = JsonDocument.Parse(data.Data);
            lock (reported)
            {
                reported.Add(document.RootElement.Clone());
                if (reported.Count == 2) bothForwarded.TrySetResult();
            }

            return ValueTask.CompletedTask;
        });
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "hello" })
        {
            Context = new Dictionary<object, object?> { [typeof(AgentToolContext)] = context }
        };

        var result = await lease.Tools.Single().InvokeAsync(arguments, deadline.Token);
        // The invoke completing does not happen after the notification handlers run: the SDK
        // dispatches them on the thread pool, and the forwarder reports asynchronously. Wait
        // for both notifications to land before asserting (bounded; CI runners expose the gap).
        await bothForwarded.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
        await lease.DisposeAsync();

        Assert.IsType<JsonElement>(result);
        Assert.Equal(2, reported.Count());
        // The SDK dispatches notifications on the thread pool, so delivery order between two
        // reports is not guaranteed; the forwarding chain only serializes the writes.
        var ordered = reported.OrderBy(static element => element.GetProperty("progress").GetDouble()).ToArray();
        Assert.Equal(25, ordered[0].GetProperty("progress").GetDouble());
        Assert.Equal(100, ordered[0].GetProperty("total").GetDouble());
        Assert.Equal("quarter", ordered[0].GetProperty("message").GetString());
        Assert.Equal(100, ordered[1].GetProperty("progress").GetDouble());
        Assert.False(ordered[1].TryGetProperty("message", out _));
        await generation.Retire().WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task ProgressReportingToolsInvokeNormallyWithoutAnAgentToolContext()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory(reportProgress: true);
        var definition = CreateStdioDefinition();
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync);

        var acquired = generation.TryAcquire();
        Assert.NotNull(acquired);
        var lease = acquired;
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "hello" });

        var result = await lease.Tools.Single().InvokeAsync(arguments, deadline.Token);
        await lease.DisposeAsync();

        var resultElement = Assert.IsType<JsonElement>(result);
        Assert.Equal("hello", resultElement.GetProperty("structuredContent").GetProperty("value").GetString());
        await generation.Retire().WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task ProgressForwardingPreservesOrderAndSilencesAfterALimitFailure()
    {
        var calls = 0;
        var payloads = new List<double>();
        var context = CreateProgressContext(async (_, _) =>
        {
            calls++;
            if (calls == 2)
                throw new AgentToolLimitExceededException(nameof(AgentSessionOptions.MaxToolProgressEventsPerCall), 256);
            await Task.Yield();
            payloads.Add(calls == 1 ? 1 : 3);
        });
        var forwarder = new McpToolProgressForwarder(context, "test", NullLogger.Instance);

        forwarder.Report(new ProgressNotificationValue { Progress = 1 });
        forwarder.Report(new ProgressNotificationValue { Progress = 2 });
        forwarder.Report(new ProgressNotificationValue { Progress = 3 });
        await forwarder.FlushAsync();

        Assert.Equal(2, calls);
        Assert.Equal(new[] { 1d }, payloads);
    }

    private static AgentToolContext CreateProgressContext(
        Func<AIContent, CancellationToken, ValueTask> report)
    {
        return new AgentToolContext(
            AgentSessionId.Create(),
            AgentRunId.Create(),
            AgentToolCallId.Create(),
            report);
    }

    [Fact]
    public void DeserializesDiscoveryTransportByTypeDiscriminator()
    {
        using var stdio = JsonDocument.Parse("""
                                             {
                                               "type": "stdio",
                                               "command": "deno",
                                               "args": ["run", "server.ts"],
                                               "env": { "PORT": "8080" },
                                               "futureField": 42
                                             }
                                             """);
        var stdioDefinition = Assert.IsType<StdioMcpTransportDefinition>(stdio.RootElement
            .Deserialize(McpJsonContext.Default.McpTransportDefinition));
        Assert.Equal("deno", stdioDefinition.Command);
        Assert.Equal(["run", "server.ts"], stdioDefinition.Arguments);
        Assert.Equal("8080", Assert.Contains("PORT", stdioDefinition.EnvironmentVariables ?? throw new InvalidOperationException("the assertion subject is null")));
        Assert.Equal(McpServerTransportKind.Stdio, stdioDefinition.Kind);

        using var http = JsonDocument.Parse("""
                                            {
                                              "type": "http",
                                              "url": "https://example.com/mcp",
                                              "headers": { "Authorization": "Bearer token" }
                                            }
                                            """);
        var httpDefinition = Assert.IsType<HttpMcpTransportDefinition>(http.RootElement
            .Deserialize(McpJsonContext.Default.McpTransportDefinition));
        Assert.Equal(new Uri("https://example.com/mcp"), httpDefinition.Endpoint);
        Assert.True((httpDefinition.Headers ?? throw new InvalidOperationException("the assertion subject is null")).ContainsKey("Authorization"));
        Assert.Equal(McpServerTransportKind.Http, httpDefinition.Kind);

        Assert.ThrowsAny<JsonException>(() =>
            {
                using var unknown = JsonDocument.Parse("""{ "type": "tcp", "url": "https://example.com" }""");
                return unknown.RootElement.Deserialize(McpJsonContext.Default.McpTransportDefinition);
            });
    }

    private static McpServerDefinition CreateStdioDefinition(bool rootsEnabled = false, bool elicitationEnabled = false)
    {
        var transport = new StdioMcpTransportDefinition(
            "unused",
            [],
            null,
            new Dictionary<string, string?>());
        return new McpServerDefinition(
            "test",
            transport,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            rootsEnabled,
            elicitationEnabled,
            McpServerDefinition.CreateGenerationKey(
                transport,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                rootsEnabled,
                elicitationEnabled));
    }

    private sealed class StubRootsSource(string? rootPath) : IMcpWorkspaceRootsSource
    {
        public string? GetRootPath() => rootPath;
    }

    [Fact(Timeout = 30_000)]
    public async Task RootsCapabilityExposesTheLiveWorkspaceRoot()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var workspace = TemporaryWorkspace.Create();
        await using var serverFactory = new StreamServerFactory(requestRoots: true);
        var generation = await McpServerGeneration.CreateAsync(
            CreateStdioDefinition(rootsEnabled: true),
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync,
            rootsSource: new StubRootsSource(workspace.Path));

        var acquired = generation.TryAcquire();
        Assert.NotNull(acquired);
        var lease = acquired;
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "roots" });

        var result = await lease.Tools.Single().InvokeAsync(arguments, deadline.Token);
        await lease.DisposeAsync();

        var resultElement = Assert.IsType<JsonElement>(result);
        var echoed = resultElement.GetProperty("structuredContent").GetProperty("value").GetString();
        // The client answered the server's roots query with the live workspace root, so the
        // server-side tool observed it mid-call. Compare URIs: the wire form is a file URI,
        // which is not byte-identical to the platform path on Windows.
        Assert.StartsWith("roots:file://", echoed ?? throw new InvalidOperationException("the assertion subject is null"));
        Assert.Contains(new Uri(Path.GetFullPath(workspace.Path)).AbsoluteUri, echoed);
        await generation.Retire().WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task ServersWithoutRootsEnabledCannotRequestRoots()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory(requestRoots: true);
        var generation = await McpServerGeneration.CreateAsync(
            CreateStdioDefinition(rootsEnabled: false),
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync,
            rootsSource: new StubRootsSource(Directory.GetCurrentDirectory()));

        var acquired = generation.TryAcquire();
        Assert.NotNull(acquired);
        var lease = acquired;
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "roots" });

        var result = await lease.Tools.Single().InvokeAsync(arguments, deadline.Token);
        await lease.DisposeAsync();

        // No capability was declared, so the server-side roots request fails and surfaces as
        // a tool error instead of a client answer.
        var resultElement = Assert.IsType<JsonElement>(result);
        Assert.True(resultElement.TryGetProperty("isError", out var isError));
        Assert.True(isError.GetBoolean());
        await generation.Retire().WaitAsync(deadline.Token);
    }

    private sealed class StubElicitationPresenter(Func<McpElicitationRequest, McpElicitationAnswer> answer)
        : IMcpElicitationPresenter
    {
        public ValueTask<McpElicitationAnswer> PresentAsync(
            McpElicitationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(answer(request));
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ElicitationRequestsRouteThroughThePresenterAndMapBack()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        McpElicitationRequest? presented = null;
        await using var serverFactory = new StreamServerFactory(elicit: true);
        var generation = await McpServerGeneration.CreateAsync(
            CreateStdioDefinition(elicitationEnabled: true),
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync,
            elicitationPresenter: new StubElicitationPresenter(request =>
            {
                presented = request;
                return new McpElicitationAnswer("accept", "{\"token\":\"t0k\"}");
            }));

        var acquired = generation.TryAcquire();
        Assert.NotNull(acquired);
        var lease = acquired;
        // Attribution rides the AgentToolContext the run attaches to every tool call; a call
        // without one is a script-tool invocation and cannot be attributed.
        var context = new AgentToolContext(
            AgentSessionId.Create(),
            AgentRunId.Create(),
            AgentToolCallId.Create(),
            (_, _) => ValueTask.CompletedTask);
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "echo" })
        {
            Context = new Dictionary<object, object?> { [typeof(AgentToolContext)] = context }
        };

        var result = await lease.Tools.Single().InvokeAsync(arguments, deadline.Token);
        await lease.DisposeAsync();

        var resultElement = Assert.IsType<JsonElement>(result);
        Assert.Equal("echo:accept:t0k", resultElement.GetProperty("structuredContent").GetProperty("value").GetString());
        Assert.NotNull(presented);
        Assert.Equal("echo token", presented!.Message);
        Assert.False(presented.Password);
        await generation.Retire().WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task ServersWithoutElicitationEnabledCannotElicit()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory(elicit: true);
        var generation = await McpServerGeneration.CreateAsync(
            CreateStdioDefinition(elicitationEnabled: false),
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync,
            elicitationPresenter: new StubElicitationPresenter(_ =>
                new McpElicitationAnswer("accept", "{}")));

        var acquired = generation.TryAcquire();
        Assert.NotNull(acquired);
        var lease = acquired;
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "echo" });

        var result = await lease.Tools.Single().InvokeAsync(arguments, deadline.Token);
        await lease.DisposeAsync();

        // No capability was declared, so the server-side elicitation fails and surfaces as a
        // tool error instead of a presenter round trip.
        var resultElement = Assert.IsType<JsonElement>(result);
        Assert.True(resultElement.TryGetProperty("isError", out var isError));
        Assert.True(isError.GetBoolean());
        await generation.Retire().WaitAsync(deadline.Token);
    }

    private sealed class StreamServerFactory(bool reportProgress = false, bool requestRoots = false, bool elicit = false)
        : IAsyncDisposable
    {
        private CancellationTokenSource lifetime = new();

        // The client-facing pipe ends are kept per connection: disposing the server does not
        // close them, and completing them is what makes the connected client observe a drop.
        private readonly List<(McpServer Server, Task Completion, PipeWriter ClientSend, PipeWriter ServerSend)> servers = [];

        /// <summary>Makes the next transport creation fail, exercising the supervisor's
        /// reconnect backoff without a real server fault.</summary>
        internal bool FailNextTransport { get; set; }

        /// <summary>Ends every live client connection (the supervisor's reconnect path) by
        /// completing the client-facing pipe ends and stopping the servers, while the factory
        /// keeps accepting replacement transports on a fresh lifetime so the reconnect can
        /// succeed.</summary>
        internal async Task DisconnectClientsAsync()
        {
            CancellationTokenSource previous = lifetime;
            lifetime = new CancellationTokenSource();
            await previous.CancelAsync().ConfigureAwait(false);
            previous.Dispose();

            // Sequential with the supervisor's reconnect attempts in these tests: a
            // replacement transport is created only after this returns.
            var disconnected = servers.ToArray();
            servers.Clear();
            foreach (var (server, _, clientSend, serverSend) in disconnected)
            {
                // ClientSend EOF fails the client's next outbound write; ServerSend EOF ends
                // its receive loop — the session completion the supervisor waits on.
                await clientSend.CompleteAsync();
                await serverSend.CompleteAsync();
                await server.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var (_, completion, _, _) in disconnected)
                try
                {
                    await completion.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Canceled with the replaced lifetime — the disconnect itself.
                }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            foreach (var (server, _, _, _) in servers) await server.DisposeAsync();

            foreach (var (_, completion, _, _) in servers)
                try
                {
                    await completion;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }

            lifetime.Dispose();
        }

        internal ValueTask<IClientTransport> CreateTransportAsync(
            McpServerDefinition definition,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextTransport)
            {
                FailNextTransport = false;
                throw new IOException("The test server factory simulated a transport failure.");
            }

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var serverTransport = new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                definition.Id,
                loggerFactory);
            // The AIFunction overload keeps the baked-in schema and never binds special
            // parameters, so the progress-reporting variant must be created from the delegate
            // for the SDK to inject its IProgress<ProgressNotificationValue> parameter.
            McpServerTool tool = reportProgress
                ? McpServerTool.Create(
                    (Func<string, IProgress<ProgressNotificationValue>, CancellationToken, Task<EchoResult>>)ProgressingEcho,
                    new McpServerToolCreateOptions
                    {
                        Name = "echo",
                        Description = "Echoes one value.",
                        UseStructuredContent = true
                    })
                : requestRoots
                    ? McpServerTool.Create(
                        (Func<string, McpServer, CancellationToken, Task<EchoResult>>)RootsEcho,
                        new McpServerToolCreateOptions
                        {
                            Name = "echo",
                            Description = "Echoes one value.",
                            UseStructuredContent = true
                        })
                : elicit
                    ? McpServerTool.Create(
                        (Func<string, McpServer, CancellationToken, Task<EchoResult>>)ElicitingEcho,
                        new McpServerToolCreateOptions
                        {
                            Name = "echo",
                            Description = "Echoes one value.",
                            UseStructuredContent = true
                        })
                    : McpServerTool.Create(
                        AIFunctionFactory.Create((string value) => new EchoResult(value), "echo", "Echoes one value."),
                        new McpServerToolCreateOptions { UseStructuredContent = true });
            var server = McpServer.Create(
                serverTransport,
                new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "test", Version = "1.0" },
                    ToolCollection = [tool]
                },
                loggerFactory);
            servers.Add((server, server.RunAsync(lifetime.Token), clientToServer.Writer, serverToClient.Writer));
            IClientTransport clientTransport = new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                loggerFactory);
            return ValueTask.FromResult(clientTransport);
        }

        private static async Task<EchoResult> ProgressingEcho(
            string value,
            IProgress<ProgressNotificationValue> progress,
            CancellationToken cancellationToken)
        {
            // The SDK processes each inbound message independently and disposes the per-call
            // progress registration when the response is processed, so a progress notification
            // that arrives simultaneously with the response races that disposal and can be
            // dropped (observed deterministically on slow CI runners). Real servers report
            // progress while the call is genuinely in flight, so the reports are spaced from
            // the response instead of sent back-to-back with it.
            progress.Report(new ProgressNotificationValue { Progress = 25, Total = 100, Message = "quarter" });
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            progress.Report(new ProgressNotificationValue { Progress = 100, Total = 100 });
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            return new EchoResult(value);
        }

        private static async Task<EchoResult> ElicitingEcho(
            string value,
            McpServer server,
            CancellationToken cancellationToken)
        {
            var result = await server.ElicitAsync(
                new ElicitRequestParams
                {
                    Message = "echo token",
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties =
                        {
                            ["token"] = new ElicitRequestParams.StringSchema { Description = "The token" }
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
            var token = result.Content is not null &&
                        result.Content.TryGetValue("token", out var tokenElement)
                ? tokenElement.GetString()
                : null;
            return new EchoResult($"{value}:{result.Action}:{token ?? "-"}");
        }

        private static async Task<EchoResult> RootsEcho(
            string value,
            McpServer server,
            CancellationToken cancellationToken)
        {
#pragma warning disable MCP9005
            var roots = await server.RequestRootsAsync(new ListRootsRequestParams(), cancellationToken)
                .ConfigureAwait(false);
            return new EchoResult($"{value}:{string.Join("|", roots.Roots.Select(static root => root.Uri))}");
#pragma warning restore MCP9005
        }
    }

    private sealed record EchoResult(string Value);
}