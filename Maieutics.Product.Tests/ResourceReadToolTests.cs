using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.Execution;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

public sealed class ResourceReadToolTests
{
    [Fact]
    public async Task ReadTextFollowsRegisteredProvidersThroughTheSharedEntry()
    {
        using var workspace = TemporaryWorkspace.Create();
        var registry = new ResourceRegistry([
            new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path)),
            new FakeProvider([
                new ResourceCatalogEntry("fake", "notes://team/idea", "idea", null, "text/plain", "resource", null)
            ])
        ]);
        var functions = CreateFunctions(workspace.Path, registry);
        var readText = Function(functions, "read_text");

        var result = Result<ReadTextResult>(await InvokeAsync(
                readText,
                """{"uri":"notes://team/idea","startLine":2,"maxLines":2}"""),
            WorkspaceJsonSerializerContext.Default.ReadTextResult);
        result.Uri.Should().Be("notes://team/idea");
        result.StartLine.Should().Be(2);
        result.EndLine.Should().Be(3);
        result.Text.Should().Be("second\nthird");
        result.Truncated.Should().BeFalse();
        result.NextStartLine.Should().BeNull();
    }

    [Fact]
    public async Task ReadTextKeepsWorkspaceSemanticsWhenRegistryIsPresent()
    {
        using var workspace = TemporaryWorkspace.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "a.txt"),
            "first\nsecond\nthird",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        var registry = new ResourceRegistry([
            new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path))
        ]);
        var functions = CreateFunctions(workspace.Path, registry);

        var result = Result<ReadTextResult>(await InvokeAsync(
                Function(functions, "read_text"),
                """{"uri":"workspace://local/a.txt"}"""),
            WorkspaceJsonSerializerContext.Default.ReadTextResult);
        result.Uri.Should().Be("workspace://local/a.txt");
        result.Text.Should().Be("first\nsecond\nthird");

        var missing = await InvokeAsync(
            Function(functions, "read_text"),
            """{"uri":"workspace://local/absent.txt"}""");
        ShouldFailure(missing).Code.Should().Be("workspace_path_not_found");
    }

    [Fact]
    public async Task ReadTextSurfacesTypedResourceFailures()
    {
        using var workspace = TemporaryWorkspace.Create();
        var registry = new ResourceRegistry([
            new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path))
        ]);
        var functions = CreateFunctions(workspace.Path, registry);
        var readText = Function(functions, "read_text");

        var unknown = await InvokeAsync(readText, """{"uri":"mystery://a/b"}""");
        ShouldFailure(unknown).Code.Should().Be("resource_unknown_scheme");

        var invalid = await InvokeAsync(readText, """{"uri":"notes://a/b#frag"}""");
        ShouldFailure(invalid).Code.Should().Be("resource_invalid_uri");

        var relative = await InvokeAsync(readText, """{"uri":"just/a/path"}""");
        ShouldFailure(relative).Code.Should().Be("resource_invalid_uri");
    }

    [Fact]
    public void ReadTextWithoutRegistryKeepsTheWorkspaceOnlyContract()
    {
        using var workspace = TemporaryWorkspace.Create();
        var functions = CreateFunctions(workspace.Path);
        var description = Function(functions, "read_text").Description;
        description.Should().NotBeNull();

        var check = () => Function(functions, "list_resources");
        check.Should().Throw<InvalidOperationException>("the tool does not exist without a registry");
    }

    [Fact]
    public async Task ListResourcesMergesCatalogsAndConflicts()
    {
        using var workspace = TemporaryWorkspace.Create();
        var catalogProvider = new FakeProvider([
            new ResourceCatalogEntry("fake", "notes://a", "a", null, null, "resource", null),
            new ResourceCatalogEntry("fake", "notes://b/{id}", "b", null, null, "template", "notes://b/{id}")
        ]);
        var shadowed = new FakeProvider(
            [new ResourceCatalogEntry("shadowed", "notes://shadow", null, null, null, "resource", null)],
            "shadowed");
        var registry = new ResourceRegistry([
            new WorkspaceResourceProvider(Workspace.Create(workspace.Path, workspace.Path)),
            catalogProvider,
            shadowed
        ]);
        var functions = new ResourceFunctions(registry);

        var result = Result<ResourceCatalogResult>(await InvokeAsync(
                functions.Functions.Single(),
                "{}"),
            ResourceJsonSerializerContext.Default.ResourceCatalogResult);
        result.Resources.Should().HaveCount(3);
        result.Resources.Should().Contain(entry => entry.Kind == "resource" && entry.Uri == "notes://a");
        result.Resources.Should().Contain(entry => entry.Kind == "template" && entry.Template == "notes://b/{id}");
        result.Conflicts.Should().ContainSingle().Which.ProviderId.Should().Be("shadowed");
    }

    [Fact]
    public async Task ControlHostServesResourceBodiesAndTypedErrors()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The control host resource endpoint test rides a Unix domain socket.");

        using var workspace = TemporaryWorkspace.Create();
        var registry = new ResourceRegistry([
            new FakeProvider([
                new ResourceCatalogEntry("fake", "notes://team/idea", null, null, "text/plain", "resource", null)
            ])
        ]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry2 = new ReplControlSessionRegistry();
        // The test process itself is the peer behind the unix socket connection.
        registry2.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(
            registry2,
            cts.Token,
            resources: registry);
        await using var _ = application;

        using var client = CreateUnixSocketClient(host.SocketPath);
        var ok = await client.GetAsync(
            $"http://localhost/v1/resource?uri={Uri.EscapeDataString("notes://team/idea")}",
            cts.Token);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
        (await ok.Content.ReadAsStringAsync(cts.Token)).Should().Be("first\nsecond\nthird\n");

        var unknown = await client.GetAsync(
            $"http://localhost/v1/resource?uri={Uri.EscapeDataString("mystery://a/b")}",
            cts.Token);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var document = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(cts.Token));
        document.RootElement.GetProperty("code").GetString().Should().Be("resource_unknown_scheme");

        var missingParameter = await client.GetAsync("http://localhost/v1/resource", cts.Token);
        missingParameter.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Speaks HTTP over the control channel's unix socket exactly like a
    /// REPL child does.</summary>
    private static HttpClient CreateUnixSocketClient(string socketPath)
    {
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        return new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
        });
    }

    [Fact]
    public async Task ReadTextRejectsBinaryProviderBodies()
    {
        using var workspace = TemporaryWorkspace.Create();
        var registry = new ResourceRegistry([new FakeProvider([
            new ResourceCatalogEntry("fake", "notes://team/binary", null, null, null, "resource", null)
        ])]);
        var functions = CreateFunctions(workspace.Path, registry);
        var readText = Function(functions, "read_text");

        var binary = await InvokeAsync(
            readText,
            """{"uri":"notes://team/binary"}""");
        ShouldFailure(binary).Code.Should().Be("workspace_binary_file");
    }

    private static WorkspaceFunctions CreateFunctions(string root, ResourceRegistry? registry = null)
    {
        return new WorkspaceFunctions(Workspace.Create(root, root), resources: registry);
    }

    private static AIFunction Function(WorkspaceFunctions functions, string name)
    {
        return functions.Functions.Single(function => function.Name == name);
    }

    private static AIFunctionArguments Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));
    }

    private static async ValueTask<ToolInvocation> InvokeAsync(AIFunction function, string json)
    {
        try
        {
            var result = await function.InvokeAsync(Arguments(json), TestContext.Current.CancellationToken);
            return new ToolInvocation(true, result as JsonElement?);
        }
        catch (AgentToolException exception)
        {
            return new ToolInvocation(false, JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                code = exception.Code,
                message = exception.Message
            })).RootElement.Clone());
        }
    }

    private static T Result<T>(ToolInvocation invocation, JsonTypeInfo<T> typeInfo)
    {
        invocation.Succeeded.Should().BeTrue();
        return invocation.Result!.Value.Deserialize(typeInfo)!;
    }

    private static (string Code, string Message) ShouldFailure(ToolInvocation invocation)
    {
        invocation.Succeeded.Should().BeFalse();
        return (
            invocation.Result!.Value.GetProperty("code").GetString()!,
            invocation.Result.Value.GetProperty("message").GetString()!);
    }

    private readonly record struct ToolInvocation(bool Succeeded, JsonElement? Result);

    /// <summary>Serves deterministic text bodies for resource URIs and reports one
    /// catalog entry per configured URI.</summary>
    private sealed class FakeProvider(ImmutableArray<ResourceCatalogEntry> catalog, string id = "fake")
        : IResourceCatalogProvider
    {
        public string Id => id;

        public ResourceProviderClass Class => ResourceProviderClass.Custom;

        public IReadOnlyList<ResourceClaim> Claims { get; } = catalog
            .Select(static entry =>
            {
                var separator = entry.Uri.IndexOf(':');
                return separator > 0 ? new ResourceClaim(entry.Uri[..separator]) : null;
            })
            .OfType<ResourceClaim>()
            .Distinct()
            .ToList();

        public ValueTask<ResourceReadResult> ReadAsync(
            string uri,
            ResourceReadRequest request,
            CancellationToken cancellationToken)
        {
            var body = uri.Contains("binary", StringComparison.Ordinal)
                ? (byte[])[0x00, 0x01, 0x02]
                : "first\nsecond\nthird\n"u8.ToArray();
            return ValueTask.FromResult<ResourceReadResult>(
                new ResourceReadResult(new MemoryStream(body), "text/plain"));
        }

        public ValueTask<ImmutableArray<ResourceCatalogEntry>> ListAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(catalog);
        }
    }
}
