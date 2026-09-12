using System.IO.Pipelines;
using System.Text;
using FluentAssertions;
using Maieutics.Execution;
using Maieutics.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maieutics.Product.Tests;

/// <summary>End-to-end resource tests over an in-proc MCP server: the provider's
/// catalog, claims, template reads, escape hatch, and typed failures all run against
/// a real connection generation (ADR 0026 decision 3).</summary>
public sealed class McpResourceIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task GenerationRefreshesResourcesAlongsideTools()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await ResourceServerHarness.StartAsync(lifetime.Token);
        var generation = harness.Generation;

        var catalog = generation.GetResourceCatalog();
        catalog.Resources.Should().ContainSingle().Which.Uri.Should().Be("test://static/hello");
        catalog.Templates.Should().ContainSingle().Which.UriTemplate.Should().Be("test://users/{id}");

        var provider = new McpResourceProvider(() => harness.Source);
        provider.Claims.Should().Contain(claim => claim.Scheme == "mcp" && claim.Authority == null);
        provider.Claims.Should().Contain(claim => claim.Scheme == "test" && claim.Authority == "static");
        provider.Claims.Should().Contain(claim => claim.Scheme == "test" && claim.Authority == "users");

        var entries = await provider.ListAsync(lifetime.Token);
        entries.Should().HaveCount(2);
        entries.Should().Contain(entry => entry.Kind == "resource" && entry.Uri == "test://static/hello");
        entries.Should().Contain(entry => entry.Kind == "template");
    }

    [Fact(Timeout = 30_000)]
    public async Task ProviderReadsExactAndTemplateResources()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await ResourceServerHarness.StartAsync(lifetime.Token);
        var provider = new McpResourceProvider(() => harness.Source);

        var exact = await provider.ReadAsync(
            "test://static/hello",
            new ResourceReadRequest(1024),
            lifetime.Token);
        using (exact.Content)
        {
            exact.MimeType.Should().Be("text/plain");
            using var buffered = new MemoryStream();
            exact.Content.CopyTo(buffered);
            Encoding.UTF8.GetString(buffered.ToArray()).Should().Be("hello content");
        }

        var templated = await provider.ReadAsync(
            "test://users/42",
            new ResourceReadRequest(1024),
            lifetime.Token);
        using (templated.Content)
        {
            using var buffered = new MemoryStream();
            templated.Content.CopyTo(buffered);
            Encoding.UTF8.GetString(buffered.ToArray()).Should().Be("user 42");
        }

        Func<Task> missing = async () => await provider.ReadAsync(
            "test://absent",
            new ResourceReadRequest(1024),
            lifetime.Token);
        var assertions = await missing.Should().ThrowAsync<ResourceException>();
        assertions.Which.Code.Should().Be("resource_not_found");

        var escape = await provider.ReadAsync(
            $"mcp://{harness.ServerId}/{Uri.EscapeDataString("test://static/hello")}",
            new ResourceReadRequest(1024),
            lifetime.Token);
        using (escape.Content)
        {
            using var buffered = new MemoryStream();
            escape.Content.CopyTo(buffered);
            Encoding.UTF8.GetString(buffered.ToArray()).Should().Be("hello content");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task RegistryResolvesWorkspaceBeforeMcpAndServesEscapeHatch()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var workspace = TemporaryWorkspace.Create();
        await using var harness = await ResourceServerHarness.StartAsync(lifetime.Token);
        var registry = new ResourceRegistry([
            new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path)),
            new McpResourceProvider(() => harness.Source)
        ]);

        ResourceRegistry.TryParseUri("workspace://local/", out var root).Should().BeTrue();
        registry.Resolve(root).Should().BeOfType<WorkspaceResourceProvider>();

        ResourceRegistry.TryParseUri("test://static/hello", out var resource).Should().BeTrue();
        registry.Resolve(resource).Should().BeOfType<McpResourceProvider>();

        ResourceRegistry.TryParseUri("mcp://other/test://x", out var escape).Should().BeTrue();
        registry.Resolve(escape).Should().BeOfType<McpResourceProvider>();

        var conflicts = registry.GetConflicts();
        conflicts.Should().BeEmpty("the workspace and mcp claims never overlap");
    }

    private sealed class ResourceServerHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime;
        private readonly McpServerGeneration generation;
        private readonly Task serverTask;

        private ResourceServerHarness(
            CancellationTokenSource lifetime,
            McpServerGeneration generation,
            Task serverTask,
            CatalogSource catalogSource)
        {
            this.lifetime = lifetime;
            this.generation = generation;
            this.serverTask = serverTask;
            this.catalogSource = catalogSource;
            Source = catalogSource;
        }

        internal string ServerId => "test-server";

        internal McpServerGeneration Generation => generation;

        internal IMcpResourceCatalogSource Source { get; }

        private readonly CatalogSource catalogSource;

        public async ValueTask DisposeAsync()
        {
            var retirement = generation.Retire();
            lifetime.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }

            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            lifetime.Dispose();
        }

        internal static async Task<ResourceServerHarness> StartAsync(CancellationToken cancellationToken)
        {
            var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var serverTransport = new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                "test-server",
                NullLoggerFactory.Instance);

            var server = McpServer.Create(
                serverTransport,
                new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "resource-test", Version = "1.0" },
                    // The client refreshes tools on every connection, so the server must
                    // advertise the tools capability even in a resources-focused test.
                    ToolCollection =
                    [
                        McpServerTool.Create(
                            AIFunctionFactory.Create((string value) => value, "echo", "Echoes one value."))
                    ],
                    Handlers = new McpServerHandlers
                    {
                        ListResourcesHandler = (request, ct) => ValueTask.FromResult(
                            new ListResourcesResult
                            {
                                Resources =
                                [
                                    new Resource
                                    {
                                        Uri = "test://static/hello",
                                        Name = "hello",
                                        MimeType = "text/plain"
                                    }
                                ]
                            }),
                        ListResourceTemplatesHandler = (request, ct) =>
                            ValueTask.FromResult(
                                new ListResourceTemplatesResult
                                {
                                    ResourceTemplates =
                                    [
                                        new ResourceTemplate
                                        {
                                            UriTemplate = "test://users/{id}",
                                            Name = "users",
                                            MimeType = "text/plain"
                                        }
                                    ]
                                }),
                        ReadResourceHandler = (request, ct) => ValueTask.FromResult(
                            new ReadResourceResult
                            {
                                Contents =
                                [
                                    new TextResourceContents
                                    {
                                        Uri = request.Params!.Uri!,
                                        Text = request.Params.Uri!.StartsWith("test://users/", StringComparison.Ordinal)
                                            ? $"user {request.Params.Uri["test://users/".Length..]}"
                                            : "hello content",
                                        MimeType = "text/plain"
                                    }
                                ]
                            })
                    }
                },
                NullLoggerFactory.Instance,
                null);
            var serverTask = server.RunAsync(linked.Token);

            var transportDefinition = new StdioMcpTransportDefinition("in-proc", [], null, null);
            var definition = new McpServerDefinition(
                "test-server",
                transportDefinition,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                McpServerDefinition.CreateGenerationKey(
                    transportDefinition,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.Zero));
            var generation = await McpServerGeneration.CreateAsync(
                definition,
                NullLoggerFactory.Instance,
                TimeProvider.System,
                linked.Token,
                (_, _, _) => ValueTask.FromResult<IClientTransport>(new StreamClientTransport(
                    clientToServer.Writer.AsStream(),
                    serverToClient.Reader.AsStream(),
                    NullLoggerFactory.Instance)));

            var source = new CatalogSource();
            source.Attach(generation);
            return new ResourceServerHarness(lifetime, generation, serverTask, source);
        }

        private sealed class CatalogSource : IMcpResourceCatalogSource
        {
            private volatile McpServerGeneration? generation;

            internal void Attach(McpServerGeneration generation)
            {
                this.generation = generation;
            }

            public IReadOnlyList<McpResourceServerAccess> GetResourceServers()
            {
                return generation is { } value
                    ? [new McpResourceServerAccess("test-server", value.GetResourceCatalog(), value)]
                    : [];
            }
        }
    }
}
