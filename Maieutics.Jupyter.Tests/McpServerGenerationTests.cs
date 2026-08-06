using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maieutics.Jupyter.Tests;

public sealed class McpServerGenerationTests
{
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
            reservedToolNames: new HashSet<string>(StringComparer.Ordinal) { "echo" });

        var acquired = generation.TryAcquire();
        acquired.Should().NotBeNull();
        acquired.Tools.Should().BeEmpty();
        generation.GetInfo().Tools.Should().ContainSingle().Which.Should().Be(
            new MaieuticsMcpToolInfo("echo", "echo", false));
        var retirement = generation.Retire();
        await acquired.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
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
        var stdioDefinition = JsonSerializer
            .Deserialize(stdio.RootElement, McpJsonContext.Default.McpTransportDefinition)
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
        var httpDefinition = JsonSerializer
            .Deserialize(http.RootElement, McpJsonContext.Default.McpTransportDefinition)
            .Should()
            .BeOfType<HttpMcpTransportDefinition>()
            .Subject;
        httpDefinition.Endpoint.Should().Be(new Uri("https://example.com/mcp"));
        httpDefinition.Headers.Should().ContainKey("Authorization");
        httpDefinition.Kind.Should().Be(McpServerTransportKind.Http);

        using var unknown = JsonDocument.Parse("""{ "type": "tcp", "url": "https://example.com" }""");
        var deserialize = () =>
            JsonSerializer.Deserialize(unknown.RootElement, McpJsonContext.Default.McpTransportDefinition);
        deserialize.Should().Throw<JsonException>();
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

    private sealed class StreamServerFactory : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<(McpServer Server, Task Completion)> servers = [];

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
            var function = AIFunctionFactory.Create(
                (string value) => new EchoResult(value),
                name: "echo",
                description: "Echoes one value.");
            var server = McpServer.Create(
                serverTransport,
                new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "test", Version = "1.0" },
                    ToolCollection =
                    [
                        McpServerTool.Create(
                            function,
                            new McpServerToolCreateOptions { UseStructuredContent = true })
                    ]
                },
                loggerFactory,
                serviceProvider: null);
            servers.Add((server, server.RunAsync(lifetime.Token)));
            IClientTransport clientTransport = new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                loggerFactory);
            return ValueTask.FromResult(clientTransport);
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            foreach (var (server, _) in servers)
            {
                await server.DisposeAsync();
            }

            foreach (var (_, completion) in servers)
            {
                try
                {
                    await completion;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
            }

            lifetime.Dispose();
        }
    }

    private sealed record EchoResult(string Value);
}
