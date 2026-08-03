using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maieutics.Jupyter.Tests;

public sealed class McpServerGenerationTests
{
    [Fact(Timeout = 30_000)]
    public async Task OfficialStreamServerDiscoversRenamesAndInvokesAllowlistedTool()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var serverFactory = new StreamServerFactory();
        var tools = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["echo"] = "remote_echo"
        };
        var definition = new McpServerDefinition(
            "test",
            McpServerTransportKind.Stdio,
            "unused",
            [],
            null,
            new Dictionary<string, string?>(),
            null,
            new Dictionary<string, string>(),
            tools,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            McpServerDefinition.CreateGenerationKey(
                McpServerTransportKind.Stdio,
                "unused",
                [],
                null,
                [],
                null,
                [],
                tools,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero));
        var generation = await McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            deadline.Token,
            serverFactory.CreateTransportAsync);

        var acquired = generation.TryAcquire();
        acquired.Should().NotBeNull();
        var lease = acquired!;
        lease.Tools.Should().ContainSingle().Which.Name.Should().Be("remote_echo");
        using var argumentsDocument = JsonDocument.Parse("{\"value\":\"hello\"}");
        var arguments = new AIFunctionArguments(argumentsDocument.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));

        var result = await lease.Tools.Single().InvokeAsync(arguments, deadline.Token);

        var resultElement = result.Should().BeOfType<JsonElement>().Subject;
        resultElement.TryGetProperty("isError", out _).Should().BeFalse();
        resultElement.GetProperty("structuredContent").GetProperty("value").GetString().Should().Be("hello");
        generation.GetInfo().Tools.Should().ContainSingle().Which.Should().Be(
            new MaieuticsMcpToolInfo("echo", "remote_echo", true));
        var retirement = generation.Retire();
        retirement.IsCompleted.Should().BeFalse();
        await lease.DisposeAsync();
        await retirement.WaitAsync(deadline.Token);
    }

    private sealed class StreamServerFactory : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<(McpServer Server, Task Completion)> servers = [];

        internal ValueTask<IClientTransport> CreateTransportAsync(
            McpServerDefinition definition,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
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
