using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Frontend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>
///     Route-table-driven frontend authentication: every endpoint the frontend host
///     registers requires the per-process bearer token, and a path whose spelling
///     differs only in case resolves through the same case-insensitive routing and is
///     authenticated the same way as its canonical form. The fixture boots a bare
///     <c>WebApplication</c> and maps the real <see cref="FrontendHost"/> onto it, so the
///     registered endpoint table contains exactly the frontend API surface and a newly
///     added route is exercised without maintaining a second list here.
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendRouteAuthenticationTests
{
    /// <summary>Substituted for every route parameter; a well-formed session id shape so
    /// the authentication outcome is observed before any handler-side validation.</summary>
    private const string ParameterPlaceholder = "00000000000000000000000000000000";

    [Fact(Timeout = 60_000)]
    public async Task EveryRegisteredFrontendEndpointRequiresAuthentication()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(50));
        await using var fixture = await FrontendFixture.StartAsync(deadline.Token);

        var endpoints = fixture.EnumerateRouteEndpoints();
        Assert.NotEmpty(endpoints);

        // The routes this review found unauthenticated must stay in the table, so a
        // fixture regression cannot quietly vacate the probe loop below.
        var routeTexts = endpoints
            .Select(static endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var route in new[]
                 {
                     "/v1/skills",
                     "/v1/prompts",
                     "/v1/plugins",
                     "/v1/plugins/{pluginId}/form",
                     "/v1/agent/session",
                     "/v1/agent/sessions/{sessionId}/events",
                 })
        {
            Assert.Contains(route, routeTexts);
        }

        var unauthenticated = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var rawText = endpoint.RoutePattern.RawText;
            if (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methodMetadata)
            {
                throw new InvalidOperationException($"Route '{rawText}' carries no HTTP method metadata.");
            }

            var httpMethod = Assert.Single(methodMetadata.HttpMethods);
            var probePath = BuildProbePath(rawText);
            using var request = new HttpRequestMessage(new HttpMethod(httpMethod), probePath);
            using var response = await fixture.Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                unauthenticated.Add($"{httpMethod} {probePath} -> {(int)response.StatusCode}");
            }
        }

        Assert.Empty(unauthenticated);
    }

    [Fact(Timeout = 60_000)]
    public async Task CaseVariantPathsAreAuthenticatedLikeTheirCanonicalRoutes()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(50));
        await using var fixture = await FrontendFixture.StartAsync(deadline.Token);

        var paths = new[] { "/V1/agent/session", "/v1/Agent/session", "/V1/SKILLS" };

        var bypassed = new List<string>();
        foreach (var path in paths)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await fixture.Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                bypassed.Add($"GET {path} -> {(int)response.StatusCode}");
            }
        }

        Assert.Empty(bypassed);

        // The same variants are served with a valid token: the 401s above come from the
        // authentication gate, not from endpoint routing failing to match the path.
        foreach (var path in paths)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);
            using var response = await fixture.Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            Assert.True(
                response.IsSuccessStatusCode,
                $"GET {path} with a valid token answered {(int)response.StatusCode}.");
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task EventsWebSocketAcceptsTheTokenAsAQueryParameter()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(50));
        await using var fixture = await FrontendFixture.StartAsync(deadline.Token);
        var sessionId = await fixture.CreateForegroundSessionAsync(deadline.Token);

        // The browser-standard WebSocket client API cannot set headers: the events route
        // must keep accepting the token from the query string, with no Authorization header.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{fixture.Port}/v1/agent/sessions/{sessionId}/events?token={fixture.Token}"),
            deadline.Token);
        Assert.Equal("hello", await ReceiveTextFrameTypeAsync(socket, deadline.Token));

        // And the same route still rejects a fully anonymous connect.
        using var anonymous = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => anonymous.ConnectAsync(
            new Uri($"ws://127.0.0.1:{fixture.Port}/v1/agent/sessions/{sessionId}/events"),
            deadline.Token));
    }

    /// <summary>Replaces every route parameter with the placeholder; authentication runs
    /// before handler binding, so the substitution only needs to route.</summary>
    private static string BuildProbePath(string? rawText)
    {
        if (rawText is null)
        {
            throw new InvalidOperationException("A frontend route carries no raw pattern text.");
        }

        var segments = rawText.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].StartsWith('{') && segments[index].EndsWith('}'))
            {
                segments[index] = ParameterPlaceholder;
            }
        }

        return "/" + string.Join('/', segments.Where(static segment => segment.Length > 0));
    }

    private static async Task<string> ReceiveTextFrameTypeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        using var document = JsonDocument.Parse(message.ToArray());
        return document.RootElement.GetProperty("type").GetString()
            ?? throw new InvalidOperationException("The events frame carried no type.");
    }

    /// <summary>A bare <c>WebApplication</c> hosting the real frontend endpoint table with
    /// the product token options, an in-memory session graph, and no control plane: the
    /// registered routes are exactly the frontend API surface under test.</summary>
    private sealed class FrontendFixture : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly FrontendOptions options;
        private readonly FrontendHost host;
        private readonly MaieuticsAgentSessionManager manager;
        private readonly HttpClient client;

        private FrontendFixture(
            WebApplication application,
            FrontendOptions options,
            FrontendHost host,
            MaieuticsAgentSessionManager manager)
        {
            this.application = application;
            this.options = options;
            this.host = host;
            this.manager = manager;
            // No default Authorization header: the anonymous probes must reach the
            // gate without a token, so callers attach credentials per request.
            client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{options.Port}") };
        }

        public HttpClient Client => client;

        public string Token => options.Token;

        public int Port => options.Port;

        public IReadOnlyList<RouteEndpoint> EnumerateRouteEndpoints() =>
            ((IEndpointRouteBuilder)application).DataSources
                .SelectMany(static source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .ToArray();

        public async Task<string> CreateForegroundSessionAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/agent/session");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            Assert.True(
                response.IsSuccessStatusCode,
                $"The foreground session could not be described: {(int)response.StatusCode}.");
            var session = await response.Content.ReadFromJsonAsync(
                    FrontendJsonContext.Default.FrontendSessionInfo, cancellationToken)
                ?? throw new InvalidOperationException("The session description body was empty.");
            return session.Id;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await application.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
            manager.Dispose();
            await application.DisposeAsync();
        }

        public static async Task<FrontendFixture> StartAsync(CancellationToken cancellationToken)
        {
            // FrontendOptions.Create reserves the loopback port Kestrel binds below, the
            // same arrangement the executable's composition root uses; the discovery file
            // is only a token carrier here and is never written.
            var options = FrontendOptions.Create(Path.Combine(
                Path.GetTempPath(), $"maieutics-frontend-route-auth-{Guid.NewGuid():N}.json"));

            var manager = new MaieuticsAgentSessionManager(
                new FixedProfileProvider(new AgentRunProfile(
                    new StubChatClient(), new AgentSessionOptions())),
                familiesRoot: null,
                storeFactory: null,
                NullLogger<MaieuticsAgentSessionManager>.Instance);
            var service = new FrontendSessionService(
                manager,
                new MaieuticsCommandExecutor(manager, null, null, null, null),
                new FrontendDenoReplPresentationRouter(),
                NullLogger<FrontendSessionService>.Instance);
            var turnQueue = new FrontendTurnQueue(
                service, manager, NullLogger<FrontendTurnQueue>.Instance);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));
            var application = builder.Build();
            var host = new FrontendHost(
                options, service, turnQueue, NullLogger<FrontendHost>.Instance);
            host.MapEndpoints(application);
            application.UseWebSockets();
            await application.StartAsync(cancellationToken);
            return new FrontendFixture(application, options, host, manager);
        }
    }


}
