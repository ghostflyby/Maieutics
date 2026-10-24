using System.IO.Pipelines;
using System.Text;
using Maieutics.Execution;
using Maieutics.Mcp;
using Microsoft.Extensions.AI;
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var harness = await ResourceServerHarness.StartAsync(deadline.Token);
        var generation = harness.Generation;

        var catalog = generation.GetResourceCatalog();
        Assert.Equal("test://static/hello", Assert.Single(catalog.Resources).Uri);
        Assert.Equal("test://users/{id}", Assert.Single(catalog.Templates).UriTemplate);

        // Hoisted: the harness source is a fixed object, and a local capture keeps the
        // factory lambda from referencing the using-scoped harness.
        var source = harness.Source;
        var provider = new McpResourceProvider(() => source);
        Assert.Contains(provider.Claims, claim => claim is { Scheme: "mcp", Authority: null });
        Assert.Contains(provider.Claims, claim => claim is { Scheme: "test", Authority: "static" });
        Assert.Contains(provider.Claims, claim => claim is { Scheme: "test", Authority: "users" });

        var entries = await provider.ListAsync(deadline.Token);
        Assert.Equal(2, entries.Count());
        Assert.Contains(entries, entry => entry is { Kind: "resource", Uri: "test://static/hello" });
        Assert.Contains(entries, entry => entry.Kind == "template");
    }

    [Fact(Timeout = 30_000)]
    public async Task ProviderReadsExactAndTemplateResources()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var harness = await ResourceServerHarness.StartAsync(deadline.Token);
        var source = harness.Source;
        var provider = new McpResourceProvider(() => source);

        var exact = await provider.ReadAsync(
            "test://static/hello",
            new ResourceReadRequest(1024),
            deadline.Token);
        await using (exact.Content)
        {
            Assert.Equal("text/plain", exact.MimeType);
            using var buffered = new MemoryStream();
            exact.Content.CopyTo(buffered);
            Assert.Equal("hello content", Encoding.UTF8.GetString(buffered.ToArray()));
        }

        var templated = await provider.ReadAsync(
            "test://users/42",
            new ResourceReadRequest(1024),
            deadline.Token);
        await using (templated.Content)
        {
            using var buffered = new MemoryStream();
            templated.Content.CopyTo(buffered);
            Assert.Equal("user 42", Encoding.UTF8.GetString(buffered.ToArray()));
        }

        var assertions = await Assert.ThrowsAnyAsync<ResourceException>(async () =>
        {
            await provider.ReadAsync(
                "test://absent",
                new ResourceReadRequest(1024),
                deadline.Token);
        });
        Assert.Equal("resource_not_found", assertions.Code);

        var escape = await provider.ReadAsync(
            $"mcp://{harness.ServerId}/{Uri.EscapeDataString("test://static/hello")}",
            new ResourceReadRequest(1024),
            deadline.Token);
        await using (escape.Content)
        {
            using var buffered = new MemoryStream();
            escape.Content.CopyTo(buffered);
            Assert.Equal("hello content", Encoding.UTF8.GetString(buffered.ToArray()));
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task RegistryResolvesWorkspaceBeforeMcpAndServesEscapeHatch()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var workspace = TemporaryWorkspace.Create();
        await using var harness = await ResourceServerHarness.StartAsync(deadline.Token);
        var source = harness.Source;
        var registry = new ResourceRegistry([
            new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path)),
            new McpResourceProvider(() => source)
        ]);

        Assert.True(ResourceRegistry.TryParseUri("workspace://local/", out var root));
        Assert.IsType<WorkspaceResourceProvider>(registry.Resolve(root));

        Assert.True(ResourceRegistry.TryParseUri("test://static/hello", out var resource));
        Assert.IsType<McpResourceProvider>(registry.Resolve(resource));

        Assert.True(ResourceRegistry.TryParseUri("mcp://other/test://x", out var escape));
        Assert.IsType<McpResourceProvider>(registry.Resolve(escape));

        var conflicts = registry.GetConflicts();
        Assert.Empty(conflicts);
    }

    private sealed class ResourceServerHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private readonly McpServerGeneration _generation;
        private readonly Task _serverTask;

        private ResourceServerHarness(
            CancellationTokenSource lifetime,
            McpServerGeneration generation,
            Task serverTask,
            CatalogSource catalogSource)
        {
            this._lifetime = lifetime;
            this._generation = generation;
            this._serverTask = serverTask;
            Source = catalogSource;
        }

        internal string ServerId => "test-server";

        internal McpServerGeneration Generation => _generation;

        internal IMcpResourceCatalogSource Source { get; }

        public async ValueTask DisposeAsync()
        {
            var retirement = _generation.Retire();
            await _lifetime.CancelAsync();
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }

            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            _lifetime.Dispose();
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
                        ListResourcesHandler = (_, _) => ValueTask.FromResult(
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
                        ListResourceTemplatesHandler = (_, _) =>
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
                        ReadResourceHandler = (request, _) => ValueTask.FromResult(
                            new ReadResourceResult
                            {
                                Contents =
                                [
                                    new TextResourceContents
                                    {
                                        Uri = request.Params.Uri,
                                        Text = request.Params.Uri.StartsWith("test://users/", StringComparison.Ordinal)
                                            ? $"user {request.Params.Uri["test://users/".Length..]}"
                                            : "hello content",
                                        MimeType = "text/plain"
                                    }
                                ]
                            })
                    }
                },
                NullLoggerFactory.Instance);
            var serverTask = server.RunAsync(linked.Token);

            var transportDefinition = new StdioMcpTransportDefinition("in-proc", []);
            var definition = new McpServerDefinition(
                "test-server",
                transportDefinition,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                false,
                false,
                McpServerDefinition.CreateGenerationKey(
                    transportDefinition,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.Zero,
                    rootsEnabled: false,
                    elicitationEnabled: false));
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
            private volatile McpServerGeneration? _currentGeneration;

            internal void Attach(McpServerGeneration generation)
            {
                _currentGeneration = generation;
            }

            public IReadOnlyList<McpResourceServerAccess> GetResourceServers()
            {
                return _currentGeneration is { } value
                    ? [new McpResourceServerAccess("test-server", value.GetResourceCatalog(), value)]
                    : [];
            }
        }
    }
}
