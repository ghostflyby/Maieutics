using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Jupyter.Shared;
using Maieutics.Providers.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class FrontendApiIntegrationTests
{
    private const string Answer = "streamed answer";

    [Fact(Timeout = 60_000)]
    public async Task DiscoveryFileIsPublishedAndRetired()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        var harness = await StartHostAsync(deadline.Token);
        try
        {
            var discovery = harness.Discovery;
            discovery.GetProperty("version").GetInt32().Should().Be(1);
            discovery.GetProperty("url").GetString().Should().StartWith("http://127.0.0.1:");
            discovery.GetProperty("token").GetString().Should().HaveLength(64);
            discovery.GetProperty("pid").GetInt32().Should().BeGreaterThan(0);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        // The discovery file must disappear with the process so a stale instance is never
        // rediscovered by a frontend.
        File.Exists(harness.DiscoveryPath).Should().BeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task FrontendEndpointsRejectMissingOrWrongBearerTokens()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var anonymous = new HttpClient { BaseAddress = new Uri(harness.Url) };
        using var forged = harness.CreateClient(token: "wrong");

        (await anonymous.GetAsync("/v1/agent/session", deadline.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await forged.GetAsync("/v1/agent/session", deadline.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/v1/status", deadline.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(Timeout = 60_000)]
    public async Task CapabilitiesAndSessionDescribeTheActiveSession()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var client = harness.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/agent/capabilities", deadline.Token);
        capabilities.GetProperty("protocolVersion").GetInt32().Should().Be(1);
        var session = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", deadline.Token);
        capabilities.GetProperty("session").GetProperty("id").GetString().Should()
            .Be(session.GetProperty("id").GetString())
            .And.HaveLength(32);
        session.GetProperty("turns").GetInt64().Should().Be(0);
        // The composition root wires the SQLite family store whenever ApplicationPaths
        // resolve, so the active session always reports persistence enabled.
        session.GetProperty("persistenceEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task TurnStreamsEventsAndCommitsTheTranscript()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        await using var harness = await StartHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
        var hello = await events.ReceiveFrameAsync(deadline.Token);
        hello.GetProperty("type").GetString().Should().Be("hello");
        hello.GetProperty("session").GetProperty("id").GetString().Should().Be(sessionId);

        var runId = await harness.SubmitTurnAsync(sessionId, "hello", deadline.Token);
        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);

        var types = frames.Select(frame => frame.GetProperty("type").GetString()).ToArray();
        types.Should().StartWith(["run.started", "run.status"]);
        types.Should().EndWith(["message.completed", "run.completed", "run.status"]);
        types.Should().Contain("text.delta");
        frames[^2].GetProperty("truncated").GetBoolean().Should().BeFalse();
        var statuses = frames
            .Where(frame => frame.GetProperty("type").GetString() == "run.status")
            .Select(frame => frame.GetProperty("state").GetString());
        statuses.Should().Equal(["busy", "idle"]);
        var deltas = frames
            .Where(frame => frame.GetProperty("type").GetString() == "text.delta")
            .Select(frame => frame.GetProperty("text").GetString());
        string.Concat(deltas).Should().Be(Answer);
        frames.Select(frame => frame.TryGetProperty("sequence", out var sequence) ? sequence.GetInt64() : 0)
            .Where(sequence => sequence > 0)
            .Should().BeInAscendingOrder();
        frames.Select(frame => frame.TryGetProperty("runId", out var run) ? run.GetString() : null)
            .Where(value => value is not null)
            .Should().OnlyContain(value => value == runId);

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript",
            deadline.Token);
        transcript.GetProperty("turns").GetArrayLength().Should().Be(1);
        var turn = transcript.GetProperty("turns")[0];
        turn.GetProperty("runId").GetString().Should().Be(runId);
        turn.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("role").GetString())
            .Should().Equal(["user", "assistant"]);
        turn.GetProperty("messages")[1].GetProperty("parts")[0].GetProperty("text").GetString().Should().Be(Answer);
    }

    [Fact(Timeout = 60_000)]
    public async Task EventsReplayRetainedFramesAfterReconnect()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        await using var harness = await StartHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        string runId;
        var liveTypes = new List<string>();
        await using (var events = await harness.OpenEventsAsync(sessionId, deadline.Token))
        {
            await events.ReceiveFrameAsync(deadline.Token);
            runId = await harness.SubmitTurnAsync(sessionId, "hello", deadline.Token);
            liveTypes.AddRange((await events.CollectUntilAsync(
                    frame => frame.GetProperty("type").GetString() == "run.completed",
                    deadline.Token))
                .Select(frame => frame.GetProperty("type").GetString()!));
        }

        liveTypes.Should().Contain("text.delta");

        // A reconnecting frontend that observed sequence 1 resumes after it and receives the
        // retained remainder exactly once and in order (the trailing run.status idle frame
        // follows run.completed but the collector stops at the terminal frame).
        await using var replay = await harness.OpenEventsAsync(
            sessionId,
            deadline.Token,
            sinceSequence: 1);
        await replay.ReceiveFrameAsync(deadline.Token);
        var replayed = await replay.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.completed",
            deadline.Token);
        replayed.Select(frame => frame.GetProperty("type").GetString()).Should().Equal(
            ["message.completed", "run.completed"]);
        replayed.Select(frame => frame.TryGetProperty("runId", out var run) ? run.GetString() : null)
            .Where(value => value is not null)
            .Should().OnlyContain(value => value == runId);
    }

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentTurnsAreRejectedAsBusy()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        await using var harness = await StartHostWithHangingProviderAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var first = await harness.SubmitTurnAsync(sessionId, "first", deadline.Token);
        var second = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/turns",
            new { text = "second" },
            deadline.Token);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await second.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        error.GetProperty("code").GetString().Should().Be("agent_busy");

        harness.ReleaseProvider();
        await harness.WaitForTurnCommittedAsync(sessionId, deadline.Token);
    }

    [Fact(Timeout = 60_000)]
    public async Task CancelTerminatesTheRunAndFramesCarryTheOutcome()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        await using var harness = await StartHostWithHangingProviderAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
        await events.ReceiveFrameAsync(deadline.Token);
        var runId = await harness.SubmitTurnAsync(sessionId, "first", deadline.Token);

        var cancel = await harness.Client.PostAsync(
            $"/v1/agent/runs/{runId}/cancel",
            content: null,
            deadline.Token);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        harness.ReleaseProvider();

        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);
        var failure = frames.Single(frame => frame.GetProperty("type").GetString() == "run.failed");
        failure.GetProperty("code").GetString().Should().Be("run_cancelled");
        failure.GetProperty("runId").GetString().Should().Be(runId);
        frames.Select(frame => frame.GetProperty("type").GetString()).Should().EndWith(
            ["run.failed", "run.status"]);

        var missing = await harness.Client.PostAsync(
            $"/v1/agent/runs/{Guid.NewGuid():N}/cancel",
            content: null,
            deadline.Token);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(Timeout = 60_000)]
    public async Task ToolLoopStreamsToolActivityFramesAndFinalAnswer()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            toolFlow: true,
            toolName: "echo",
            toolArgumentsJson: "{\"text\":\"hello\"}");
        var harness = await FrontendHarness.StartAsync(
            deadline.Token, provider, hanging: false, configureBuilder: builder =>
            {
                builder.Services.RemoveAll<IReadOnlyList<AIFunction>>();
                builder.Services.AddSingleton<IReadOnlyList<AIFunction>>([FrontendTestFunctions.CreateEchoFunction()]);
            });
        try
        {
            var sessionId = await harness.GetSessionIdAsync(deadline.Token);

            await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
            await events.ReceiveFrameAsync(deadline.Token);
            var runId = await harness.SubmitTurnAsync(sessionId, "use echo", deadline.Token);

            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.status" &&
                         frame.GetProperty("state").GetString() == "idle",
                deadline.Token);

            var types = frames.Select(frame => frame.GetProperty("type").GetString()).ToArray();
            types.Should().Contain("tool.started").And.Contain("tool.finished");
            var started = frames.Single(frame => frame.GetProperty("type").GetString() == "tool.started");
            started.GetProperty("tool").GetString().Should().Be("echo");
            started.GetProperty("runId").GetString().Should().Be(runId);
            frames.Single(frame => frame.GetProperty("type").GetString() == "text.delta")
                .GetProperty("text").GetString().Should().Be("tool-backed answer");

            var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                deadline.Token);
            transcript.GetProperty("turns").GetArrayLength().Should().Be(1);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CommandCellsAnswerInlineOnTheTurnEndpoint()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var response = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/turns",
            new { text = "%status" },
            deadline.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        body.GetProperty("markdown").GetString().Should().Contain("### Maieutics status");
        // Command answers always carry the active session so frontends can re-pin.
        body.GetProperty("sessionId").GetString().Should().Be(sessionId);

        (await harness.Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "%session list" },
                deadline.Token))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript",
            deadline.Token);
        transcript.GetProperty("turns").GetArrayLength().Should().Be(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task StatusCompletionAndSessionLifecycleEndpointsAnswer()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var client = harness.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/v1/status", deadline.Token);
        status.GetProperty("markdown").GetString().Should().Contain("### Maieutics status");

        var completion = await client.PostAsJsonAsync(
            "/v1/agent/complete",
            new { text = "%se", cursor = 3 },
            deadline.Token);
        completion.StatusCode.Should().Be(HttpStatusCode.OK);
        var matches = (await completion.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("matches").EnumerateArray().Select(value => value.GetString()).ToArray();
        matches.Should().Contain("%session");

        var stored = await client.GetFromJsonAsync<JsonElement>("/v1/agent/sessions", deadline.Token);
        stored.ValueKind.Should().Be(JsonValueKind.Array);

        // Persistence is wired in the composition root, so an unknown stored session is a
        // typed not-found error.
        var resume = await client.PostAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/resume",
            content: null,
            deadline.Token);
        resume.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var resumeError = await resume.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        resumeError.GetProperty("code").GetString().Should().Be("not_found");

        var previousSessionId = await harness.GetSessionIdAsync(deadline.Token);
        var newSession = await client.PostAsync("/v1/agent/sessions", content: null, deadline.Token);
        newSession.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await newSession.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        created.GetProperty("id").GetString().Should().NotBe(previousSessionId);
    }

    [Fact(Timeout = 60_000)]
    public async Task ReasoningContentStaysOutOfTheFrontendSurface()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            answer: "public answer",
            reasoning: "secret chain of thought");
        await using var harness = await FrontendHarness.StartAsync(
            deadline.Token, provider, hanging: false);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
        await events.ReceiveFrameAsync(deadline.Token);
        await harness.SubmitTurnAsync(sessionId, "think", deadline.Token);
        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);

        var allText = string.Concat(frames
            .Select(frame => frame.TryGetProperty("text", out var text) ? text.GetString() : null)
            .Where(value => value is not null));
        allText.Should().NotContain("secret chain of thought");
        var completed = frames.Single(frame => frame.GetProperty("type").GetString() == "message.completed");
        completed.GetProperty("agentMessage").GetRawText().Should().NotContain("secret chain of thought");

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript",
            deadline.Token);
        transcript.GetRawText().Should().NotContain("secret chain of thought");
        transcript.GetRawText().Should().Contain("public answer");
    }

    [Fact(Timeout = 60_000)]
    public async Task ConfigurationReloadSwitchesTheProviderForTheNextTurn()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
        var firstProvider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            model: "model-one",
            answer: "answer from model one");
        var secondProvider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            model: "model-two",
            answer: "answer from model two");
        await using var harness = await FrontendHarness.StartAsync(
            deadline.Token, firstProvider, hanging: false, model: "model-one");
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await harness.SubmitTurnAsync(sessionId, "first", deadline.Token);
        var firstTranscript = await WaitForTranscriptTurnsAsync(
            harness, sessionId, minimumTurns: 1, deadline.Token);
        firstTranscript.GetProperty("turns")[0].GetProperty("messages")[1]
            .GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("answer from model one");

        // Rewrite the configuration file: the runtime hot reload picks the new endpoint up
        // and the next turn runs against the second provider.
        await File.WriteAllTextAsync(
            harness.ConfigurationFile,
            CreateSmokeConfiguration(secondProvider.Endpoint.ToString(), "model-two"),
            deadline.Token);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var status = await harness.Client.GetFromJsonAsync<JsonElement>("/v1/status", wait.Token);
            if (status.GetProperty("markdown").GetString()!.Contains("model-two")) break;
            await Task.Delay(100, wait.Token);
        }

        await harness.SubmitTurnAsync(sessionId, "second", deadline.Token);
        var reloaded = await WaitForTranscriptTurnsAsync(
            harness, sessionId, minimumTurns: 2, deadline.Token);
        reloaded.GetProperty("turns").GetArrayLength().Should().Be(2);
        reloaded.GetProperty("turns")[1].GetProperty("messages")[1]
            .GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("answer from model two");
        reloaded.GetProperty("turns")[1].GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("role").GetString())
            .Should().Equal(["user", "assistant"]);
    }

    private static async Task<JsonElement> WaitForTranscriptTurnsAsync(
        FrontendHarness harness,
        string sessionId,
        int minimumTurns,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                wait.Token);
            if (transcript.GetProperty("turns").GetArrayLength() >= minimumTurns)
                return transcript;

            await Task.Delay(50, wait.Token);
        }
    }

    private static string CreateSmokeConfiguration(string endpoint, string model)
    {
        return $$"""
            {
              "Maieutics": {
                "Model": { "Provider": "OpenAI", "Name": "{{model}}" },
                "Providers": {
                  "OpenAI": {
                    "ApiFlavor": "ChatCompletions",
                    "ApiKey": "test-key",
                    "Endpoint": "{{endpoint}}"
                  }
                }
              }
            }
            """;
    }

    [Fact(Timeout = 60_000)]
    public async Task SessionQueriesAreServedByTheActiveSessionOnly()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var response = await harness.Client.GetAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/transcript",
            deadline.Token);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        error.GetProperty("code").GetString().Should().Be("session_not_active");
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private static async Task<FrontendHarness> StartHostAsync(CancellationToken cancellationToken)
    {
        return await FrontendHarness.StartAsync(
            cancellationToken,
            new FakeOpenAiServer(
                OpenAiApiFlavor.ChatCompletions,
                answer: Answer),
            hanging: false);
    }

    private static async Task<FrontendHarness> StartHostWithHangingProviderAsync(
        CancellationToken cancellationToken)
    {
        return await FrontendHarness.StartAsync(
            cancellationToken,
            new HangingOpenAiServer(),
            hanging: true);
    }

    /// <summary>Boots the composition root in process with the frontend API enabled and a
    /// fake model provider, mirroring the Jupyter host integration tests.</summary>
    private sealed class FrontendHarness : IAsyncDisposable
    {
        private readonly IHost host;
        private readonly IAsyncDisposable provider;
        private readonly string configurationFile;
        private readonly HangingOpenAiServer? hangingProvider;

        private FrontendHarness(
            IHost host,
            IAsyncDisposable provider,
            string configurationFile,
            string discoveryPath,
            JsonElement discovery,
            bool hanging)
        {
            this.host = host;
            this.provider = provider;
            this.configurationFile = configurationFile;
            hangingProvider = hanging ? (HangingOpenAiServer)provider : null;
            DiscoveryPath = discoveryPath;
            Discovery = discovery;
            Client = CreateClient();
        }

        public string DiscoveryPath { get; }

        public string ConfigurationFile => configurationFile;

        public JsonElement Discovery { get; }

        public HttpClient Client { get; }

        public string Url => Discovery.GetProperty("url").GetString()!;

        public string Token => Discovery.GetProperty("token").GetString()!;

        public static async Task<FrontendHarness> StartAsync(
            CancellationToken cancellationToken,
            IAsyncDisposable provider,
            bool hanging,
            Action<WebApplicationBuilder>? configureBuilder = null,
            string model = "test-model")
        {
            var configurationFile = Path.Combine(
                Path.GetTempPath(),
                $"maieutics-frontend-config-{Guid.NewGuid():N}.json");
            // The model and provider endpoint live in the configuration file (not the CLI or
            // in-memory configuration) so the hot-reload test can rewrite them.
            var endpoint = (provider as FakeOpenAiServer)?.Endpoint.ToString()
                ?? ((HangingOpenAiServer)provider).Endpoint.ToString();
            var configurationFileBody = $$"""
                {
                  "Maieutics": {
                    "Model": { "Provider": "OpenAI", "Name": "{{model}}" },
                    "Providers": {
                      "OpenAI": {
                        "ApiFlavor": "ChatCompletions",
                        "ApiKey": "test-key",
                        "Endpoint": "{{endpoint}}"
                      }
                    }
                  }
                }
                """;
            File.WriteAllText(configurationFile, configurationFileBody);
            var discoveryPath = Path.Combine(
                Path.GetTempPath(),
                $"maieutics-frontend-discovery-{Guid.NewGuid():N}.json");

            var host = MaieuticsHost.CreateApplication(
            [
                "--config", configurationFile,
                "--frontend-discovery", discoveryPath
            ], builder =>
            {
                configureBuilder?.Invoke(builder);
            });
            await host.StartAsync(cancellationToken);

            JsonElement discoveryElement;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(20));
                while (true)
                {
                    if (File.Exists(discoveryPath))
                    {
                        using var stream = File.OpenRead(discoveryPath);
                        using var document = await JsonDocument.ParseAsync(
                            stream,
                            cancellationToken: wait.Token);
                        discoveryElement = document.RootElement.Clone();
                        break;
                    }

                    await Task.Delay(50, wait.Token);
                }
            }

            return new FrontendHarness(
                host,
                provider,
                configurationFile,
                discoveryPath,
                discoveryElement,
                hanging);
        }

        public HttpClient CreateClient(string? token = null)
        {
            var client = new HttpClient { BaseAddress = new Uri(Url) };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token ?? Token);
            return client;
        }

        public async Task<string> GetSessionIdAsync(CancellationToken cancellationToken)
        {
            var session = await Client.GetFromJsonAsync<JsonElement>("/v1/agent/session", cancellationToken)
                ;
            return session.GetProperty("id").GetString()!;
        }

        public async Task<string> SubmitTurnAsync(
            string sessionId,
            string text,
            CancellationToken cancellationToken)
        {
            using var response = await Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text },
                cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
                ;
            return body.GetProperty("runId").GetString()!;
        }

        public async Task<FrontendEventsConnection> OpenEventsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            long sinceSequence = 0)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
            await socket.ConnectAsync(
                new Uri(
                    $"{Url.Replace("http://", "ws://")}/v1/agent/sessions/{sessionId}/events?sinceSequence={sinceSequence}"),
                cancellationToken);
            return new FrontendEventsConnection(socket);
        }

        public void ReleaseProvider() => hangingProvider?.Release();

        public async Task WaitForTurnCommittedAsync(string sessionId, CancellationToken cancellationToken)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var transcript = await Client.GetFromJsonAsync<JsonElement>(
                    $"/v1/agent/sessions/{sessionId}/transcript",
                    wait.Token);
                if (transcript.GetProperty("turns").GetArrayLength() > 0) return;

                await Task.Delay(50, wait.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await host.StopAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
            }

            await ((IAsyncDisposable)host).DisposeAsync();
            await provider.DisposeAsync();
            File.Delete(configurationFile);
        }
    }

    private sealed class FrontendEventsConnection(ClientWebSocket socket) : IAsyncDisposable
    {
        public async ValueTask<JsonElement> ReceiveFrameAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (true)
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await socket.ReceiveAsync(segment, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("The events socket closed unexpectedly.");

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(message.ToArray()));
            return document.RootElement.Clone();
        }

        public async Task<IReadOnlyList<JsonElement>> CollectUntilAsync(
            Func<JsonElement, bool> until,
            CancellationToken cancellationToken)
        {
            var frames = new List<JsonElement>();
            while (true)
            {
                var frame = await ReceiveFrameAsync(cancellationToken);
                frames.Add(frame);
                if (until(frame)) return frames;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "test done",
                        cleanup.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
            }
            finally
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>A chat-completions SSE server that accepts the request and never answers
    /// until released, making run lifetime deterministic.</summary>
    private sealed class HangingOpenAiServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HangingOpenAiServer()
        {
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
            _ = ServeAsync(cancellation.Token);
        }

        public Uri Endpoint { get; }

        public void Release() => release.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync();
            listener.Stop();
            release.TrySetResult();
            cancellation.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                await using var stream = client.GetStream();
                _ = DrainAsync(stream, cancellationToken);
                await release.Task;
                var body = Encoding.UTF8.GetBytes(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"late\"}}]}\n\ndata: [DONE]\n\n");
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or SocketException or IOException)
            {
            }
        }

        private async Task DrainAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            try
            {
                while (await stream.ReadAsync(buffer, cancellationToken) > 0)
                {
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
            }
        }
    }
}
