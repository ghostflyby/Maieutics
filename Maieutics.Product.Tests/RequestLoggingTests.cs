using System.Net;
using System.Net.Sockets;
using Maieutics.Configuration;
using Maieutics.Control;
using Maieutics.Execution;
using Microsoft.Extensions.Logging;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class RequestLoggingTests
{
    [Fact(Timeout = 60_000)]
    public async Task RejectedFrontendPathCannotInsertLogLinesOrTerminalControls()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        using var area = TempArea.Create("request-logging-tests");
        using var logs = new FileLoggerProvider(area.Path);
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            deadline.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted),
            hanging: false,
            configureBuilder: builder =>
            {
                builder.Logging.AddProvider(logs);
                builder.Logging.AddFilter("Maieutics", LogLevel.Debug);
            });

        const string path = "/v1/objects/bad%0D%0AFORGED%1B%5B2J%E2%80%A8tail";
        using var response = await harness.Client.GetAsync(path, deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", await response.Content.ReadAsStringAsync(deadline.Token));

        // The rejection is logged synchronously before its response is written.
        // Close the writer before reading so the assertion also works with Windows file sharing.
        logs.Dispose();
        var lines = File.ReadAllLines(Path.Combine(area.Path, $"maieutics-{Environment.ProcessId}.log"));
        var rejection = Assert.Single(lines, line => line.Contains("Frontend route GET /v1/objects/", StringComparison.Ordinal));
        Assert.EndsWith($"Frontend route GET {path} rejected with invalid_request.", rejection);
        Assert.DoesNotContain(lines, line => line.StartsWith("FORGED", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', rejection);
        Assert.DoesNotContain('\u2028', rejection);
    }

    [Fact(Timeout = 30_000)]
    public async Task FailedResourceReadEscapesLogFieldsWithoutChangingTheProviderUri()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The control host test harness authenticates this request over a Unix domain socket.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var area = TempArea.Create("resource-logging-tests");
        using var logs = new FileLoggerProvider(area.Path);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "log-test");
        var provider = new FailingResourceProvider();
        using var host = new ReplControlHost(
            ReplControlHost.CreateSocketPath(), registry, factory.CreateLogger<ReplControlHost>(),
            resources: new ResourceRegistry([provider]));
        await using var application = await ReplControlTestHost.StartAsync(host.SocketPath, host, deadline.Token);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(host.SocketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        });
        const string uri = "log-test://local/a\r\nFORGED\u001b[2J\u2028tail\\n";
        using var response = await client.GetAsync(
            $"http://localhost/v1/resource?uri={Uri.EscapeDataString(uri)}", deadline.Token);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("resource_provider_failed", await response.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal(uri, provider.LastUri);

        logs.Dispose();
        var lines = File.ReadAllLines(Path.Combine(area.Path, $"maieutics-{Environment.ProcessId}.log"));
        var failure = Assert.Single(lines, line => line.Contains("Resource read for '", StringComparison.Ordinal));
        Assert.EndsWith(
            "Resource read for 'log-test://local/a\\r\\nFORGED\\u001b[2J\\u2028tail\\\\n' via provider broken\\nprovider failed.",
            failure);
        Assert.DoesNotContain(lines, line => line.StartsWith("FORGED", StringComparison.Ordinal));
    }

    private sealed class FailingResourceProvider : IResourceProvider
    {
        public string Id => "broken\nprovider";
        public ResourceProviderClass Class => ResourceProviderClass.Custom;
        public IReadOnlyList<ResourceClaim> Claims => [new("log-test", "local")];
        internal string? LastUri { get; private set; }

        public ValueTask<ResourceReadResult> ReadAsync(
            string uri, ResourceReadRequest request, CancellationToken cancellationToken)
        {
            LastUri = uri;
            throw new InvalidOperationException("The test provider failed.");
        }
    }
}
