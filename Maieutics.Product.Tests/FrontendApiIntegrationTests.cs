using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Configuration;
using Maieutics.Control;
using Maieutics.Frontend;
using Maieutics.Persistence;
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
        var harness = await StartHostAsync(TestContext.Current.CancellationToken);
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
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);

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
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        // One anonymous client per test is deliberate; it is disposed with the test, so
        // the socket-exhaustion concern behind the short-lived-client guidance does not apply.
        // ReSharper disable once ShortLivedHttpClient
        using var anonymous = new HttpClient();
        anonymous.BaseAddress = new Uri(harness.Url);
        using var forged = harness.CreateClient(token: "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/agent/session", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/v1/agent/session", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/status", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact(Timeout = 60_000)]
    public async Task CapabilitiesAndSessionDescribeTheActiveSession()
    {
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        using var client = harness.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/agent/capabilities", TestContext.Current.CancellationToken);
        Assert.Equal(1, capabilities.GetProperty("protocolVersion").GetInt32());
        var session = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", TestContext.Current.CancellationToken);
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
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        await using var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken);
        var hello = await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
        Assert.Equal("hello", hello.GetProperty("type").GetString());
        Assert.Equal(sessionId, hello.GetProperty("session").GetProperty("id").GetString());

        var runId = await harness.SubmitTurnAsync(sessionId, "hello", TestContext.Current.CancellationToken);
        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);
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
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        string runId;
        var liveTypes = new List<string>();
        await using (var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken))
        {
            await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
            runId = await harness.SubmitTurnAsync(sessionId, "hello", TestContext.Current.CancellationToken);
            liveTypes.AddRange((await events.CollectUntilAsync(
                    frame => frame.GetProperty("type").GetString() == "run.completed",
                    TestContext.Current.CancellationToken))
                .Select(frame => frame.GetProperty("type").GetString()!));
        }

        Assert.Contains("text.delta", liveTypes);

        // A reconnecting frontend that observed sequence 1 resumes after it and receives the
        // retained remainder exactly once and in order (the trailing run.status idle frame
        // follows run.completed but the collector stops at the terminal frame).
        await using var replay = await harness.OpenEventsAsync(
            sessionId,
            TestContext.Current.CancellationToken,
            sinceSequence: 1);
        await replay.ReceiveFrameAsync(TestContext.Current.CancellationToken);
        var replayed = await replay.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.completed",
            TestContext.Current.CancellationToken);
        Assert.Equal(["message.completed", "run.completed"], replayed.Select(frame => frame.GetProperty("type").GetString()));
        Assert.All(replayed.Select(frame => frame.TryGetProperty("runId", out var run) ? run.GetString() : null)
            .Where(value => value is not null), value => Assert.True(value == runId));
    }

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentTurnsAreRejectedAsBusy()
    {
        await using var harness = await StartHostWithHangingProviderAsync(TestContext.Current.CancellationToken);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        var first = await harness.SubmitTurnAsync(sessionId, "first", TestContext.Current.CancellationToken);
        var second = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/turns",
            new { text = "second" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var error = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
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
        await harness.WaitForTurnCommittedAsync(sessionId, TestContext.Current.CancellationToken, runId: first);
    }

    [Fact(Timeout = 60_000)]
    public async Task CancelTerminatesTheRunAndFramesCarryTheOutcome()
    {
        await using var harness = await StartHostWithHangingProviderAsync(TestContext.Current.CancellationToken);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        await using var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken);
        await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
        var runId = await harness.SubmitTurnAsync(sessionId, "first", TestContext.Current.CancellationToken);

        var cancel = await harness.Client.PostAsync(
            $"/v1/agent/runs/{runId}/cancel",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        harness.ReleaseProvider();

        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            TestContext.Current.CancellationToken);
        var failure = frames.Single(frame => frame.GetProperty("type").GetString() == "run.failed");
        Assert.Equal("run_cancelled", failure.GetProperty("code").GetString());
        Assert.Equal(runId, failure.GetProperty("runId").GetString());
        Assert.Equal(["run.failed", "run.status"], frames.Select(frame => frame.GetProperty("type").GetString()).TakeLast(2));

        var missing = await harness.Client.PostAsync(
            $"/v1/agent/runs/{Guid.NewGuid():N}/cancel",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact(Timeout = 60_000)]
    public async Task InputAnswersCompletePendingReplStdinRequests()
    {
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        var target = new FakePresentationTarget();
        var scope = harness.AttachPresentation(sessionId, target);
        using var waitDeadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        waitDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        var wait = scope.Sink.RequestInputAsync("Name:", password: false, waitDeadline.Token);

        var published = target.Published.Single(entry => entry.Type == "input.request");
        var requestId = published.Data.GetProperty("requestId").GetString();
        Assert.NotNull(requestId);

        var answer = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/{requestId}",
            new { value = "ghost" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

        Assert.Equal("ghost", await wait);

        // A second answer for the same id is a typed 404, and so is an unknown id.
        var duplicate = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/{requestId}",
            new { value = "again" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, duplicate.StatusCode);
        var duplicateError = await duplicate.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("not_found", duplicateError.GetProperty("code").GetString());

        var unknown = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/inputs/input-{Guid.NewGuid():N}",
            new { value = "x" },
            TestContext.Current.CancellationToken);
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
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, lateAnswer.StatusCode);
    }

    [Fact(Timeout = 60_000)]
    public async Task InputAnswersWithMissingOrMalformedBodiesAreRejected()
    {
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);

        var empty = await harness.Client.PostAsync(
            "/v1/agent/inputs/input-unknown",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var malformed = await harness.Client.PostAsync(
            "/v1/agent/inputs/input-unknown",
            new StringContent("not json", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact(Timeout = 60_000)]
    public async Task CanceledTurnStartAbortsTheHandshakeWithoutReservingTheSession()
    {
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        // The cancellation token only reaches the pre-run handshake: AgentSession
        // aborts before the session reservation, so the turn start leaves the
        // session free and the transcript untouched.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await harness.SessionService.StartTurnAsync(
                sessionId,
                "hello",
                new CancellationToken(canceled: true));
        });

        Assert.Equal(0, harness.SessionService.DescribeSession(sessionId).Turns);

        // The session still accepts a normal turn afterwards.
        var runId = await harness.SubmitTurnAsync(sessionId, "hello", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(runId));
        await harness.WaitForTurnCommittedAsync(sessionId, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 60_000)]
    public async Task ToolLoopStreamsToolActivityFramesAndFinalAnswer()
    {
        var provider = new GatedModelProvider(
            ModelApiFlavor.ChatCompletions,
            ProviderGateMode.Scripted,
            toolFlow: true,
            toolName: "echo",
            toolArgumentsJson: "{\"text\":\"hello\"}");
        var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, provider, hanging: false, configureBuilder: builder =>
            {
                builder.Services.RemoveAll<IReadOnlyList<AIFunction>>();
                builder.Services.AddSingleton<IReadOnlyList<AIFunction>>([FrontendTestFunctions.CreateEchoFunction()]);
            });
        try
        {
            var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

            await using var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken);
            await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
            var runId = await harness.SubmitTurnAsync(sessionId, "use echo", TestContext.Current.CancellationToken);

            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.status" &&
                         frame.GetProperty("state").GetString() == "idle",
                TestContext.Current.CancellationToken);

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
                TestContext.Current.CancellationToken);
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
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        var response = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/turns",
            new { text = "%status" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("### Maieutics status", body.GetProperty("markdown").GetString());
        // Command answers always carry the active session so frontends can re-pin.
        Assert.Equal(sessionId, body.GetProperty("sessionId").GetString());

        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "%session list" },
                TestContext.Current.CancellationToken))
            .StatusCode);

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, transcript.GetProperty("turns").GetArrayLength());
    }

    [Fact(Timeout = 60_000)]
    public async Task StatusCompletionAndSessionLifecycleEndpointsAnswer()
    {
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        using var client = harness.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/v1/status", TestContext.Current.CancellationToken);
        Assert.Contains("### Maieutics status", status.GetProperty("markdown").GetString());

        var completion = await client.PostAsJsonAsync(
            "/v1/agent/complete",
            new { text = "%se", cursor = 3 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var matches = (await completion.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("matches").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("%session", matches);

        var stored = await client.GetFromJsonAsync<JsonElement>("/v1/agent/sessions", TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Array, stored.ValueKind);

        // Persistence is wired in the composition root, so an unknown stored session is a
        // typed not-found error.
        var resume = await client.PostAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/resume",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resume.StatusCode);
        var resumeError = await resume.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("not_found", resumeError.GetProperty("code").GetString());

        var previousSessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        var newSession = await client.PostAsync("/v1/agent/sessions", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, newSession.StatusCode);
        var created = await newSession.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.NotEqual(previousSessionId, created.GetProperty("id").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task SessionsCarryDisplayMetadataAndRenameWithoutActivation()
    {
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        using var client = harness.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/agent/capabilities", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrEmpty(capabilities.GetProperty("workspaceRoot").GetString()));

        // Renaming the active session before any turn creates its zero-turn row.
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        var rename = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/rename",
            new { title = "Design review" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var renamed = await rename.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, renamed.GetProperty("id").GetString());
        Assert.Equal("Design review", renamed.GetProperty("title").GetString());

        // The active-session endpoint surfaces the stored title once set.
        var activeAfterRename = await client.GetFromJsonAsync<JsonElement>(
            "/v1/agent/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, activeAfterRename.GetProperty("id").GetString());
        Assert.Equal("Design review", activeAfterRename.GetProperty("title").GetString());

        var stored = await client.GetFromJsonAsync<JsonElement>("/v1/agent/sessions", TestContext.Current.CancellationToken);
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
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        // Over the cap is a typed invalid request; whitespace-only clears the title.
        var tooLong = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/rename",
            new { title = new string('x', 201) },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var error = await tooLong.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid_request", error.GetProperty("code").GetString());

        var invalid = await client.PostAsJsonAsync(
            "/v1/agent/sessions/not-a-guid/rename",
            new { title = "x" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var clear = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/rename",
            new { title = "   " },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var cleared = await clear.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        // Nulls are omitted on write (protocol convention), so a cleared title is absent.
        Assert.False(cleared.TryGetProperty("title", out _));

        // The active session endpoint carries no title once cleared.
        var active = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, active.GetProperty("id").GetString());
        Assert.False(active.TryGetProperty("title", out _));
    }

    [Fact(Timeout = 60_000)]
    public async Task ForkRewindsToACommittedTurnAndActivatesTheNewHead()
    {
        // Two committed turns need two provider requests; the fake server serves exactly
        // the configured count and stops answering after that.
        await using var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: Answer, requestCount: 2),
            hanging: false);
        using var client = harness.CreateClient();

        var sourceId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        await harness.SubmitTurnAsync(sourceId, "first question", TestContext.Current.CancellationToken);
        // The single-run gate rejects concurrent turns; wait for the first to commit.
        await WaitForTranscriptTurnsAsync(harness, sourceId, minimumTurns: 1, TestContext.Current.CancellationToken);
        await harness.SubmitTurnAsync(sourceId, "second question", TestContext.Current.CancellationToken);
        var source = await WaitForTranscriptTurnsAsync(harness, sourceId, minimumTurns: 2, TestContext.Current.CancellationToken);
        var firstRunId = source.GetProperty("turns")[0].GetProperty("runId").GetString()!;

        // Fork by run id: the fork keeps the turns before the referenced one and re-runs it.
        // Referencing the first turn keeps zero turns — the branch point is turn 1.
        var byRun = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { runId = firstRunId },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, byRun.StatusCode);
        var fork = await byRun.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var forkId = fork.GetProperty("id").GetString()!;
        Assert.NotEqual(sourceId, forkId);
        // Auto-title from the source preview plus the branch point.
        Assert.Equal("first question · branch @ turn 1", fork.GetProperty("title").GetString());

        // The fork is the active session and its history ends at the fork point.
        var active = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", TestContext.Current.CancellationToken);
        Assert.Equal(forkId, active.GetProperty("id").GetString());
        Assert.Equal(0, active.GetProperty("turns").GetInt32());
        var forkTranscript = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{forkId}/transcript",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, forkTranscript.GetProperty("turns").GetArrayLength());

        // The stored list carries the lineage.
        var stored = await client.GetFromJsonAsync<JsonElement>("/v1/agent/sessions", TestContext.Current.CancellationToken);
        var head = stored.EnumerateArray().Single(session => session.GetProperty("id").GetString() == forkId);
        Assert.Equal(sourceId, head.GetProperty("parentSessionId").GetString());
        Assert.Equal(0, head.GetProperty("forkPointSeq").GetInt32());

        // Forking by seq keeps one committed turn and works for a non-active source too.
        var bySeq = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { seq = 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, bySeq.StatusCode);
        var second = await bySeq.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("first question · branch @ turn 2", second.GetProperty("title").GetString());

        // Typed failures: an out-of-range seq, an unknown run, an unknown session.
        var outOfRange = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { seq = 99 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        var rangeError = await outOfRange.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid_request", rangeError.GetProperty("code").GetString());

        var unknownRun = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { runId = Guid.NewGuid().ToString("N") },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknownRun.StatusCode);

        var noBody = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sourceId}/fork",
            new { },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);

        var unknownSource = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/fork",
            new { seq = 0 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknownSource.StatusCode);
    }

    [Fact(Timeout = 60_000)]
    public async Task RunCompletedCarriesModelIdentityAndUsage()
    {
        // The Responses flavor's final event carries the provider usage; the
        // chat-completions flavor omits it unless the client asks for it.
        var provider = new GatedModelProvider(ModelApiFlavor.Responses, ProviderGateMode.Scripted, answer: Answer);
        await using var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, provider, hanging: false,
            transformConfiguration: body => body.Replace("ChatCompletions", "Responses"));
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        await using var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken);
        await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
        await harness.SubmitTurnAsync(sessionId, "count my tokens", TestContext.Current.CancellationToken);
        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            TestContext.Current.CancellationToken);

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
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        using var client = harness.CreateClient();

        var profiles = await client.GetFromJsonAsync<JsonElement>("/v1/model/profiles", TestContext.Current.CancellationToken);
        Assert.True(profiles.GetArrayLength() > 0);
        Assert.Equal(1, profiles.EnumerateArray().Count(profile => profile.GetProperty("selected").GetBoolean()));
        Assert.Equal("OpenAI", profiles[0].GetProperty("provider").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task ForkAppliesTheRequestedProfileOverride()
    {
        var openAi = new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "openai answer");
        var anthropic = new GatedModelProvider(ModelApiFlavor.Anthropic, ProviderGateMode.Scripted, model: "claude-test", answer: "anthropic answer");
        var dualConfig = DualProviderConfigTemplate
            .Replace("{{openaiEndpoint}}", openAi.Endpoint.ToString())
            .Replace("{{anthropicEndpoint}}", anthropic.Endpoint.ToString());
        await using var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, openAi, hanging: false,
            transformConfiguration: _ => dualConfig);
        using var client = harness.CreateClient();

        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        await harness.SubmitTurnAsync(sessionId, "first turn", TestContext.Current.CancellationToken);
        await harness.WaitForTurnCommittedAsync(sessionId, TestContext.Current.CancellationToken);

        var fork = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/fork",
            new { seq = 0, profileId = "claude-profile" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, fork.StatusCode);
        var forkBody = await fork.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var forkId = forkBody.GetProperty("id").GetString()!;

        // The override is the fork's own: its next turn runs on Anthropic while
        // the process selection stays on the configured default (OpenAI).
        await harness.SubmitTurnAsync(forkId, "second turn", TestContext.Current.CancellationToken);
        await harness.WaitForTurnCommittedAsync(forkId, TestContext.Current.CancellationToken);
        var forkTranscript = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{forkId}/transcript",
            TestContext.Current.CancellationToken);
        Assert.Equal("anthropic answer", forkTranscript.GetProperty("turns")[0].GetProperty("messages")[1]
            .GetProperty("parts")[0].GetProperty("text").GetString());

        var profiles = await client.GetFromJsonAsync<JsonElement>("/v1/model/profiles", TestContext.Current.CancellationToken);
        Assert.True(profiles.EnumerateArray()
            .Single(profile => profile.GetProperty("id").GetString() == "openai-profile")
            .GetProperty("selected").GetBoolean());

        var unknown = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/fork",
            new { seq = 0, profileId = "no-such-profile" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var error = await unknown.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid_request", error.GetProperty("code").GetString());

        // A failed fork applies no model side effect: the process selection is
        // untouched after the rejected request (overrides are per session).
        var after = await client.GetFromJsonAsync<JsonElement>("/v1/model/profiles", TestContext.Current.CancellationToken);
        Assert.True(after.EnumerateArray()
            .Single(profile => profile.GetProperty("id").GetString() == "openai-profile")
            .GetProperty("selected").GetBoolean());
    }

    [Fact(Timeout = 60_000)]
    public async Task AddressedModelCommandsLazilyResumeStoredSessions()
    {
        var openAi = new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "openai answer");
        var anthropic = new GatedModelProvider(ModelApiFlavor.Anthropic, ProviderGateMode.Scripted, model: "claude-test", answer: "anthropic answer");
        var dualConfig = DualProviderConfigTemplate
            .Replace("{{openaiEndpoint}}", openAi.Endpoint.ToString())
            .Replace("{{anthropicEndpoint}}", anthropic.Endpoint.ToString());
        await using var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, openAi, hanging: false,
            transformConfiguration: _ => dualConfig);
        using var client = harness.CreateClient();

        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        await harness.SubmitTurnAsync(sessionId, "first turn", TestContext.Current.CancellationToken);
        await harness.WaitForTurnCommittedAsync(sessionId, TestContext.Current.CancellationToken);

        // Churn the foreground past the live capacity so the addressed session is
        // evicted from the live set (it stays resumable in storage).
        for (var index = 0; index < 8; index++)
        {
            await client.PostAsync("/v1/agent/sessions", null, TestContext.Current.CancellationToken);
        }

        // An addressed %model use lazily resumes the session and applies the
        // override; the next turn runs on Anthropic.
        var use = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/turns",
            new { text = "%model use claude-profile" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, use.StatusCode);

        await harness.SubmitTurnAsync(sessionId, "second turn", TestContext.Current.CancellationToken);
        var transcript = await WaitForTranscriptTurnsAsync(
            harness, sessionId, minimumTurns: 2, TestContext.Current.CancellationToken);
        Assert.Equal("anthropic answer", transcript.GetProperty("turns")[1].GetProperty("messages")[1]
            .GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task ResponsesSummaryStreamsOnceAndReplaysWithoutExposingProtectedState()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        var provider = new GatedModelProvider(
            ModelApiFlavor.Responses, ProviderGateMode.Scripted,
            answer: "public answer", reasoning: "Check the relevant evidence.");
        await using var harness = await FrontendHarness.StartAsync(
            deadline.Token, provider, hanging: false,
            transformConfiguration: configuration => configuration.Replace(
                "\"ApiFlavor\": \"ChatCompletions\"",
                "\"ApiFlavor\": \"Responses\", \"RequestReasoningSummary\": true"));
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
        await events.ReceiveFrameAsync(deadline.Token);
        await harness.SubmitTurnAsync(sessionId, "think", deadline.Token);
        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);
        var request = Assert.Single(provider.RequestBodies);
        Assert.Equal("concise", request.GetProperty("reasoning").GetProperty("summary").GetString());
        var summary = Assert.Single(frames, frame => frame.GetProperty("type").GetString() == "reasoning.delta");
        Assert.Equal("Check the relevant evidence.", summary.GetProperty("text").GetString());
        Assert.False(string.IsNullOrEmpty(summary.GetProperty("messageId").GetString()));
        var answer = frames.First(frame => frame.GetProperty("type").GetString() == "text.delta");
        Assert.True(summary.GetProperty("sequence").GetInt64() < answer.GetProperty("sequence").GetInt64());
        Assert.DoesNotContain("opaque-replay-state", string.Concat(frames.Select(frame => frame.GetRawText())));
        Assert.DoesNotContain("private-response-trace", string.Concat(frames.Select(frame => frame.GetRawText())));
        await using var replay = await harness.OpenEventsAsync(
            sessionId, deadline.Token,
            sinceSequence: summary.GetProperty("sequence").GetInt64() - 1);
        await replay.ReceiveFrameAsync(deadline.Token);
        var replayed = await replay.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.completed",
            deadline.Token);
        Assert.Equal(summary.GetRawText(), Assert.Single(replayed,
            frame => frame.GetProperty("type").GetString() == "reasoning.delta").GetRawText());
        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript", deadline.Token);
        Assert.DoesNotContain("Check the relevant evidence.", transcript.GetRawText());
        Assert.DoesNotContain("opaque-replay-state", transcript.GetRawText());
        Assert.Contains("public answer", transcript.GetRawText());
    }

    [Fact(Timeout = 60_000)]
    public async Task ReasoningContentStaysOutOfTheFrontendSurface()
    {
        var provider = new GatedModelProvider(
            ModelApiFlavor.ChatCompletions,
            ProviderGateMode.Scripted,
            answer: "public answer",
            reasoning: "secret chain of thought");
        await using var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, provider, hanging: false);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        await using var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken);
        await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
        await harness.SubmitTurnAsync(sessionId, "think", TestContext.Current.CancellationToken);
        var frames = await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            TestContext.Current.CancellationToken);

        var allText = string.Concat(frames
            .Select(frame => frame.TryGetProperty("text", out var text) ? text.GetString() : null)
            .Where(value => value is not null));
        Assert.DoesNotContain("secret chain of thought", allText);
        var completed = frames.Single(frame => frame.GetProperty("type").GetString() == "message.completed");
        Assert.DoesNotContain("secret chain of thought", completed.GetProperty("agentMessage").GetRawText());

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript",
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret chain of thought", transcript.GetRawText());
        Assert.Contains("public answer", transcript.GetRawText());
    }

    [Fact(Timeout = 60_000)]
    public async Task ConfigurationReloadSwitchesTheProviderForTheNextTurn()
    {
        var firstProvider = new GatedModelProvider(
            ModelApiFlavor.ChatCompletions,
            ProviderGateMode.Scripted,
            model: "model-one",
            answer: "answer from model one");
        var secondProvider = new GatedModelProvider(
            ModelApiFlavor.ChatCompletions,
            ProviderGateMode.Scripted,
            model: "model-two",
            answer: "answer from model two");
        await using var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, firstProvider, hanging: false, model: "model-one");
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        await harness.SubmitTurnAsync(sessionId, "first", TestContext.Current.CancellationToken);
        var firstTranscript = await WaitForTranscriptTurnsAsync(
            harness, sessionId, minimumTurns: 1, TestContext.Current.CancellationToken);
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
            TestContext.Current.CancellationToken);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        await runtimeConfiguration.WaitForVersionAsync(beforeVersion + 1, wait.Token);
        var status = await harness.Client.GetFromJsonAsync<JsonElement>("/v1/status", wait.Token);
        Assert.Contains("model-two", status.GetProperty("markdown").GetString());

        await harness.SubmitTurnAsync(sessionId, "second", TestContext.Current.CancellationToken);
        var reloaded = await WaitForTranscriptTurnsAsync(
            harness, sessionId, minimumTurns: 2, TestContext.Current.CancellationToken);
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
        var provider = new GatedModelProvider(ModelApiFlavor.Anthropic, ProviderGateMode.Scripted, model: "claude-test", answer: "tool-backed answer", toolFlow: true);
        var anthropicEndpoint = provider.Endpoint.ToString();
        var anthropicConfig = AnthropicConfigTemplate.Replace("{{endpoint}}", anthropicEndpoint);
        var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, provider, hanging: false, configureBuilder: builder =>
            {
                builder.Services.RemoveAll<IReadOnlyList<AIFunction>>();
                builder.Services.AddSingleton<IReadOnlyList<AIFunction>>([FrontendTestFunctions.CreateEchoFunction()]);
            }, transformConfiguration: _ => anthropicConfig);
        try
        {
            var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

            await using var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken);
            await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
            await harness.SubmitTurnAsync(sessionId, "use echo", TestContext.Current.CancellationToken);
            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.status" &&
                         frame.GetProperty("state").GetString() == "idle",
                TestContext.Current.CancellationToken);

            var types = frames.Select(frame => frame.GetProperty("type").GetString()).ToArray();
            Assert.Contains("tool.started", types);
            Assert.Contains("tool.finished", types);
            Assert.Equal("tool-backed answer", frames.Single(frame => frame.GetProperty("type").GetString() == "text.delta")
                .GetProperty("text").GetString());

            var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                TestContext.Current.CancellationToken);
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
        var operation = """{"type":"create_file","path":"patched.txt","diff":"+patched body\n"}""";
        var provider = new GatedModelProvider(
            ModelApiFlavor.Responses,
            ProviderGateMode.Scripted,
            applyPatchFlow: true,
            applyPatchOperationJson: operation,
            answer: "tool-backed answer");
        var workspace = Path.Combine(Path.GetTempPath(), $"maieutics-applypatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, provider, hanging: false, configureBuilder: null,
            transformConfiguration: body => body
                .Replace("ChatCompletions", "Responses")
                .Replace(
                    "\"Model\"",
                    $"\"Workspace\": {{ \"Root\": {JsonSerializer.Serialize(workspace)} }},\n            \"Model\""));
        try
        {
            var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

            await using var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken);
            await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
            await harness.SubmitTurnAsync(sessionId, "apply the patch", TestContext.Current.CancellationToken);
            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.status" &&
                         frame.GetProperty("state").GetString() == "idle",
                TestContext.Current.CancellationToken);

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
                TestContext.Current.CancellationToken));
        }
        finally
        {
            await harness.DisposeAsync();
            try
            {
                await TempArea.DeleteAsync(workspace, CancellationToken.None);
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
        var openAi = new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "openai answer");
        var anthropic = new GatedModelProvider(ModelApiFlavor.Anthropic, ProviderGateMode.Scripted, model: "claude-test", answer: "anthropic answer");
        var dualConfig = DualProviderConfigTemplate
            .Replace("{{openaiEndpoint}}", openAi.Endpoint.ToString())
            .Replace("{{anthropicEndpoint}}", anthropic.Endpoint.ToString());
        var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, openAi, hanging: false, configureBuilder: null,
            transformConfiguration: _ => dualConfig);
        try
        {
            var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

            await harness.SubmitTurnAsync(sessionId, "first turn", TestContext.Current.CancellationToken);
            var transcript = await WaitForTranscriptTurnsAsync(
                harness, sessionId, minimumTurns: 1, TestContext.Current.CancellationToken);
            Assert.Equal("openai answer", transcript.GetProperty("turns")[0].GetProperty("messages")[1]
                .GetProperty("parts")[0].GetProperty("text").GetString());

            // Switch the model profile via a command cell; the next turn runs on Anthropic.
            var switchResponse = await harness.Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "%model use claude-profile" },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, switchResponse.StatusCode);
            var switchBody = await switchResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Contains("claude-profile", switchBody.GetProperty("markdown").GetString());

            await harness.SubmitTurnAsync(sessionId, "second turn", TestContext.Current.CancellationToken);
            var reloaded = await WaitForTranscriptTurnsAsync(
                harness, sessionId, minimumTurns: 2, TestContext.Current.CancellationToken);
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

        var provider = new GatedModelProvider(
            ModelApiFlavor.ChatCompletions,
            ProviderGateMode.Scripted,
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
        await File.WriteAllTextAsync(
            Path.Combine(pluginsRoot, "maieutics.json"),
            """{ "entrypoints": { "mcp": "mcp.json" } }""",
            TestContext.Current.CancellationToken);
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
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            configurationFile,
            ConfigurationTemplate
                .Replace("{{model}}", "test-model")
                .Replace("{{endpoint}}", provider.Endpoint.ToString()),
            TestContext.Current.CancellationToken);

        var approvalsPath = PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot);
        Environment.SetEnvironmentVariable("MAIEUTICS_PLUGIN_APPROVALS", approvalsPath);

        var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, provider, hanging: false, configureBuilder: null);
        try
        {
            var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

            var mcpList = await harness.Client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "%mcp list" },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, mcpList.StatusCode);
            var markdown = (await mcpList.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
                .GetProperty("markdown").GetString()!;
            Assert.Contains("`echo` → `echo`", markdown);
            Assert.Contains("Connected", markdown);

            await harness.SubmitTurnAsync(sessionId, "call the MCP echo tool", TestContext.Current.CancellationToken);
            var transcript = await WaitForTranscriptTurnsAsync(
                harness, sessionId, minimumTurns: 1, TestContext.Current.CancellationToken);
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
        await using var harness = await StartHostAsync(TestContext.Current.CancellationToken);
        using var client = harness.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/agent/capabilities", TestContext.Current.CancellationToken);
        Assert.True(capabilities.GetProperty("multiSession").GetBoolean());

        var bootId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

        // Unknown sessions fail with not_found.
        var missing = await client.GetAsync(
            $"/v1/agent/sessions/{Guid.NewGuid():N}/transcript",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // A stored session that is not the foreground is still served: it is
        // lazily resumed. Starting a new session moves the foreground alias.
        await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{bootId}/rename",
            new { title = "boot" },
            TestContext.Current.CancellationToken);
        await client.PostAsync("/v1/agent/sessions", null, TestContext.Current.CancellationToken);
        var foreground = await client.GetFromJsonAsync<JsonElement>("/v1/agent/session", TestContext.Current.CancellationToken);
        Assert.NotEqual(bootId, foreground.GetProperty("id").GetString());

        var transcript = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{bootId}/transcript",
            TestContext.Current.CancellationToken);
        Assert.Equal(bootId, transcript.GetProperty("sessionId").GetString());
        Assert.Equal(0, transcript.GetProperty("turns").GetArrayLength());
    }

    [Fact(Timeout = 60_000)]
    public async Task SessionsRunConcurrentlyAndCommandsStaySessionScoped()
    {
        // Two in-flight turns on two sessions must both commit (the old
        // single-active semantics busy-rejected the second submission).
        await using var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: Answer, requestCount: 2),
            hanging: false);
        using var client = harness.CreateClient();

        var first = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        var second = await (await client.PostAsync("/v1/agent/sessions", null, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var secondId = second.GetProperty("id").GetString()!;

        var firstTurn = harness.SubmitTurnAsync(first, "turn on first", TestContext.Current.CancellationToken);
        var secondTurn = harness.SubmitTurnAsync(secondId, "turn on second", TestContext.Current.CancellationToken);
        await Task.WhenAll(firstTurn, secondTurn);
        await WaitForTranscriptTurnsAsync(harness, first, minimumTurns: 1, TestContext.Current.CancellationToken);
        await WaitForTranscriptTurnsAsync(harness, secondId, minimumTurns: 1, TestContext.Current.CancellationToken);

        // A command cell answers with its own session: no re-pin happens.
        var command = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{first}/turns",
            new { text = "%session current" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        var answered = await command.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(first, answered.GetProperty("sessionId").GetString());
        Assert.Contains(first[..12], answered.GetProperty("markdown").GetString());

        // A session-switching command moves the foreground and the answer carries
        // the new session so the notebook re-pins.
        var switched = await client.PostAsJsonAsync(
            $"/v1/agent/sessions/{first}/turns",
            new { text = "%session new" },
            TestContext.Current.CancellationToken);
        var switchedBody = await switched.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.NotEqual(first, switchedBody.GetProperty("sessionId").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task ObjectRouteNormalizesUppercaseIdsAndTypesInvalidIds()
    {
        // The object store (and the /v1/objects route) only exists when Agent persistence
        // is enabled; the transform turns it on for this host.
        await using var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: Answer),
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
        var served = await client.GetAsync($"/v1/objects/{sha256.ToUpperInvariant()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(payload, await served.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // A well-formed but unknown id is a typed not_found, and a non-hex id is a typed
        // invalid_request — never an unhandled 500.
        var missing = await client.GetAsync($"/v1/objects/{new string('a', 63)}A", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var missingError = await missing.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("not_found", missingError.GetProperty("code").GetString());

        var invalid = await client.GetAsync($"/v1/objects/{new string('z', 64)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidError = await invalid.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid_request", invalidError.GetProperty("code").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task ResponsesWebSearchStreamsTheSearchAndItsCitation()
    {
        var provider = new GatedModelProvider(ModelApiFlavor.Responses, ProviderGateMode.Scripted, webSearchFlow: true);
        var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, provider, hanging: false, configureBuilder: null,
            transformConfiguration: body => DeclareEndpointCapability(
                body.Replace("ChatCompletions", "Responses"),
                provider.Endpoint,
                "WebSearch"));
        try
        {
            var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

            await using var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken);
            await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
            await harness.SubmitTurnAsync(sessionId, "search zhipu ai", TestContext.Current.CancellationToken);
            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.status" &&
                         frame.GetProperty("state").GetString() == "idle",
                TestContext.Current.CancellationToken);

            Assert.Contains("run.completed", frames.Select(frame => frame.GetProperty("type").GetString()));

            // The request declares the provider's web search tool; it is server-executed, so no
            // local function is registered for it.
            Assert.Contains("web_search", provider.RequestBodies.First().GetProperty("tools").EnumerateArray()
                .Where(tool => tool.TryGetProperty("type", out _))
                .Select(tool => tool.GetProperty("type").GetString()));

            // The canonical transcript records the cited answer.
            var transcript = (await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                TestContext.Current.CancellationToken)).GetRawText();
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
        var provider = new GatedModelProvider(ModelApiFlavor.Responses, ProviderGateMode.Scripted, webSearchFlow: true, webSearchCalls: 2);
        var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, provider, hanging: false, configureBuilder: null,
            transformConfiguration: body => DeclareEndpointCapabilityWithLimit(
                body.Replace("ChatCompletions", "Responses"),
                provider.Endpoint,
                "WebSearch",
                limit: 1));
        try
        {
            var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

            await using var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken);
            await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
            await harness.SubmitTurnAsync(sessionId, "search zhipu ai", TestContext.Current.CancellationToken);
            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.failed",
                TestContext.Current.CancellationToken);

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
        var provider = new GatedModelProvider(ModelApiFlavor.Anthropic, ProviderGateMode.Scripted, model: "claude-test", answer: "tool-backed answer", webSearchFlow: true);
        var config = AnthropicConfigTemplate.Replace("{{endpoint}}", provider.Endpoint.ToString());
        var harness = await FrontendHarness.StartAsync(
            TestContext.Current.CancellationToken, provider, hanging: false, configureBuilder: null,
            transformConfiguration: _ => DeclareEndpointCapability(config, provider.Endpoint, "WebSearch"));
        try
        {
            var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);

            await using var events = await harness.OpenEventsAsync(sessionId, TestContext.Current.CancellationToken);
            await events.ReceiveFrameAsync(TestContext.Current.CancellationToken);
            await harness.SubmitTurnAsync(sessionId, "search zhipu ai", TestContext.Current.CancellationToken);
            var frames = await events.CollectUntilAsync(
                frame => frame.GetProperty("type").GetString() == "run.status" &&
                         frame.GetProperty("state").GetString() == "idle",
                TestContext.Current.CancellationToken);

            Assert.Contains("run.completed", frames.Select(frame => frame.GetProperty("type").GetString()));

            // The request declares Anthropic's server tool by its versioned type; function tools
            // carry no type field, so the projection skips them.
            Assert.Contains("web_search_20250305", provider.RequestBodies.First().GetProperty("tools").EnumerateArray()
                .Where(tool => tool.TryGetProperty("type", out _))
                .Select(tool => tool.GetProperty("type").GetString()));

            var transcript = (await harness.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                TestContext.Current.CancellationToken)).GetRawText();
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

    private static async Task<FrontendHarness> StartHostAsync(CancellationToken cancellationToken)
    {
        return await FrontendHarness.StartAsync(
            cancellationToken,
            new GatedModelProvider(
                ModelApiFlavor.ChatCompletions,
                ProviderGateMode.Scripted,
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
        private readonly IHost _host;
        private readonly IAsyncDisposable _provider;
        private readonly string _configurationFile;
        private readonly string _dataRoot;
        private readonly GatedModelProvider? _hangingProvider;

        private FrontendHarness(
            IHost host,
            IAsyncDisposable provider,
            string configurationFile,
            string dataRoot,
            string discoveryPath,
            JsonElement discovery,
            bool hanging)
        {
            _host = host;
            _provider = provider;
            _configurationFile = configurationFile;
            _dataRoot = dataRoot;
            _hangingProvider = hanging ? (GatedModelProvider)provider : null;
            DiscoveryPath = discoveryPath;
            Discovery = discovery;
            Client = CreateClient();
        }

        public string DiscoveryPath { get; }

        public string ConfigurationFile => _configurationFile;

        public JsonElement Discovery { get; }

        public HttpClient Client { get; }

        public string Url => Discovery.GetProperty("url").GetString()!;

        public string Token => Discovery.GetProperty("token").GetString()!;

        /// <summary>The content-addressed object store backing <c>/v1/objects</c>, so tests
        /// can ingest real objects without driving a full turn.</summary>
        public ObjectStore ObjectStore =>
            _host.Services.GetRequiredService<ObjectStore>();

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
            var endpoint = (provider as GatedModelProvider)?.Endpoint.ToString() ?? string.Empty;
            var configurationFileBody = ConfigurationTemplate
                .Replace("{{model}}", model)
                .Replace("{{endpoint}}", endpoint);
            if (transformConfiguration is not null)
            {
                // A named Sources/Profiles configuration cannot carry the legacy Model
                // section; the transform rewrites the file for those scenarios.
                configurationFileBody = transformConfiguration(configurationFileBody);
            }

            await File.WriteAllTextAsync(configurationFile, configurationFileBody, cancellationToken);
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
            _host.Services.GetRequiredService<ReplControlSessionRegistry>()
                .Register(Environment.ProcessId, sessionId);

        /// <summary>The live frontend session service, for direct service-level tests
        /// that need precise control over inputs such as cancellation tokens.</summary>
        public FrontendSessionService SessionService =>
            _host.Services.GetRequiredService<FrontendSessionService>();

        /// <summary>The live session manager, so tests can observe eviction-pin state
        /// that the server-side turn queue drives (ADR 0025).</summary>
        public MaieuticsAgentSessionManager SessionManager =>
            _host.Services.GetRequiredService<MaieuticsAgentSessionManager>();

        /// <summary>The live turn queue, so tests can arm its worker seams (the
        /// queue.updated liveness contract, ADR 0025).</summary>
        public FrontendTurnQueue TurnQueue =>
            _host.Services.GetRequiredService<FrontendTurnQueue>();

        /// <summary>The live runtime configuration, so tests can await hot-reload completion
        /// (each completed reload swaps a fresh completion task) instead of polling the
        /// status surface.</summary>
        public MaieuticsRuntimeConfiguration RuntimeConfiguration =>
            _host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();

        /// <summary>The IDE request registry, so endpoint tests can place pending requests
        /// and complete them through the real answer route (ADR 0042).</summary>
        public IdeRequestRegistry IdeRequests =>
            _host.Services.GetRequiredService<IdeRequestRegistry>();

        /// <summary>The live comm router, so tests can await a session plane's live-set
        /// transitions instead of probing with throwaway hello reads.</summary>
        public FrontendCommRouter CommRouter =>
            _host.Services.GetRequiredService<FrontendCommRouter>();

        /// <summary>The live IDE announcement gate, so tests observe the per-socket
        /// capability lifecycle without driving a run.</summary>
        public IIdeFrontendGate IdeGate =>
            _host.Services.GetRequiredService<IIdeFrontendGate>();

        /// <summary>Waits for the session's announcing sockets to lapse (the last one
        /// withdrawing is the completion). Capture the task before closing the socket:
        /// the underlying signal is re-armed by the next announce.</summary>
        public Task IdeLapsedAsync(string sessionId)
        {
            return ((FrontendIdeSockets)IdeGate).Lapsed(sessionId);
        }

        /// <summary>Attaches a test presentation target to the live REPL presentation
        /// router so tests can drive stdin-style input requests against the real
        /// input-answer endpoint.</summary>
        public FrontendDenoReplPresentationRouter.FrontendPresentationScope AttachPresentation(
            string sessionId,
            IFrontendPresentationTarget target)
        {
            return _host.Services.GetRequiredService<FrontendDenoReplPresentationRouter>()
                .Attach(new AgentSessionId(Guid.ParseExact(sessionId, "N")), target);
        }

        /// <summary>The control host address (the Unix socket path on Unix).</summary>
        public string ControlAddress =>
            _host.Services.GetRequiredService<ReplControlHost>().ControlAddress;

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

        public void ReleaseProvider() => _hangingProvider?.Release();

        public async Task WaitForTurnCommittedAsync(
            string sessionId,
            CancellationToken cancellationToken,
            string? runId = null)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(20));

            // Completion-driven: the run's settlement is the completion notification for
            // "the turn finished", and the transcript commit precedes it — CommitTurn
            // appends the turn before the run result exists, and the pump settles after
            // the terminal frame. With a runId the harness awaits that run's settlement;
            // without one (or when the run was evicted), the session's latest run's
            // settlement stands in. The transcript REST read then verifies the commit; a
            // settled run that still reads empty can never commit, so the failure names
            // itself instead of retrying into a bare cancellation.
            FrontendRunStream? settledStream = null;
            if (runId is not null && SessionService.TryGetRun(runId, out var stream) && stream is not null)
            {
                await stream.Settled.WaitAsync(wait.Token);
                settledStream = stream;
            }
            else if (Guid.TryParseExact(sessionId, "N", out var parsed) &&
                     SessionService.TryGetLatestRun(
                         new AgentSessionId(parsed),
                         out var latest) &&
                     latest is not null)
            {
                await latest.Settled.WaitAsync(wait.Token);
                settledStream = latest;
            }

            var transcript = await Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                wait.Token);
            if (transcript.GetProperty("turns").GetArrayLength() > 0) return;

            var confirmation = await Client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                wait.Token);
            if (confirmation.GetProperty("turns").GetArrayLength() > 0) return;

            var retained = settledStream is not null
                ? string.Join(" | ", settledStream.DescribeFrames())
                : "(evicted)";
            var providerState = _hangingProvider?.DescribeState() ?? "(none)";
            throw new InvalidOperationException(
                $"The turn of session {sessionId} never appeared in the transcript after its run "
                + $"settled; the turn can never appear. transcript={confirmation.GetRawText()} "
                + $"provider={providerState} frames=[{retained}]");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _host.StopAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
            }

            await ((IAsyncDisposable)_host).DisposeAsync();
            await _provider.DisposeAsync();
            try
            {
                await TempArea.DeleteAsync(_dataRoot, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // A store connection can hold the directory briefly on Windows; the shared
                // retry rides that out, and a leftover temp directory must not mask the
                // test result.
            }
            try
            {
                File.Delete(_configurationFile);
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
