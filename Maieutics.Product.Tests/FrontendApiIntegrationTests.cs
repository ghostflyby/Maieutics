using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Frontend;
using Maieutics.Providers.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendApiIntegrationTests
{
    private const string Answer = "streamed answer";

    private const string DUAL_PROVIDER_CONFIG_TEMPLATE = """
        {
          "Maieutics": {
            "DefaultProfile": "openai-profile",
            "Sources": {
              "openai": {
                "Provider": "OpenAI",
                "ApiKey": "test-key",
                "ApiFlavor": "ChatCompletions",
                "Endpoint": "{{openaiEndpoint}}"
              },
              "anthropic": {
                "Provider": "Anthropic",
                "ApiKey": "anthropic-key",
                "Endpoint": "{{anthropicEndpoint}}"
              }
            },
            "Profiles": {
              "openai-profile": { "Source": "openai", "Model": "test-model" },
              "claude-profile": { "Source": "anthropic", "Model": "claude-test" }
            }
          }
        }
        """;

    private const string ANTHROPIC_CONFIG_TEMPLATE = """
        {
          "Maieutics": {
            "DefaultProfile": "claude",
            "Sources": {
              "anthropic": {
                "Provider": "Anthropic",
                "ApiKey": "anthropic-key",
                "Endpoint": "{{endpoint}}"
              }
            },
            "Profiles": {
              "claude": { "Source": "anthropic", "Model": "claude-test" }
            }
          }
        }
        """;

    private const string CONFIGURATION_TEMPLATE = """
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
    public async Task DiscoveryFileIsOwnerOnly()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);

        // The discovery file carries the bearer token; on Unix it must be readable and
        // writable by the owner only (Windows relies on the user-scoped temp ACLs).
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(harness.DiscoveryPath);
            mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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
    public async Task InputAnswersCompletePendingReplStdinRequests()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        await using var harness = await StartHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var target = new FakePresentationTarget();
        await using var scope = harness.AttachPresentation(sessionId, target);
        using var waitDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        waitDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        var wait = scope.Sink.RequestInputAsync("Name:", password: false, waitDeadline.Token);

        var published = target.Published.Single(entry => entry.Type == "input.request");
        var requestId = published.Data.GetProperty("requestId").GetString()!;

        var answer = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/{requestId}",
            new { value = "ghost" },
            deadline.Token);
        answer.StatusCode.Should().Be(HttpStatusCode.OK);

        (await wait).Should().Be("ghost");

        // A second answer for the same id is a typed 404, and so is an unknown id.
        var duplicate = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/{requestId}",
            new { value = "again" },
            deadline.Token);
        duplicate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var duplicateError = await duplicate.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        duplicateError.GetProperty("code").GetString().Should().Be("not_found");

        var unknown = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/input-{Guid.NewGuid():N}",
            new { value = "x" },
            deadline.Token);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A run ending (the presentation scope detaching) cancels pending requests
        // server-side, so the late answer for an outstanding id is a typed 404 too.
        var lateTask = scope.Sink.RequestInputAsync("late:", password: false, waitDeadline.Token);
        target.Published.Should().Contain(entry => entry.Type == "input.request");
        await scope.DisposeAsync();
        var lateAct = async () => await lateTask;
        await lateAct.Should().ThrowAsync<OperationCanceledException>();
        var lateId = target.Published.Last(entry => entry.Type == "input.request")
            .Data.GetProperty("requestId").GetString()!;
        var lateAnswer = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/{lateId}",
            new { value = "late" },
            deadline.Token);
        lateAnswer.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(Timeout = 60_000)]
    public async Task InputAnswersWithMissingOrMalformedBodiesAreRejected()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);

        var empty = await harness.Client.PostAsync(
            "/v1/agent/inputs/input-unknown",
            content: null,
            deadline.Token);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var malformed = await harness.Client.PostAsync(
            "/v1/agent/inputs/input-unknown",
            new StringContent("not json", Encoding.UTF8, "application/json"),
            deadline.Token);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(Timeout = 60_000)]
    public async Task CanceledTurnStartAbortsTheHandshakeWithoutReservingTheSession()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        // The cancellation token only reaches the pre-run handshake: AgentSession
        // aborts before the session reservation, so the turn start leaves the
        // session free and the transcript untouched.
        var act = () => harness.SessionService.StartTurnAsync(
            sessionId,
            "hello",
            new CancellationToken(canceled: true));
        await act.Should().ThrowAsync<OperationCanceledException>();

        harness.SessionService.DescribeSession(sessionId).Turns.Should().Be(0);

        // The session still accepts a normal turn afterwards.
        var runId = await harness.SubmitTurnAsync(sessionId, "hello", deadline.Token);
        runId.Should().NotBeNullOrWhiteSpace();
        await harness.WaitForTurnCommittedAsync(sessionId, deadline.Token);
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
    public async Task SessionsCarryDisplayMetadataAndRenameWithoutActivation()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var client = harness.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/agent/capabilities", deadline.Token);
        capabilities.GetProperty("workspaceRoot").GetString().Should().NotBeNullOrEmpty();

        // Renaming the active session before any turn creates its zero-turn row.
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        var rename = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/rename",
            new { title = "Design review" },
            deadline.Token);
        rename.StatusCode.Should().Be(HttpStatusCode.OK);
        var renamed = await rename.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        renamed.GetProperty("id").GetString().Should().Be(sessionId);
        renamed.GetProperty("title").GetString().Should().Be("Design review");

        // The active-session endpoint surfaces the stored title once set.
        var activeAfterRename = await client.GetFromJsonAsync<JsonElement>(
            "/v1/agent/session",
            deadline.Token);
        activeAfterRename.GetProperty("id").GetString().Should().Be(sessionId);
        activeAfterRename.GetProperty("title").GetString().Should().Be("Design review");

        var stored = await client.GetFromJsonAsync<JsonElement>("/v1/agent/sessions", deadline.Token);
        var match = stored.EnumerateArray()
            .Single(session => session.GetProperty("id").GetString() == sessionId);
        match.GetProperty("title").GetString().Should().Be("Design review");
        match.GetProperty("turns").GetInt32().Should().Be(0);
        match.GetProperty("workspaceRoot").GetString().Should().NotBeNullOrEmpty();

        // Renaming a never-seen session is not a 404: the row is created (family = itself).
        var fresh = Guid.NewGuid().ToString("N");
        var create = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{fresh}/rename",
            new { title = "Named ahead of time" },
            deadline.Token);
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        // Over the cap is a typed invalid request; whitespace-only clears the title.
        var tooLong = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/rename",
            new { title = new string('x', 201) },
            deadline.Token);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await tooLong.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        error.GetProperty("code").GetString().Should().Be("invalid_request");

        var invalid = await client.PostAsJsonAsync(
            "/v1/agent/sessions/not-a-guid/rename",
            new { title = "x" },
            deadline.Token);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var clear = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/rename",
            new { title = "   " },
            deadline.Token);
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = await clear.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        // Nulls are omitted on write (protocol convention), so a cleared title is absent.
        cleared.TryGetProperty("title", out _).Should().BeFalse();

        // The active session endpoint carries no title once cleared.
        var active = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", deadline.Token);
        active.GetProperty("id").GetString().Should().Be(sessionId);
        active.TryGetProperty("title", out _).Should().BeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task ForkRewindsToACommittedTurnAndActivatesTheNewHead()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        // Two committed turns need two provider requests; the fake server serves exactly
        // the configured count and stops answering after that.
        await using var harness = await FrontendHarness.StartAsync(
            deadline.Token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: Answer, requestCount: 2),
            hanging: false);
        using var client = harness.CreateClient();

        var sourceId = await harness.GetSessionIdAsync(deadline.Token);
        await harness.SubmitTurnAsync(sourceId, "first question", deadline.Token);
        // The single-run gate rejects concurrent turns; wait for the first to commit.
        await WaitForTranscriptTurnsAsync(harness, sourceId, minimumTurns: 1, deadline.Token);
        await harness.SubmitTurnAsync(sourceId, "second question", deadline.Token);
        var source = await WaitForTranscriptTurnsAsync(harness, sourceId, minimumTurns: 2, deadline.Token);
        var firstRunId = source.GetProperty("turns")[0].GetProperty("runId").GetString()!;

        // Fork by run id: the fork keeps the turns before the referenced one and re-runs it.
        // Referencing the first turn keeps zero turns — the branch point is turn 1.
        var byRun = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { runId = firstRunId },
            deadline.Token);
        byRun.StatusCode.Should().Be(HttpStatusCode.OK);
        var fork = await byRun.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        var forkId = fork.GetProperty("id").GetString()!;
        forkId.Should().NotBe(sourceId);
        // Auto-title from the source preview plus the branch point.
        fork.GetProperty("title").GetString().Should().Be("first question · branch @ turn 1");

        // The fork is the active session and its history ends at the fork point.
        var active = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", deadline.Token);
        active.GetProperty("id").GetString().Should().Be(forkId);
        active.GetProperty("turns").GetInt32().Should().Be(0);
        var forkTranscript = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{forkId}/transcript",
            deadline.Token);
        forkTranscript.GetProperty("turns").GetArrayLength().Should().Be(0);

        // The stored list carries the lineage.
        var stored = await client.GetFromJsonAsync<JsonElement>("/v1/agent/sessions", deadline.Token);
        var head = stored.EnumerateArray().Single(session => session.GetProperty("id").GetString() == forkId);
        head.GetProperty("parentSessionId").GetString().Should().Be(sourceId);
        head.GetProperty("forkPointSeq").GetInt32().Should().Be(0);

        // Forking by seq keeps one committed turn and works for a non-active source too.
        var bySeq = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { seq = 1 },
            deadline.Token);
        bySeq.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await bySeq.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        second.GetProperty("title").GetString().Should().Be("first question · branch @ turn 2");

        // Typed failures: an out-of-range seq, an unknown run, an unknown session.
        var outOfRange = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { seq = 99 },
            deadline.Token);
        outOfRange.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var rangeError = await outOfRange.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        rangeError.GetProperty("code").GetString().Should().Be("invalid_request");

        var unknownRun = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { runId = Guid.NewGuid().ToString("N") },
            deadline.Token);
        unknownRun.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var noBody = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { },
            deadline.Token);
        noBody.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var unknownSource = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{Guid.NewGuid().ToString("N")}/fork",
            new { seq = 0 },
            deadline.Token);
        unknownSource.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(Timeout = 60_000)]
    public async Task RunCompletedCarriesModelIdentityAndUsage()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        // The Responses flavor's final event carries the provider usage; the
        // chat-completions flavor omits it unless the client asks for it.
        var provider = new FakeOpenAiServer(OpenAiApiFlavor.Responses, answer: Answer);
        await using var harness = await FrontendHarness.StartAsync(
            deadline.Token, provider, hanging: false,
            transformConfiguration: body => body.Replace("ChatCompletions", "Responses"));
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
        await events.ReceiveFrameAsync(deadline.Token);
        await harness.SubmitTurnAsync(sessionId, "count my tokens", deadline.Token);
        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);

        var completed = frames.Single(frame => frame.GetProperty("type").GetString() == "run.completed");
        var usage = completed.GetProperty("usage");
        usage.GetProperty("inputTokens").GetInt32().Should().Be(1);
        usage.GetProperty("outputTokens").GetInt32().Should().Be(1);
        usage.GetProperty("totalTokens").GetInt32().Should().Be(2);
        var model = completed.GetProperty("model");
        model.GetProperty("provider").GetString().Should().Be("OpenAI");
        model.GetProperty("model").GetString().Should().Be("test-model");
    }

    [Fact(Timeout = 60_000)]
    public async Task ModelProfilesAreListedWithTheSelection()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var client = harness.CreateClient();

        var profiles = await client.GetFromJsonAsync<JsonElement>("/v1/model/profiles", deadline.Token);
        profiles.GetArrayLength().Should().BeGreaterThan(0);
        profiles.EnumerateArray().Count(profile => profile.GetProperty("selected").GetBoolean())
            .Should().Be(1);
        profiles[0].GetProperty("provider").GetString().Should().Be("OpenAI");
    }

    [Fact(Timeout = 60_000)]
    public async Task ForkAppliesTheRequestedProfileOverride()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
        var openAi = new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, model: "test-model", answer: "openai answer");
        var anthropic = new FakeAnthropicServer("claude-test", "anthropic answer");
        var dualConfig = DUAL_PROVIDER_CONFIG_TEMPLATE
            .Replace("{{openaiEndpoint}}", openAi.Endpoint.ToString())
            .Replace("{{anthropicEndpoint}}", anthropic.Endpoint.ToString());
        await using var harness = await FrontendHarness.StartAsync(
            deadline.Token, openAi, hanging: false,
            transformConfiguration: _ => dualConfig);
        using var client = harness.CreateClient();

        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        await harness.SubmitTurnAsync(sessionId, "first turn", deadline.Token);
        await harness.WaitForTurnCommittedAsync(sessionId, deadline.Token);

        var fork = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/fork",
            new { seq = 0, profileId = "claude-profile" },
            deadline.Token);
        fork.StatusCode.Should().Be(HttpStatusCode.OK);
        var forkBody = await fork.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        var forkId = forkBody.GetProperty("id").GetString()!;

        // The override is the fork's own: its next turn runs on Anthropic while
        // the process selection stays on the configured default (OpenAI).
        await harness.SubmitTurnAsync(forkId, "second turn", deadline.Token);
        await harness.WaitForTurnCommittedAsync(forkId, deadline.Token);
        var forkTranscript = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{forkId}/transcript",
            deadline.Token);
        forkTranscript.GetProperty("turns")[0].GetProperty("messages")[1]
            .GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("anthropic answer");

        var profiles = await client.GetFromJsonAsync<JsonElement>("/v1/model/profiles", deadline.Token);
        profiles.EnumerateArray()
            .Single(profile => profile.GetProperty("id").GetString() == "openai-profile")
            .GetProperty("selected").GetBoolean().Should().BeTrue();

        var unknown = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/fork",
            new { seq = 0, profileId = "no-such-profile" },
            deadline.Token);
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await unknown.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        error.GetProperty("code").GetString().Should().Be("invalid_request");

        // A failed fork applies no model side effect: the process selection is
        // untouched after the rejected request (overrides are per session).
        var after = await client.GetFromJsonAsync<JsonElement>("/v1/model/profiles", deadline.Token);
        after.EnumerateArray()
            .Single(profile => profile.GetProperty("id").GetString() == "openai-profile")
            .GetProperty("selected").GetBoolean().Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task AddressedModelCommandsLazilyResumeStoredSessions()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
        var openAi = new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, model: "test-model", answer: "openai answer");
        var anthropic = new FakeAnthropicServer("claude-test", "anthropic answer");
        var dualConfig = DUAL_PROVIDER_CONFIG_TEMPLATE
            .Replace("{{openaiEndpoint}}", openAi.Endpoint.ToString())
            .Replace("{{anthropicEndpoint}}", anthropic.Endpoint.ToString());
        await using var harness = await FrontendHarness.StartAsync(
            deadline.Token, openAi, hanging: false,
            transformConfiguration: _ => dualConfig);
        using var client = harness.CreateClient();

        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        await harness.SubmitTurnAsync(sessionId, "first turn", deadline.Token);
        await harness.WaitForTurnCommittedAsync(sessionId, deadline.Token);

        // Churn the foreground past the live capacity so the addressed session is
        // evicted from the live set (it stays resumable in storage).
        for (var index = 0; index < 8; index++)
        {
            await client.PostAsync("/v1/agent/sessions", null, deadline.Token);
        }

        // An addressed %model use lazily resumes the session and applies the
        // override; the next turn runs on Anthropic.
        var use = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/turns",
            new { text = "%model use claude-profile" },
            deadline.Token);
        use.StatusCode.Should().Be(HttpStatusCode.OK);

        await harness.SubmitTurnAsync(sessionId, "second turn", deadline.Token);
        var transcript = await WaitForTranscriptTurnsAsync(
            harness, sessionId, minimumTurns: 2, deadline.Token);
        transcript.GetProperty("turns")[1].GetProperty("messages")[1]
            .GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("anthropic answer");
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
    public async Task AnthropicToolLoopStreamsAndCommitsTheTranscript()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        var provider = new FakeAnthropicServer("claude-test", "tool-backed answer", toolFlow: true);
        var anthropicEndpoint = provider.Endpoint.ToString();
        var anthropicConfig = ANTHROPIC_CONFIG_TEMPLATE.Replace("{{endpoint}}", anthropicEndpoint);
        var harness = await FrontendHarness.StartAsync(
            deadline.Token, provider, hanging: false, configureBuilder: builder =>
            {
                builder.Services.RemoveAll<IReadOnlyList<AIFunction>>();
                builder.Services.AddSingleton<IReadOnlyList<AIFunction>>([FrontendTestFunctions.CreateEchoFunction()]);
            }, transformConfiguration: _ => anthropicConfig);
        try
        {
            var sessionId = await harness.GetSessionIdAsync(deadline.Token);

            await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
            await events.ReceiveFrameAsync(deadline.Token);
            await harness.SubmitTurnAsync(sessionId, "use echo", deadline.Token);
            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.status" &&
                         frame.GetProperty("state").GetString() == "idle",
                deadline.Token);

            var types = frames.Select(frame => frame.GetProperty("type").GetString()).ToArray();
            types.Should().Contain("tool.started").And.Contain("tool.finished");
            frames.Single(frame => frame.GetProperty("type").GetString() == "text.delta")
                .GetProperty("text").GetString().Should().Be("tool-backed answer");

            var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                deadline.Token);
            transcript.GetProperty("turns").GetArrayLength().Should().Be(1);
            // The second provider request carries the tool result envelope.
            provider.RequestBodies.Should().HaveCount(2);
            provider.RequestBodies.Last().GetRawText().Should()
                .Contain("tool_result").And.Contain("status").And.Contain("ok");
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task NotebookSwitchesBetweenOpenAiAndAnthropicWithCanonicalHistory()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(60));
        var openAi = new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, model: "test-model", answer: "openai answer");
        var anthropic = new FakeAnthropicServer("claude-test", "anthropic answer");
        var dualConfig = DUAL_PROVIDER_CONFIG_TEMPLATE
            .Replace("{{openaiEndpoint}}", openAi.Endpoint.ToString())
            .Replace("{{anthropicEndpoint}}", anthropic.Endpoint.ToString());
        var harness = await FrontendHarness.StartAsync(
            deadline.Token, openAi, hanging: false, configureBuilder: null,
            transformConfiguration: _ => dualConfig);
        try
        {
            var sessionId = await harness.GetSessionIdAsync(deadline.Token);

            await harness.SubmitTurnAsync(sessionId, "first turn", deadline.Token);
            var transcript = await WaitForTranscriptTurnsAsync(
                harness, sessionId, minimumTurns: 1, deadline.Token);
            transcript.GetProperty("turns")[0].GetProperty("messages")[1]
                .GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("openai answer");

            // Switch the model profile via a command cell; the next turn runs on Anthropic.
            var switchResponse = await harness.Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "%model use claude-profile" },
                deadline.Token);
            switchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var switchBody = await switchResponse.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            switchBody.GetProperty("markdown").GetString().Should().Contain("claude-profile");

            await harness.SubmitTurnAsync(sessionId, "second turn", deadline.Token);
            var reloaded = await WaitForTranscriptTurnsAsync(
                harness, sessionId, minimumTurns: 2, deadline.Token);
            reloaded.GetProperty("turns")[1].GetProperty("messages")[1]
                .GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("anthropic answer");
            // Canonical history: both turns committed in order with user→assistant shape.
            reloaded.GetProperty("turns").EnumerateArray()
                .Select(turn => turn.GetProperty("messages").EnumerateArray()
                    .Select(message => message.GetProperty("role").GetString()).ToArray())
                .Should().OnlyContain(roles => roles.First() == "user" && roles.Last() == "assistant");
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task McpToolLoopRunsThroughTheFrontendWhenTestServerIsConfigured()
    {
        // Mirrors the retired kernel-driver test's opt-in semantics: the run needs
        // an external stdio MCP test server, provided through the environment.
        var mcpServer = Environment.GetEnvironmentVariable("MAIEUTICS_TEST_MCP_SERVER_EXECUTABLE");
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(mcpServer),
            "Requires an external stdio MCP test server; set MAIEUTICS_TEST_MCP_SERVER_EXECUTABLE.");

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(80));
        var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            toolFlow: true,
            toolName: "echo",
            toolArgumentsJson: "{\"value\":\"native mcp value\"}");
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var discoveryPath = Path.Combine(root, "discovery.json");
        var mcpFile = Path.Combine(root, "mcp.json");
        await File.WriteAllTextAsync(
            mcpFile,
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["test"] = new JsonObject
                    {
                        ["command"] = Path.GetFullPath(mcpServer),
                        ["args"] = new JsonArray(),
                        ["env"] = new JsonObject()
                    }
                }
            }.ToJsonString(),
            deadline.Token);
        await File.WriteAllTextAsync(
            configurationFile,
            CONFIGURATION_TEMPLATE
                .Replace("{{model}}", "test-model")
                .Replace("{{endpoint}}", provider.Endpoint.ToString()),
            deadline.Token);

        var harness = await FrontendHarness.StartAsync(
            deadline.Token, provider, hanging: false, configureBuilder: null);
        try
        {
            var sessionId = await harness.GetSessionIdAsync(deadline.Token);

            var mcpList = await harness.Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "%mcp list" },
                deadline.Token);
            mcpList.StatusCode.Should().Be(HttpStatusCode.OK);
            var markdown = (await mcpList.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
                .GetProperty("markdown").GetString()!;
            markdown.Should().Contain("`echo` → `echo`").And.Contain("Connected");

            await harness.SubmitTurnAsync(sessionId, "call the MCP echo tool", deadline.Token);
            var transcript = await WaitForTranscriptTurnsAsync(
                harness, sessionId, minimumTurns: 1, deadline.Token);
            transcript.GetProperty("turns")[0].GetProperty("messages")[1]
                .GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("tool-backed answer");
            provider.RequestBodies.Last().GetRawText().Should()
                .Contain("status").And.Contain("ok").And.Contain("native mcp value");
        }
        finally
        {
            await harness.DisposeAsync();
            DeleteDirectoryWithRetry(root);
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task SessionQueriesAreServedPerSessionWithLazyResolve()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var client = harness.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/agent/capabilities", deadline.Token);
        capabilities.GetProperty("multiSession").GetBoolean().Should().BeTrue();

        var bootId = await harness.GetSessionIdAsync(deadline.Token);

        // Unknown sessions fail with not_found.
        var missing = await client.GetAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/transcript",
            deadline.Token);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A stored session that is not the foreground is still served: it is
        // lazily resumed. Starting a new session moves the foreground alias.
        await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{bootId}/rename",
            new { title = "boot" },
            deadline.Token);
        await client.PostAsync("/v1/agent/sessions", null, deadline.Token);
        var foreground = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", deadline.Token);
        foreground.GetProperty("id").GetString().Should().NotBe(bootId);

        var transcript = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{bootId}/transcript",
            deadline.Token);
        transcript.GetProperty("sessionId").GetString().Should().Be(bootId);
        transcript.GetProperty("turns").GetArrayLength().Should().Be(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task SessionsRunConcurrentlyAndCommandsStaySessionScoped()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
        // Two in-flight turns on two sessions must both commit (the old
        // single-active semantics busy-rejected the second submission).
        await using var harness = await FrontendHarness.StartAsync(
            deadline.Token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: Answer, requestCount: 2),
            hanging: false);
        using var client = harness.CreateClient();

        var first = await harness.GetSessionIdAsync(deadline.Token);
        var second = await (await client.PostAsync("/v1/agent/sessions", null, deadline.Token))
            .Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        var secondId = second.GetProperty("id").GetString()!;

        var firstTurn = harness.SubmitTurnAsync(first, "turn on first", deadline.Token);
        var secondTurn = harness.SubmitTurnAsync(secondId, "turn on second", deadline.Token);
        await Task.WhenAll(firstTurn, secondTurn);
        await WaitForTranscriptTurnsAsync(harness, first, minimumTurns: 1, deadline.Token);
        await WaitForTranscriptTurnsAsync(harness, secondId, minimumTurns: 1, deadline.Token);

        // A command cell answers with its own session: no re-pin happens.
        var command = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{first}/turns",
            new { text = "%session current" },
            deadline.Token);
        command.StatusCode.Should().Be(HttpStatusCode.OK);
        var answered = await command.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        answered.GetProperty("sessionId").GetString().Should().Be(first);
        answered.GetProperty("markdown").GetString().Should().Contain(first[..12]);

        // A session-switching command moves the foreground and the answer carries
        // the new session so the notebook re-pins.
        var switched = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{first}/turns",
            new { text = "%session new" },
            deadline.Token);
        var switchedBody = await switched.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        switchedBody.GetProperty("sessionId").GetString().Should().NotBe(first);
    }

    [Fact(Timeout = 60_000)]
    public async Task ObjectRouteNormalizesUppercaseIdsAndTypesInvalidIds()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        // The object store (and the /v1/objects route) only exists when Agent persistence
        // is enabled; the transform turns it on for this host.
        await using var harness = await FrontendHarness.StartAsync(
            deadline.Token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: Answer),
            hanging: false,
            transformConfiguration: body => body.Replace(
                "\"Maieutics\": {",
                """
                "Maieutics": {
                    "Agent": { "Persistence": { "Enabled": true } },
                """,
                StringComparison.Ordinal));
        using var client = harness.CreateClient();

        // The route accepts uppercase hex but the content-addressed store is lowercase:
        // the id must be normalized instead of surfacing as an untyped 500.
        var payload = new byte[] { 1, 2, 3 };
        var sha256 = harness.ObjectStore.Ingest(new MemoryStream(payload)).Sha256;
        var served = await client.GetAsync($"/v1/objects/{sha256.ToUpperInvariant()}", deadline.Token);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync(deadline.Token)).Should().Equal(payload);

        // A well-formed but unknown id is a typed not_found, and a non-hex id is a typed
        // invalid_request — never an unhandled 500.
        var missing = await client.GetAsync($"/v1/objects/{new string('a', 63)}A", deadline.Token);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var missingError = await missing.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        missingError.GetProperty("code").GetString().Should().Be("not_found");

        var invalid = await client.GetAsync($"/v1/objects/{new string('z', 64)}", deadline.Token);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var invalidError = await invalid.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        invalidError.GetProperty("code").GetString().Should().Be("invalid_request");
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
    /// fake model provider, mirroring the Jupyter host integration tests. Shared with the
    /// comm-plane integration tests.</summary>
    internal sealed class FrontendHarness : IAsyncDisposable
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

        /// <summary>The content-addressed object store backing <c>/v1/objects</c>, so tests
        /// can ingest real objects without driving a full turn.</summary>
        public Maieutics.Persistence.ObjectStore ObjectStore =>
            host.Services.GetRequiredService<Maieutics.Persistence.ObjectStore>();

        public static async Task<FrontendHarness> StartAsync(
            CancellationToken cancellationToken,
            IAsyncDisposable provider,
            bool hanging,
            Action<WebApplicationBuilder>? configureBuilder = null,
            string model = "test-model",
            Func<string, string>? transformConfiguration = null)
        {
            var configurationFile = Path.Combine(
                Path.GetTempPath(),
                $"maieutics-frontend-config-{Guid.NewGuid():N}.json");
            // The model and provider endpoint live in the configuration file (not the CLI or
            // in-memory configuration) so the hot-reload test can rewrite them.
            var endpoint = (provider as FakeOpenAiServer)?.Endpoint.ToString()
                ?? (provider as HangingOpenAiServer)?.Endpoint.ToString() ?? string.Empty;
            var configurationFileBody = CONFIGURATION_TEMPLATE
                .Replace("{{model}}", model)
                .Replace("{{endpoint}}", endpoint);
            if (transformConfiguration is not null)
            {
                // A named Sources/Profiles configuration cannot carry the legacy Model
                // section; the transform rewrites the file for those scenarios.
                configurationFileBody = transformConfiguration(configurationFileBody);
            }

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

        /// <summary>Registers the test process as the owning peer of a session so a raw
        /// child socket stub can pass the /comm handshake.</summary>
        public void RegisterControlPeer(string sessionId) =>
            host.Services.GetRequiredService<Maieutics.Control.ReplControlSessionRegistry>()
                .Register(Environment.ProcessId, sessionId);

        /// <summary>The live frontend session service, for direct service-level tests
        /// that need precise control over inputs such as cancellation tokens.</summary>
        public FrontendSessionService SessionService =>
            host.Services.GetRequiredService<FrontendSessionService>();

        /// <summary>Attaches a test presentation target to the live REPL presentation
        /// router so tests can drive stdin-style input requests against the real
        /// input-answer endpoint.</summary>
        public FrontendDenoReplPresentationRouter.FrontendPresentationScope AttachPresentation(
            string sessionId,
            IFrontendPresentationTarget target)
        {
            return host.Services.GetRequiredService<FrontendDenoReplPresentationRouter>()
                .Attach(new AgentSessionId(Guid.ParseExact(sessionId, "N")), target);
        }

        /// <summary>The control host address (the Unix socket path on Unix).</summary>
        public string ControlAddress =>
            host.Services.GetRequiredService<Maieutics.Control.ReplControlHost>().ControlAddress;

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
            try
            {
                File.Delete(configurationFile);
            }
            catch (IOException)
            {
                // The reload file watcher can hold the config file briefly on
                // Windows; a leftover temp file must not mask the test result.
            }
        }
    }

    internal sealed class FrontendEventsConnection(ClientWebSocket socket) : IAsyncDisposable
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

    /// <summary>Records the presentation frames a test publishes through the live
    /// REPL presentation router.</summary>
    private sealed class FakePresentationTarget : IFrontendPresentationTarget
    {
        public List<(string Type, string? DisplayId, JsonElement Data)> Published { get; } = [];

        public void PublishPresentation(
            string type,
            string? displayId,
            JsonElement data,
            CancellationToken cancellationToken)
        {
            Published.Add((type, displayId, data.Clone()));
        }
    }

    /// <summary>A chat-completions SSE server that accepts the request and never answers
    /// until released, making run lifetime deterministic.</summary>
    private sealed class HangingOpenAiServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task serveLoop;

        public HangingOpenAiServer()
        {
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
            serveLoop = ServeAsync(cancellation.Token);
        }

        public Uri Endpoint { get; }

        public void Release() => release.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync();
            listener.Stop();
            release.TrySetResult();
            try
            {
                // The accept loop settles once the listener stops; without observing
                // it, a late ObjectDisposedException on the disposed CTS escapes.
                await serveLoop.ConfigureAwait(false);
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
            }

            cancellation.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            // Accept in a loop: the provider client (and any retried request) may open more
            // than one connection; a single accept would hang the second one on slow CI.
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellationToken);
                    _ = ServeClientAsync(client, cancellationToken);
                }
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or SocketException or IOException)
            {
            }
        }

        private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
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
            catch (Exception exception) when
                (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }
}
