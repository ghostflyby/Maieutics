using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Mcp;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        check.Should().Throw<ArgumentException>()
            .WithMessage("*not permitted by the effective policy*");
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

        check.Should().Throw<ArgumentException>()
            .WithMessage("*not permitted by the effective policy*");
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
        acquired.Should().NotBeNull();
        var lease = acquired;
        lease.Tools.Should().ContainSingle().Which.Name.Should().Be("echo");
        using var argumentsDocument = JsonDocument.Parse("{\"value\":\"hello\"}");
        var arguments = new AIFunctionArguments(argumentsDocument.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));

        var result = await lease.Tools.Single().InvokeAsync(arguments, deadline.Token);

        var resultElement = result.Should().BeOfType<JsonElement>().Subject;
        resultElement.TryGetProperty("isError", out _).Should().BeFalse();
        resultElement.GetProperty("structuredContent").GetProperty("value").GetString().Should().Be("hello");
        generation.GetInfo().Tools.Should().ContainSingle().Which.Should().Be(
            new MaieuticsMcpToolInfo("echo", "echo", true));
        var retirement = generation.Retire();
        retirement.IsCompleted.Should().BeFalse();
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
        acquired.Should().NotBeNull();
        acquired.Tools.Should().BeEmpty();
        generation.GetInfo().Tools.Should().ContainSingle().Which.Should().Be(
            new MaieuticsMcpToolInfo("echo", "echo", false));
        var retirement = generation.Retire();
        await acquired.DisposeAsync();
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
        acquired.Should().NotBeNull();
        var lease = acquired;
        var reported = new List<JsonElement>();
        var bothForwarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = CreateProgressContext((content, _) =>
        {
            var data = content.Should().BeOfType<DataContent>().Subject;
            data.MediaType.Should().Be("application/json");
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
        await bothForwarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await lease.DisposeAsync();

        result.Should().BeOfType<JsonElement>();
        reported.Should().HaveCount(2);
        // The SDK dispatches notifications on the thread pool, so delivery order between two
        // reports is not guaranteed; the forwarding chain only serializes the writes.
        var ordered = reported.OrderBy(static element => element.GetProperty("progress").GetDouble()).ToArray();
        ordered[0].GetProperty("progress").GetDouble().Should().Be(25);
        ordered[0].GetProperty("total").GetDouble().Should().Be(100);
        ordered[0].GetProperty("message").GetString().Should().Be("quarter");
        ordered[1].GetProperty("progress").GetDouble().Should().Be(100);
        ordered[1].TryGetProperty("message", out _).Should().BeFalse();
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
        acquired.Should().NotBeNull();
        var lease = acquired;
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "hello" });

        var result = await lease.Tools.Single().InvokeAsync(arguments, deadline.Token);
        await lease.DisposeAsync();

        var resultElement = result.Should().BeOfType<JsonElement>().Subject;
        resultElement.GetProperty("structuredContent").GetProperty("value").GetString().Should().Be("hello");
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

        calls.Should().Be(2);
        payloads.Should().Equal(1d);
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
        var stdioDefinition = stdio.RootElement
            .Deserialize(McpJsonContext.Default.McpTransportDefinition)
            .Should()
            .BeOfType<StdioMcpTransportDefinition>()
            .Subject;
        stdioDefinition.Command.Should().Be("deno");
        stdioDefinition.Arguments.Should().Equal("run", "server.ts");
        stdioDefinition.EnvironmentVariables.Should().ContainKey("PORT").WhoseValue.Should().Be("8080");
        stdioDefinition.Kind.Should().Be(McpServerTransportKind.Stdio);

        using var http = JsonDocument.Parse("""
                                            {
                                              "type": "http",
                                              "url": "https://example.com/mcp",
                                              "headers": { "Authorization": "Bearer token" }
                                            }
                                            """);
        var httpDefinition = http.RootElement
            .Deserialize(McpJsonContext.Default.McpTransportDefinition)
            .Should()
            .BeOfType<HttpMcpTransportDefinition>()
            .Subject;
        httpDefinition.Endpoint.Should().Be(new Uri("https://example.com/mcp"));
        httpDefinition.Headers.Should().ContainKey("Authorization");
        httpDefinition.Kind.Should().Be(McpServerTransportKind.Http);

        FluentActions.Invoking(() =>
            {
                using var unknown = JsonDocument.Parse("""{ "type": "tcp", "url": "https://example.com" }""");
                return unknown.RootElement.Deserialize(McpJsonContext.Default.McpTransportDefinition);
            })
            .Should()
            .Throw<JsonException>();
    }

    private static McpServerDefinition CreateStdioDefinition()
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
            McpServerDefinition.CreateGenerationKey(
                transport,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero));
    }

    private sealed class StreamServerFactory(bool reportProgress = false) : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<(McpServer Server, Task Completion)> servers = [];

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            foreach (var (server, _) in servers) await server.DisposeAsync();

            foreach (var (_, completion) in servers)
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
                    (Func<string, IProgress<ProgressNotificationValue>, EchoResult>)ProgressingEcho,
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
                loggerFactory,
                null);
            servers.Add((server, server.RunAsync(lifetime.Token)));
            IClientTransport clientTransport = new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                loggerFactory);
            return ValueTask.FromResult(clientTransport);
        }

        private static EchoResult ProgressingEcho(string value, IProgress<ProgressNotificationValue> progress)
        {
            progress.Report(new ProgressNotificationValue { Progress = 25, Total = 100, Message = "quarter" });
            progress.Report(new ProgressNotificationValue { Progress = 100, Total = 100 });
            return new EchoResult(value);
        }
    }

    private sealed record EchoResult(string Value);
}