using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maieutics.Agent;
using Maieutics.Frontend;
using Maieutics.Providers.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendApiIntegrationTests
{
    private const string Answer = "streamed answer";

    private const string DualProviderConfigTemplate = """
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

    private const string AnthropicConfigTemplate = """
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

    private const string ConfigurationTemplate = """
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
            Assert.Equal(1, discovery.GetProperty("version").GetInt32());
            Assert.StartsWith("http://127.0.0.1:", discovery.GetProperty("url").GetString() ?? throw new InvalidOperationException("the assertion subject is null"));
            Assert.Equal(64, (discovery.GetProperty("token").GetString() ?? throw new InvalidOperationException("the assertion subject is null")).Length);
            Assert.True(discovery.GetProperty("pid").GetInt32() > 0);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        // The discovery file must disappear with the process so a stale instance is never
        // rediscovered by a frontend.
        Assert.False(File.Exists(harness.DiscoveryPath));
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
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task FrontendEndpointsRejectMissingOrWrongBearerTokens()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var anonymous = new HttpClient { BaseAddress = new Uri(harness.Url) };
        using var forged = harness.CreateClient(token: "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/agent/session", deadline.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/v1/agent/session", deadline.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/status", deadline.Token)).StatusCode);
    }

    [Fact(Timeout = 60_000)]
    public async Task CapabilitiesAndSessionDescribeTheActiveSession()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var client = harness.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/agent/capabilities", deadline.Token);
        Assert.Equal(1, capabilities.GetProperty("protocolVersion").GetInt32());
        var session = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", deadline.Token);
        Assert.Equal(session.GetProperty("id").GetString(), capabilities.GetProperty("session").GetProperty("id").GetString());
        Assert.Equal(32, (capabilities.GetProperty("session").GetProperty("id").GetString() ?? throw new InvalidOperationException("the assertion subject is null")).Length);
        Assert.Equal(0, session.GetProperty("turns").GetInt64());
        // The composition root wires the SQLite family store whenever ApplicationPaths
        // resolve, so the active session always reports persistence enabled.
        Assert.True(session.GetProperty("persistenceEnabled").GetBoolean());
    }

    [Fact(Timeout = 60_000)]
    public async Task TurnStreamsEventsAndCommitsTheTranscript()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        await using var harness = await StartHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
        var hello = await events.ReceiveFrameAsync(deadline.Token);
        Assert.Equal("hello", hello.GetProperty("type").GetString());
        Assert.Equal(sessionId, hello.GetProperty("session").GetProperty("id").GetString());

        var runId = await harness.SubmitTurnAsync(sessionId, "hello", deadline.Token);
        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);

        var types = frames.Select(frame => frame.GetProperty("type").GetString()).ToArray();
        Assert.Equal(["run.started", "run.status"], types.Take(2));
        Assert.Equal(["message.completed", "run.completed", "run.status"], types.TakeLast(3));
        Assert.Contains("text.delta", types);
        Assert.False(frames[^2].GetProperty("truncated").GetBoolean());
        var statuses = frames
            .Where(frame => frame.GetProperty("type").GetString() == "run.status")
            .Select(frame => frame.GetProperty("state").GetString());
        Assert.Equal(["busy", "idle"], statuses);
        var deltas = frames
            .Where(frame => frame.GetProperty("type").GetString() == "text.delta")
            .Select(frame => frame.GetProperty("text").GetString());
        Assert.Equal(Answer, string.Concat(deltas));
        Assert.True(frames.Select(frame => frame.TryGetProperty("sequence", out var sequence) ? sequence.GetInt64() : 0)
            .Where(sequence => sequence > 0).SequenceEqual(frames.Select(frame => frame.TryGetProperty("sequence", out var sequence) ? sequence.GetInt64() : 0)
            .Where(sequence => sequence > 0).OrderBy(static item => item)));
        Assert.All(frames.Select(frame => frame.TryGetProperty("runId", out var run) ? run.GetString() : null)
            .Where(value => value is not null), value => Assert.True(value == runId));

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript",
            deadline.Token);
        Assert.Equal(1, transcript.GetProperty("turns").GetArrayLength());
        var turn = transcript.GetProperty("turns")[0];
        Assert.Equal(runId, turn.GetProperty("runId").GetString());
        Assert.Equal(["user", "assistant"], turn.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("role").GetString()));
        Assert.Equal(Answer, turn.GetProperty("messages")[1].GetProperty("parts")[0].GetProperty("text").GetString());
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

        Assert.Contains("text.delta", liveTypes);

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
        Assert.Equal(["message.completed", "run.completed"], replayed.Select(frame => frame.GetProperty("type").GetString()));
        Assert.All(replayed.Select(frame => frame.TryGetProperty("runId", out var run) ? run.GetString() : null)
            .Where(value => value is not null), value => Assert.True(value == runId));
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
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var error = await second.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("agent_busy", error.GetProperty("code").GetString());

        // "busy" has two causes with different remedies: the first run holding the
        // session's single-run gate (what this test is about), or the previous run still
        // detaching its presentation scope. The code alone cannot tell them apart, so pin
        // the intended one — the first run must still be in flight against the hanging
        // provider. Without this, a race satisfies the assertion for the wrong reason and
        // the real failure lands later as an opaque wait timeout.
        Assert.True(harness.SessionService.TryGetRun(first, out var firstStream));
        if (firstStream is null)
            throw new InvalidOperationException(
                $"Run {first} is not retained; the busy rejection cannot be attributed to an in-flight run.");
        Assert.False(firstStream.Completion.IsCompleted, "the first turn must still be running when the second submission is rejected as busy");

        harness.ReleaseProvider();
        await harness.WaitForTurnCommittedAsync(sessionId, deadline.Token, runId: first);
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
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        harness.ReleaseProvider();

        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);
        var failure = frames.Single(frame => frame.GetProperty("type").GetString() == "run.failed");
        Assert.Equal("run_cancelled", failure.GetProperty("code").GetString());
        Assert.Equal(runId, failure.GetProperty("runId").GetString());
        Assert.Equal(["run.failed", "run.status"], frames.Select(frame => frame.GetProperty("type").GetString()).TakeLast(2));

        var missing = await harness.Client.PostAsync(
            $"/v1/agent/runs/{Guid.NewGuid():N}/cancel",
            content: null,
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact(Timeout = 60_000)]
    public async Task InputAnswersCompletePendingReplStdinRequests()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        await using var harness = await StartHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var target = new FakePresentationTarget();
        var scope = harness.AttachPresentation(sessionId, target);
        using var waitDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        waitDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        var wait = scope.Sink.RequestInputAsync("Name:", password: false, waitDeadline.Token);

        var published = target.Published.Single(entry => entry.Type == "input.request");
        var requestId = published.Data.GetProperty("requestId").GetString();
        Assert.NotNull(requestId);

        var answer = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/{requestId}",
            new { value = "ghost" },
            deadline.Token);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

        Assert.Equal("ghost", await wait);

        // A second answer for the same id is a typed 404, and so is an unknown id.
        var duplicate = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/{requestId}",
            new { value = "again" },
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, duplicate.StatusCode);
        var duplicateError = await duplicate.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("not_found", duplicateError.GetProperty("code").GetString());

        var unknown = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/input-{Guid.NewGuid():N}",
            new { value = "x" },
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // A run ending (the presentation scope detaching) cancels pending requests
        // server-side, so the late answer for an outstanding id is a typed 404 too.
        var lateTask = scope.Sink.RequestInputAsync("late:", password: false, waitDeadline.Token);
        Assert.Contains(target.Published, entry => entry.Type == "input.request");
        await scope.DisposeAsync();
        var lateAct = async () => await lateTask;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await lateAct.Invoke(); });
        var lateId = target.Published.Last(entry => entry.Type == "input.request")
            .Data.GetProperty("requestId").GetString()!;
        var lateAnswer = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/{lateId}",
            new { value = "late" },
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, lateAnswer.StatusCode);
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
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var malformed = await harness.Client.PostAsync(
            "/v1/agent/inputs/input-unknown",
            new StringContent("not json", Encoding.UTF8, "application/json"),
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await act.Invoke(); });

        Assert.Equal(0, harness.SessionService.DescribeSession(sessionId).Turns);

        // The session still accepts a normal turn afterwards.
        var runId = await harness.SubmitTurnAsync(sessionId, "hello", deadline.Token);
        Assert.False(string.IsNullOrWhiteSpace(runId));
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
            Assert.Contains("tool.started", types);
            Assert.Contains("tool.finished", types);
            var started = frames.Single(frame => frame.GetProperty("type").GetString() == "tool.started");
            Assert.Equal("echo", started.GetProperty("tool").GetString());
            Assert.Equal(runId, started.GetProperty("runId").GetString());
            Assert.Equal("tool-backed answer", frames.Single(frame => frame.GetProperty("type").GetString() == "text.delta")
                .GetProperty("text").GetString());

            var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                deadline.Token);
            Assert.Equal(1, transcript.GetProperty("turns").GetArrayLength());
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Contains("### Maieutics status", body.GetProperty("markdown").GetString());
        // Command answers always carry the active session so frontends can re-pin.
        Assert.Equal(sessionId, body.GetProperty("sessionId").GetString());

        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "%session list" },
                deadline.Token))
            .StatusCode);

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript",
            deadline.Token);
        Assert.Equal(0, transcript.GetProperty("turns").GetArrayLength());
    }

    [Fact(Timeout = 60_000)]
    public async Task StatusCompletionAndSessionLifecycleEndpointsAnswer()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var client = harness.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/v1/status", deadline.Token);
        Assert.Contains("### Maieutics status", status.GetProperty("markdown").GetString());

        var completion = await client.PostAsJsonAsync(
            "/v1/agent/complete",
            new { text = "%se", cursor = 3 },
            deadline.Token);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var matches = (await completion.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("matches").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("%session", matches);

        var stored = await client.GetFromJsonAsync<JsonElement>("/v1/agent/sessions", deadline.Token);
        Assert.Equal(JsonValueKind.Array, stored.ValueKind);

        // Persistence is wired in the composition root, so an unknown stored session is a
        // typed not-found error.
        var resume = await client.PostAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/resume",
            content: null,
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, resume.StatusCode);
        var resumeError = await resume.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("not_found", resumeError.GetProperty("code").GetString());

        var previousSessionId = await harness.GetSessionIdAsync(deadline.Token);
        var newSession = await client.PostAsync("/v1/agent/sessions", content: null, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, newSession.StatusCode);
        var created = await newSession.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.NotEqual(previousSessionId, created.GetProperty("id").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task SessionsCarryDisplayMetadataAndRenameWithoutActivation()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var client = harness.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/agent/capabilities", deadline.Token);
        Assert.False(string.IsNullOrEmpty(capabilities.GetProperty("workspaceRoot").GetString()));

        // Renaming the active session before any turn creates its zero-turn row.
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        var rename = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/rename",
            new { title = "Design review" },
            deadline.Token);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var renamed = await rename.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal(sessionId, renamed.GetProperty("id").GetString());
        Assert.Equal("Design review", renamed.GetProperty("title").GetString());

        // The active-session endpoint surfaces the stored title once set.
        var activeAfterRename = await client.GetFromJsonAsync<JsonElement>(
            "/v1/agent/session",
            deadline.Token);
        Assert.Equal(sessionId, activeAfterRename.GetProperty("id").GetString());
        Assert.Equal("Design review", activeAfterRename.GetProperty("title").GetString());

        var stored = await client.GetFromJsonAsync<JsonElement>("/v1/agent/sessions", deadline.Token);
        var match = stored.EnumerateArray()
            .Single(session => session.GetProperty("id").GetString() == sessionId);
        Assert.Equal("Design review", match.GetProperty("title").GetString());
        Assert.Equal(0, match.GetProperty("turns").GetInt32());
        Assert.False(string.IsNullOrEmpty(match.GetProperty("workspaceRoot").GetString()));

        // Renaming a never-seen session is not a 404: the row is created (family = itself).
        var fresh = Guid.NewGuid().ToString("N");
        var create = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{fresh}/rename",
            new { title = "Named ahead of time" },
            deadline.Token);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        // Over the cap is a typed invalid request; whitespace-only clears the title.
        var tooLong = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/rename",
            new { title = new string('x', 201) },
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var error = await tooLong.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("invalid_request", error.GetProperty("code").GetString());

        var invalid = await client.PostAsJsonAsync(
            "/v1/agent/sessions/not-a-guid/rename",
            new { title = "x" },
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var clear = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/rename",
            new { title = "   " },
            deadline.Token);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var cleared = await clear.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        // Nulls are omitted on write (protocol convention), so a cleared title is absent.
        Assert.False(cleared.TryGetProperty("title", out _));

        // The active session endpoint carries no title once cleared.
        var active = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", deadline.Token);
        Assert.Equal(sessionId, active.GetProperty("id").GetString());
        Assert.False(active.TryGetProperty("title", out _));
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
        Assert.Equal(HttpStatusCode.OK, byRun.StatusCode);
        var fork = await byRun.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        var forkId = fork.GetProperty("id").GetString()!;
        Assert.NotEqual(sourceId, forkId);
        // Auto-title from the source preview plus the branch point.
        Assert.Equal("first question · branch @ turn 1", fork.GetProperty("title").GetString());

        // The fork is the active session and its history ends at the fork point.
        var active = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", deadline.Token);
        Assert.Equal(forkId, active.GetProperty("id").GetString());
        Assert.Equal(0, active.GetProperty("turns").GetInt32());
        var forkTranscript = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{forkId}/transcript",
            deadline.Token);
        Assert.Equal(0, forkTranscript.GetProperty("turns").GetArrayLength());

        // The stored list carries the lineage.
        var stored = await client.GetFromJsonAsync<JsonElement>("/v1/agent/sessions", deadline.Token);
        var head = stored.EnumerateArray().Single(session => session.GetProperty("id").GetString() == forkId);
        Assert.Equal(sourceId, head.GetProperty("parentSessionId").GetString());
        Assert.Equal(0, head.GetProperty("forkPointSeq").GetInt32());

        // Forking by seq keeps one committed turn and works for a non-active source too.
        var bySeq = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { seq = 1 },
            deadline.Token);
        Assert.Equal(HttpStatusCode.OK, bySeq.StatusCode);
        var second = await bySeq.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("first question · branch @ turn 2", second.GetProperty("title").GetString());

        // Typed failures: an out-of-range seq, an unknown run, an unknown session.
        var outOfRange = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { seq = 99 },
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        var rangeError = await outOfRange.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("invalid_request", rangeError.GetProperty("code").GetString());

        var unknownRun = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { runId = Guid.NewGuid().ToString("N") },
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, unknownRun.StatusCode);

        var noBody = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { },
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);

        var unknownSource = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{Guid.NewGuid().ToString("N")}/fork",
            new { seq = 0 },
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, unknownSource.StatusCode);
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
        Assert.Equal(1, usage.GetProperty("inputTokens").GetInt32());
        Assert.Equal(1, usage.GetProperty("outputTokens").GetInt32());
        Assert.Equal(2, usage.GetProperty("totalTokens").GetInt32());
        var model = completed.GetProperty("model");
        Assert.Equal("OpenAI", model.GetProperty("provider").GetString());
        Assert.Equal("test-model", model.GetProperty("model").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task ModelProfilesAreListedWithTheSelection()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var client = harness.CreateClient();

        var profiles = await client.GetFromJsonAsync<JsonElement>("/v1/model/profiles", deadline.Token);
        Assert.True(profiles.GetArrayLength() > 0);
        Assert.Equal(1, profiles.EnumerateArray().Count(profile => profile.GetProperty("selected").GetBoolean()));
        Assert.Equal("OpenAI", profiles[0].GetProperty("provider").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task ForkAppliesTheRequestedProfileOverride()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
        var openAi = new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, model: "test-model", answer: "openai answer");
        var anthropic = new FakeAnthropicServer("claude-test", "anthropic answer");
        var dualConfig = DualProviderConfigTemplate
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
        Assert.Equal(HttpStatusCode.OK, fork.StatusCode);
        var forkBody = await fork.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        var forkId = forkBody.GetProperty("id").GetString()!;

        // The override is the fork's own: its next turn runs on Anthropic while
        // the process selection stays on the configured default (OpenAI).
        await harness.SubmitTurnAsync(forkId, "second turn", deadline.Token);
        await harness.WaitForTurnCommittedAsync(forkId, deadline.Token);
        var forkTranscript = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{forkId}/transcript",
            deadline.Token);
        Assert.Equal("anthropic answer", forkTranscript.GetProperty("turns")[0].GetProperty("messages")[1]
            .GetProperty("parts")[0].GetProperty("text").GetString());

        var profiles = await client.GetFromJsonAsync<JsonElement>("/v1/model/profiles", deadline.Token);
        Assert.True(profiles.EnumerateArray()
            .Single(profile => profile.GetProperty("id").GetString() == "openai-profile")
            .GetProperty("selected").GetBoolean());

        var unknown = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/fork",
            new { seq = 0, profileId = "no-such-profile" },
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var error = await unknown.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("invalid_request", error.GetProperty("code").GetString());

        // A failed fork applies no model side effect: the process selection is
        // untouched after the rejected request (overrides are per session).
        var after = await client.GetFromJsonAsync<JsonElement>("/v1/model/profiles", deadline.Token);
        Assert.True(after.EnumerateArray()
            .Single(profile => profile.GetProperty("id").GetString() == "openai-profile")
            .GetProperty("selected").GetBoolean());
    }

    [Fact(Timeout = 60_000)]
    public async Task AddressedModelCommandsLazilyResumeStoredSessions()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
        var openAi = new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, model: "test-model", answer: "openai answer");
        var anthropic = new FakeAnthropicServer("claude-test", "anthropic answer");
        var dualConfig = DualProviderConfigTemplate
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
        Assert.Equal(HttpStatusCode.OK, use.StatusCode);

        await harness.SubmitTurnAsync(sessionId, "second turn", deadline.Token);
        var transcript = await WaitForTranscriptTurnsAsync(
            harness, sessionId, minimumTurns: 2, deadline.Token);
        Assert.Equal("anthropic answer", transcript.GetProperty("turns")[1].GetProperty("messages")[1]
            .GetProperty("parts")[0].GetProperty("text").GetString());
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
        Assert.DoesNotContain("secret chain of thought", allText);
        var completed = frames.Single(frame => frame.GetProperty("type").GetString() == "message.completed");
        Assert.DoesNotContain("secret chain of thought", completed.GetProperty("agentMessage").GetRawText());

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript",
            deadline.Token);
        Assert.DoesNotContain("secret chain of thought", transcript.GetRawText());
        Assert.Contains("public answer", transcript.GetRawText());
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
        Assert.Equal("answer from model one", firstTranscript.GetProperty("turns")[0].GetProperty("messages")[1]
            .GetProperty("parts")[0].GetProperty("text").GetString());

        // Rewrite the configuration file: the runtime hot reload picks the new endpoint up
        // and the next turn runs against the second provider. A reload commits the replacement
        // snapshot (and its version) before the reload loop completes its signal, so awaiting
        // the version bump the write must produce guarantees the new configuration is active;
        // the single status read afterwards only confirms the surfaced model.
        var runtimeConfiguration = harness.RuntimeConfiguration;
        var beforeVersion = runtimeConfiguration.Version;
        await File.WriteAllTextAsync(
            harness.ConfigurationFile,
            CreateSmokeConfiguration(secondProvider.Endpoint.ToString(), "model-two"),
            deadline.Token);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        await runtimeConfiguration.WaitForVersionAsync(beforeVersion + 1, wait.Token);
        var status = await harness.Client.GetFromJsonAsync<JsonElement>("/v1/status", wait.Token);
        Assert.Contains("model-two", status.GetProperty("markdown").GetString());

        await harness.SubmitTurnAsync(sessionId, "second", deadline.Token);
        var reloaded = await WaitForTranscriptTurnsAsync(
            harness, sessionId, minimumTurns: 2, deadline.Token);
        Assert.Equal(2, reloaded.GetProperty("turns").GetArrayLength());
        Assert.Equal("answer from model two", reloaded.GetProperty("turns")[1].GetProperty("messages")[1]
            .GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(["user", "assistant"], reloaded.GetProperty("turns")[1].GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("role").GetString()));
    }

    /// <summary>
    ///     Waits for <paramref name="minimumTurns"/> committed turns, driven by the session's
    ///     event stream instead of polling: the transcript is read once at attach and then
    ///     re-read only after each distinct run's terminal frame — never on a timer. A
    ///     terminal frame publishes strictly after the run's completion task settles, which
    ///     follows the turn commit, so each re-read observes everything committed so far.
    ///     The socket replays the latest run's retained frames, so a run that settled just
    ///     before attach is still observed; a failed run publishes run.failed, commits no
    ///     turn, and merely triggers the next re-read.
    /// </summary>
    private static async Task<JsonElement> WaitForTranscriptTurnsAsync(
        FrontendHarness harness,
        string sessionId,
        int minimumTurns,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(30));

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript",
            wait.Token);
        if (transcript.GetProperty("turns").GetArrayLength() >= minimumTurns)
            return transcript;

        var countedRuns = new HashSet<string>(StringComparer.Ordinal);
        await using var events = await harness.OpenEventsAsync(sessionId, wait.Token);
        while (transcript.GetProperty("turns").GetArrayLength() < minimumTurns)
        {
            var frame = await events.ReceiveFrameAsync(wait.Token);
            if (frame.GetProperty("type").GetString() is not "run.completed" ||
                !frame.TryGetProperty("runId", out var runId) ||
                runId.GetString() is not { } completedRun ||
                !countedRuns.Add(completedRun))
            {
                continue;
            }

            transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                wait.Token);
        }

        return transcript;
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
        var anthropicConfig = AnthropicConfigTemplate.Replace("{{endpoint}}", anthropicEndpoint);
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
            Assert.Contains("tool.started", types);
            Assert.Contains("tool.finished", types);
            Assert.Equal("tool-backed answer", frames.Single(frame => frame.GetProperty("type").GetString() == "text.delta")
                .GetProperty("text").GetString());

            var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                deadline.Token);
            Assert.Equal(1, transcript.GetProperty("turns").GetArrayLength());
            // The second provider request carries the tool result envelope.
            Assert.Equal(2, provider.RequestBodies.Count());
            Assert.Contains("tool_result", provider.RequestBodies.Last().GetRawText());
            Assert.Contains("status", provider.RequestBodies.Last().GetRawText());
            Assert.Contains("ok", provider.RequestBodies.Last().GetRawText());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ResponsesApplyPatchToolEditsFilesAndHidesTheGeneralEditTools()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        var operation = """{"type":"create_file","path":"patched.txt","diff":"+patched body\n"}""";
        var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.Responses,
            applyPatchFlow: true,
            applyPatchOperationJson: operation,
            answer: "tool-backed answer");
        var workspace = Path.Combine(Path.GetTempPath(), $"maieutics-applypatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var harness = await FrontendHarness.StartAsync(
            deadline.Token, provider, hanging: false, configureBuilder: null,
            transformConfiguration: body => body
                .Replace("ChatCompletions", "Responses")
                .Replace(
                    "\"Model\"",
                    $"\"Workspace\": {{ \"Root\": {JsonSerializer.Serialize(workspace)} }},\n            \"Model\""));
        try
        {
            var sessionId = await harness.GetSessionIdAsync(deadline.Token);

            await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
            await events.ReceiveFrameAsync(deadline.Token);
            await harness.SubmitTurnAsync(sessionId, "apply the patch", deadline.Token);
            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.status" &&
                         frame.GetProperty("state").GetString() == "idle",
                deadline.Token);

            var types = frames.Select(frame => frame.GetProperty("type").GetString()).ToArray();
            Assert.Contains("tool.started", types);
            Assert.Contains("tool.finished", types);
            Assert.Equal("apply_patch", frames.Single(frame => frame.GetProperty("type").GetString() == "tool.started")
                .GetProperty("tool").GetString());
            Assert.Equal("tool-backed answer", frames.Single(frame => frame.GetProperty("type").GetString() == "text.delta")
                .GetProperty("text").GetString());

            // The wire carries the built-in apply_patch tool and neither of the
            // general edit functions it replaces.
            Assert.Equal(2, provider.RequestBodies.Count());
            var firstRequest = provider.RequestBodies.First();
            var toolTypes = firstRequest.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.TryGetProperty("type", out var type) ? type.GetString() : null)
                .ToArray();
            Assert.Contains("apply_patch", toolTypes);
            Assert.DoesNotContain(firstRequest.GetProperty("tools").EnumerateArray(), tool => tool.TryGetProperty("name", out _) &&
                               (tool.GetProperty("name").GetString() == "write_text" ||
                                tool.GetProperty("name").GetString() == "edit_text"));

            // The follow-up request replays the call in its original structured
            // shape and carries the structured result envelope as its output.
            var secondRequest = provider.RequestBodies.Last();
            Assert.Contains("apply_patch_call", secondRequest.GetRawText());
            Assert.Contains("apply_patch_call_output", secondRequest.GetRawText());
            Assert.Contains("patched.txt", secondRequest.GetRawText());

            Assert.Equal("patched body\n", await File.ReadAllTextAsync(
                Path.Combine(workspace, "patched.txt"),
                deadline.Token));
        }
        finally
        {
            await harness.DisposeAsync();
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task NotebookSwitchesBetweenOpenAiAndAnthropicWithCanonicalHistory()
    {
        // 45 s inside the 60 s outer Fact timeout: the switch exercises two providers and a
        // canonical-history replay, while the outer attribute still fires with time to report.
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
        var openAi = new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, model: "test-model", answer: "openai answer");
        var anthropic = new FakeAnthropicServer("claude-test", "anthropic answer");
        var dualConfig = DualProviderConfigTemplate
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
            Assert.Equal("openai answer", transcript.GetProperty("turns")[0].GetProperty("messages")[1]
                .GetProperty("parts")[0].GetProperty("text").GetString());

            // Switch the model profile via a command cell; the next turn runs on Anthropic.
            var switchResponse = await harness.Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "%model use claude-profile" },
                deadline.Token);
            Assert.Equal(HttpStatusCode.OK, switchResponse.StatusCode);
            var switchBody = await switchResponse.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            Assert.Contains("claude-profile", switchBody.GetProperty("markdown").GetString());

            await harness.SubmitTurnAsync(sessionId, "second turn", deadline.Token);
            var reloaded = await WaitForTranscriptTurnsAsync(
                harness, sessionId, minimumTurns: 2, deadline.Token);
            Assert.Equal("anthropic answer", reloaded.GetProperty("turns")[1].GetProperty("messages")[1]
                .GetProperty("parts")[0].GetProperty("text").GetString());
            // Canonical history: both turns committed in order with user→assistant shape.
            Assert.All(reloaded.GetProperty("turns").EnumerateArray()
                .Select(turn => turn.GetProperty("messages").EnumerateArray()
                    .Select(message => message.GetProperty("role").GetString()).ToArray()), roles => Assert.True(roles.First() == "user" && roles.Last() == "assistant"));
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
        // MCP configuration is a declared plugin data entrypoint now (ADR 0033): the
        // plugins root is redirected into the test sandbox and the root project (itself
        // a plugin) declares the data file.
        var pluginsRoot = Path.Combine(root, "plugins");
        Directory.CreateDirectory(pluginsRoot);
        var previousPluginsRoot = Environment.GetEnvironmentVariable("MAIEUTICS_PLUGINS_ROOT");
        Environment.SetEnvironmentVariable("MAIEUTICS_PLUGINS_ROOT", pluginsRoot);
        // The approval registry is redirected beside the sandbox and seeded for the
        // root project's mcp data entry (ADR 0037); without it the data file stays
        // pending and no MCP server is ever acquired.
        var previousApprovals = Environment.GetEnvironmentVariable("MAIEUTICS_PLUGIN_APPROVALS");
        string approvalsPath;
            await File.WriteAllTextAsync(
                Path.Combine(pluginsRoot, "maieutics.json"),
                """{ "entrypoints": { "mcp": "mcp.json" } }""",
                deadline.Token);
            await File.WriteAllTextAsync(
                Path.Combine(pluginsRoot, "mcp.json"),
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
                ConfigurationTemplate
                    .Replace("{{model}}", "test-model")
                    .Replace("{{endpoint}}", provider.Endpoint.ToString()),
                deadline.Token);

            approvalsPath = PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot);
            Environment.SetEnvironmentVariable("MAIEUTICS_PLUGIN_APPROVALS", approvalsPath);

            var harness = await FrontendHarness.StartAsync(
                deadline.Token, provider, hanging: false, configureBuilder: null);
            try
            {
            var sessionId = await harness.GetSessionIdAsync(deadline.Token);

            var mcpList = await harness.Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "%mcp list" },
                deadline.Token);
            Assert.Equal(HttpStatusCode.OK, mcpList.StatusCode);
            var markdown = (await mcpList.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
                .GetProperty("markdown").GetString()!;
            Assert.Contains("`echo` → `echo`", markdown);
            Assert.Contains("Connected", markdown);

            await harness.SubmitTurnAsync(sessionId, "call the MCP echo tool", deadline.Token);
            var transcript = await WaitForTranscriptTurnsAsync(
                harness, sessionId, minimumTurns: 1, deadline.Token);
            Assert.Equal("tool-backed answer", transcript.GetProperty("turns")[0].GetProperty("messages")[1]
                .GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Contains("status", provider.RequestBodies.Last().GetRawText());
            Assert.Contains("ok", provider.RequestBodies.Last().GetRawText());
            Assert.Contains("native mcp value", provider.RequestBodies.Last().GetRawText());
        }
        finally
        {
            await harness.DisposeAsync();
            Environment.SetEnvironmentVariable("MAIEUTICS_PLUGINS_ROOT", previousPluginsRoot);
            Environment.SetEnvironmentVariable("MAIEUTICS_PLUGIN_APPROVALS", previousApprovals);
            if (File.Exists(approvalsPath)) File.Delete(approvalsPath);
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    /// <summary>Deletes a temporary directory, retrying with an asynchronous backoff:
    /// the lock holder (watcher, exiting child) has no completion signal, so the
    /// retry waits by delay — asynchronously, never blocking a pool thread. Teardown gets
    /// its own budget, not the test token: after a timeout that token is canceled and
    /// would abort cleanup and leak the tree.</summary>
    private static Task DeleteDirectoryWithRetryAsync(string path) =>
        TempArea.DeleteAsync(path);

    [Fact(Timeout = 60_000)]
    public async Task SessionQueriesAreServedPerSessionWithLazyResolve()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartHostAsync(deadline.Token);
        using var client = harness.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/agent/capabilities", deadline.Token);
        Assert.True(capabilities.GetProperty("multiSession").GetBoolean());

        var bootId = await harness.GetSessionIdAsync(deadline.Token);

        // Unknown sessions fail with not_found.
        var missing = await client.GetAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/transcript",
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // A stored session that is not the foreground is still served: it is
        // lazily resumed. Starting a new session moves the foreground alias.
        await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{bootId}/rename",
            new { title = "boot" },
            deadline.Token);
        await client.PostAsync("/v1/agent/sessions", null, deadline.Token);
        var foreground = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", deadline.Token);
        Assert.NotEqual(bootId, foreground.GetProperty("id").GetString());

        var transcript = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{bootId}/transcript",
            deadline.Token);
        Assert.Equal(bootId, transcript.GetProperty("sessionId").GetString());
        Assert.Equal(0, transcript.GetProperty("turns").GetArrayLength());
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
        Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        var answered = await command.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal(first, answered.GetProperty("sessionId").GetString());
        Assert.Contains(first[..12], answered.GetProperty("markdown").GetString());

        // A session-switching command moves the foreground and the answer carries
        // the new session so the notebook re-pins.
        var switched = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{first}/turns",
            new { text = "%session new" },
            deadline.Token);
        var switchedBody = await switched.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.NotEqual(first, switchedBody.GetProperty("sessionId").GetString());
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
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(payload, await served.Content.ReadAsByteArrayAsync(deadline.Token));

        // A well-formed but unknown id is a typed not_found, and a non-hex id is a typed
        // invalid_request — never an unhandled 500.
        var missing = await client.GetAsync($"/v1/objects/{new string('a', 63)}A", deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var missingError = await missing.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("not_found", missingError.GetProperty("code").GetString());

        var invalid = await client.GetAsync($"/v1/objects/{new string('z', 64)}", deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidError = await invalid.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("invalid_request", invalidError.GetProperty("code").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task ResponsesWebSearchStreamsTheSearchAndItsCitation()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        var provider = new FakeOpenAiServer(OpenAiApiFlavor.Responses, webSearchFlow: true);
        var harness = await FrontendHarness.StartAsync(
            deadline.Token, provider, hanging: false, configureBuilder: null,
            transformConfiguration: body => DeclareEndpointCapability(
                body.Replace("ChatCompletions", "Responses"),
                provider.Endpoint,
                "WebSearch"));
        try
        {
            var sessionId = await harness.GetSessionIdAsync(deadline.Token);

            await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
            await events.ReceiveFrameAsync(deadline.Token);
            await harness.SubmitTurnAsync(sessionId, "search zhipu ai", deadline.Token);
            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.status" &&
                         frame.GetProperty("state").GetString() == "idle",
                deadline.Token);

            Assert.Contains("run.completed", frames.Select(frame => frame.GetProperty("type").GetString()));

            // The request declares the provider's web search tool; it is server-executed, so no
            // local function is registered for it.
            Assert.Contains("web_search", provider.RequestBodies.First().GetProperty("tools").EnumerateArray()
                .Where(tool => tool.TryGetProperty("type", out _))
                .Select(tool => tool.GetProperty("type").GetString()));

            // The canonical transcript records the cited answer.
            var transcript = (await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                deadline.Token)).GetRawText();
            Assert.Contains("Zhipu AI is a Chinese AI company.", transcript);
            Assert.Contains("https://example.com/zhipu", transcript);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ResponsesBuiltinCallsBeyondTheEndpointLimitFailTheTurn()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        var provider = new FakeOpenAiServer(OpenAiApiFlavor.Responses, webSearchFlow: true, webSearchCalls: 2);
        var harness = await FrontendHarness.StartAsync(
            deadline.Token, provider, hanging: false, configureBuilder: null,
            transformConfiguration: body => DeclareEndpointCapabilityWithLimit(
                body.Replace("ChatCompletions", "Responses"),
                provider.Endpoint,
                "WebSearch",
                limit: 1));
        try
        {
            var sessionId = await harness.GetSessionIdAsync(deadline.Token);

            await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
            await events.ReceiveFrameAsync(deadline.Token);
            await harness.SubmitTurnAsync(sessionId, "search zhipu ai", deadline.Token);
            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.failed",
                deadline.Token);

            // The response surfaced two built-in searches against a per-request ceiling of one;
            // the OpenAI wire has no cap parameter, so the client enforces it and fails the turn.
            Assert.Equal("agent_tool_limit_exceeded", frames.Last().GetProperty("code").GetString());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AnthropicWebSearchStreamsTheServerToolAndCommitsTheTranscript()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        var provider = new FakeAnthropicServer("claude-test", "tool-backed answer", webSearchFlow: true);
        var config = AnthropicConfigTemplate.Replace("{{endpoint}}", provider.Endpoint.ToString());
        var harness = await FrontendHarness.StartAsync(
            deadline.Token, provider, hanging: false, configureBuilder: null,
            transformConfiguration: _ => DeclareEndpointCapability(config, provider.Endpoint, "WebSearch"));
        try
        {
            var sessionId = await harness.GetSessionIdAsync(deadline.Token);

            await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
            await events.ReceiveFrameAsync(deadline.Token);
            await harness.SubmitTurnAsync(sessionId, "search zhipu ai", deadline.Token);
            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.status" &&
                         frame.GetProperty("state").GetString() == "idle",
                deadline.Token);

            Assert.Contains("run.completed", frames.Select(frame => frame.GetProperty("type").GetString()));

            // The request declares Anthropic's server tool by its versioned type; function tools
            // carry no type field, so the projection skips them.
            Assert.Contains("web_search_20250305", provider.RequestBodies.First().GetProperty("tools").EnumerateArray()
                .Where(tool => tool.TryGetProperty("type", out _))
                .Select(tool => tool.GetProperty("type").GetString()));

            var transcript = (await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                deadline.Token)).GetRawText();
            Assert.Contains("Zhipu AI is a Chinese AI company.", transcript);
            Assert.Contains("zhipu ai", transcript);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }


    /// <summary>The harness points at a loopback endpoint, and an unknown endpoint is assumed to
    /// host nothing, so a test that needs a hosted capability declares it explicitly here.</summary>
    private static string DeclareEndpointCapability(string body, Uri endpoint, string capability)
    {
        var withoutTrailingSlash = endpoint.ToString().TrimEnd('/');
        // Anchor on the Maieutics section itself: the capability table sits beside Sources,
        // Profiles, and Model, so every template shape can carry it.
        return body.Replace(
            "\"Maieutics\": {",
            "\"Maieutics\": {\n            \"Endpoints\": [ { \"Url\": " +
            $"{JsonSerializer.Serialize(withoutTrailingSlash)}, \"Capabilities\": [ \"{capability}\" ] }} ],");
    }

    private static string DeclareEndpointCapabilityWithLimit(string body, Uri endpoint, string capability, int limit)
    {
        var withoutTrailingSlash = endpoint.ToString().TrimEnd('/');
        return body.Replace(
            "\"Maieutics\": {",
            "\"Maieutics\": {\n            \"Endpoints\": [ { \"Url\": " +
            $"{JsonSerializer.Serialize(withoutTrailingSlash)}, \"Capabilities\": [ \"{capability}\" ], " +
            $"\"Limits\": {{ \"MaxBuiltinToolCalls\": {limit} }} }} ],");
    }


    private static CancellationTokenSource CreateDeadline(
        CancellationToken cancellationToken,
        TimeSpan timeout) => TestDeadlines.Create(cancellationToken, timeout);

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
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Hanging, answer: "late"),
            hanging: true);
    }

    /// <summary>Boots the composition root in process with the frontend API enabled and a
    /// fake model provider. Shared with the comm-plane integration tests.</summary>
    internal sealed class FrontendHarness : IAsyncDisposable
    {
        private readonly IHost host;
        private readonly IAsyncDisposable provider;
        private readonly string configurationFile;
        private readonly string dataRoot;
        private readonly GatedModelProvider? hangingProvider;

        private FrontendHarness(
            IHost host,
            IAsyncDisposable provider,
            string configurationFile,
            string dataRoot,
            string discoveryPath,
            JsonElement discovery,
            bool hanging)
        {
            this.host = host;
            this.provider = provider;
            this.configurationFile = configurationFile;
            this.dataRoot = dataRoot;
            hangingProvider = hanging ? (GatedModelProvider)provider : null;
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
        public Persistence.ObjectStore ObjectStore =>
            host.Services.GetRequiredService<Persistence.ObjectStore>();

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
                ?? (provider as GatedModelProvider)?.Endpoint.ToString() ?? string.Empty;
            var configurationFileBody = ConfigurationTemplate
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

            // Isolate persistent data: the composition root registers ApplicationPaths
            // unconditionally, so without this override the host resolves sessions against
            // the machine's real agent families root — an unknown-session lookup would
            // enumerate and open every real family database (slow, and SQLITE_CANTOPEN
            // under load or stale locks surfaces as an untyped 500).
            var dataRoot = Path.Combine(
                Path.GetTempPath(),
                $"maieutics-frontend-data-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataRoot);
            var host = MaieuticsHost.CreateApplication(
            [
                "--config", configurationFile,
                "--frontend-discovery", discoveryPath
            ], builder =>
            {
                var real = ApplicationPaths.Resolve();
                builder.Services.RemoveAll<ApplicationPaths>();
                builder.Services.AddSingleton(new ApplicationPaths(
                    dataRoot,
                    real.CacheRoot,
                    real.RuntimeRoot,
                    real.TempRoot));
                configureBuilder?.Invoke(builder);
            });
            await host.StartAsync(cancellationToken);

            // The host publishes the discovery file after Kestrel binds; a file that exists
            // but cannot be opened yet (Windows scanner lock on the fresh rename) is retried
            // rather than surfaced as an IOException at the readiness probe.
            var discoveryElement = await FrontendDiscoveryFile.ReadAsync(
                discoveryPath,
                TimeSpan.FromSeconds(20),
                cancellationToken);

            return new FrontendHarness(
                host,
                provider,
                configurationFile,
                dataRoot,
                discoveryPath,
                discoveryElement,
                hanging);
        }

        /// <summary>Registers the test process as the owning peer of a session so a raw
        /// child socket stub can pass the /comm handshake.</summary>
        public void RegisterControlPeer(string sessionId) =>
            host.Services.GetRequiredService<Control.ReplControlSessionRegistry>()
                .Register(Environment.ProcessId, sessionId);

        /// <summary>The live frontend session service, for direct service-level tests
        /// that need precise control over inputs such as cancellation tokens.</summary>
        public FrontendSessionService SessionService =>
            host.Services.GetRequiredService<FrontendSessionService>();

        /// <summary>The live session manager, so tests can observe eviction-pin state
        /// that the server-side turn queue drives (ADR 0025).</summary>
        public Commands.MaieuticsAgentSessionManager SessionManager =>
            host.Services.GetRequiredService<Commands.MaieuticsAgentSessionManager>();

        /// <summary>The live turn queue, so tests can arm its worker seams (the
        /// queue.updated liveness contract, ADR 0025).</summary>
        public FrontendTurnQueue TurnQueue =>
            host.Services.GetRequiredService<FrontendTurnQueue>();

        /// <summary>The live runtime configuration, so tests can await hot-reload completion
        /// (each completed reload swaps a fresh completion task) instead of polling the
        /// status surface.</summary>
        public Configuration.MaieuticsRuntimeConfiguration RuntimeConfiguration =>
            host.Services.GetRequiredService<Configuration.MaieuticsRuntimeConfiguration>();

        /// <summary>The IDE request registry, so endpoint tests can place pending requests
        /// and complete them through the real answer route (ADR 0042).</summary>
        public IdeRequestRegistry IdeRequests =>
            host.Services.GetRequiredService<IdeRequestRegistry>();

        /// <summary>The live IDE announcement gate, so tests observe the per-socket
        /// capability lifecycle without driving a run.</summary>
        public Configuration.IIdeFrontendGate IdeGate =>
            host.Services.GetRequiredService<Configuration.IIdeFrontendGate>();

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
            host.Services.GetRequiredService<Control.ReplControlHost>().ControlAddress;

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
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
                ;
            return body.GetProperty("runId").GetString()!;
        }

        public async Task<FrontendEventsConnection> OpenEventsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            long sinceSequence = 0,
            bool announceIde = false)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
            var ide = announceIde ? "&ide=1" : string.Empty;
            await socket.ConnectAsync(
                new Uri(
                    $"{Url.Replace("http://", "ws://")}/v1/agent/sessions/{sessionId}/events?sinceSequence={sinceSequence}{ide}"),
                cancellationToken);
            return new FrontendEventsConnection(socket);
        }

        public void ReleaseProvider() => hangingProvider?.Release();

        public async Task WaitForTurnCommittedAsync(
            string sessionId,
            CancellationToken cancellationToken,
            string? runId = null)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var transcript = await Client.GetFromJsonAsync<JsonElement>(
                    $"/v1/agent/sessions/{sessionId}/transcript",
                    wait.Token);
                if (transcript.GetProperty("turns").GetArrayLength() > 0) return;

                // A run that already settled can never commit a turn, so polling until the
                // budget expires only rewrites the real cause as an empty
                // TaskCanceledException. Name it instead: the run's retained stream carries
                // the protocol code the pump published. The two reads race the commit (the
                // GET may land just before it and the settled check just after), so the
                // verdict is only trustworthy after one fresh re-read in
                // settled-first order.
                if (runId is not null && DescribeSettledRunWithoutCommit(runId) is { } settled)
                {
                    var confirmation = await Client.GetFromJsonAsync<JsonElement>(
                        $"/v1/agent/sessions/{sessionId}/transcript",
                        wait.Token);
                    if (confirmation.GetProperty("turns").GetArrayLength() > 0) return;

                    var retained = SessionService.TryGetRun(runId, out var settledStream) && settledStream is not null
                        ? string.Join(" | ", settledStream.DescribeFrames())
                        : "(evicted)";
                    var providerState = hangingProvider?.DescribeState() ?? "(none)";
                    throw new InvalidOperationException(
                        $"Run {runId} settled ({settled}) without committing a turn to session {sessionId}; "
                        + $"the turn can never appear in the transcript. transcript={confirmation.GetRawText()} "
                        + $"provider={providerState} frames=[{retained}]");
                }

                await Task.Delay(50, wait.Token);
            }
        }

        /// <summary>Describes a run that has already settled, or returns null when it is
        /// still in flight (or evicted from the retained registry). Converts a bare wait
        /// timeout into the run's actual outcome and protocol code.</summary>
        private string? DescribeSettledRunWithoutCommit(string runId)
        {
            if (!SessionService.TryGetRun(runId, out var stream) || stream is null) return null;

            var completion = stream.Completion;
            if (!completion.IsCompleted) return null;

            var outcome = completion.Status switch
            {
                TaskStatus.RanToCompletion => "completed",
                TaskStatus.Faulted => "faulted",
                TaskStatus.Canceled => "cancelled",
                _ => "pending"
            };
            if (completion.Exception?.GetBaseException() is not { } cause)
                return $"{outcome}, no failure recorded";

            var code = cause is AgentException agentException
                ? FrontendErrors.MapAgentException(agentException)
                : "agent_error";
            // A provider failure's interesting detail is its inner exception (the actual
            // HTTP/SSE/socket failure); without it the verdict names only the wrapper.
            var inner = cause.InnerException is { } wrapped
                ? $" inner={wrapped.GetType().Name}: {wrapped.Message}"
                : string.Empty;
            return $"{outcome}, code={code}, cause={cause.GetType().Name}: {cause.Message}{inner}";
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
                await TempArea.DeleteAsync(dataRoot);
            }
            catch (TimeoutException)
            {
                // A store connection can hold the directory briefly on Windows; the shared
                // retry rides that out, and a leftover temp directory must not mask the
                // test result.
            }
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
}
