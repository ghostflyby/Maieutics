using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maieutics.Providers.OpenAI;

namespace Maieutics.Product.Tests;

/// <summary>
///     The attachment flow end to end (docs/web-frontend-protocol.md, "Attachments"): the
///     upload endpoint publishes bytes into the object store and answers the content address,
///     the turn build path parses canonical markers into blob reference data parts, and a
///     committed marker reference keeps its object alive across <c>%session gc</c>-style
///     pruning.
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendObjectAttachmentTests
{
    [Fact(Timeout = 60_000)]
    public async Task UploadAnswersContentAddressAndServesTheBytesBack()
    {
        using var deadline = CreateDeadline(
            TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartPersistenceHostAsync(deadline.Token);
        var client = harness.Client;
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var payload = "attachment bytes"u8.ToArray();
        var response = await client.PostAsync(
            $"/v1/agent/sessions/{sessionId}/objects?name=shot.png",
            new ByteArrayContent(payload),
            deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var uploaded = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        var sha256 = uploaded.GetProperty("sha256").GetString();
        Assert.Matches("^[0-9a-f]{64}$", sha256);
        Assert.Equal(payload.Length, uploaded.GetProperty("byteLength").GetInt64());

        // The content address is the same object store the display-bundle GET serves.
        var served = await client.GetAsync($"/v1/objects/{sha256}", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(payload, await served.Content.ReadAsByteArrayAsync(deadline.Token));

        // Content addressing: identical bytes deduplicate to the same address.
        var again = await client.PostAsync(
            $"/v1/agent/sessions/{sessionId}/objects",
            new ByteArrayContent(payload),
            deadline.Token);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(sha256, (await again.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("sha256").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task UploadRejectsUnknownSessionAndOversizedPayloadTyped()
    {
        using var deadline = CreateDeadline(
            TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartPersistenceHostAsync(deadline.Token);
        var client = harness.Client;

        var unknown = await client.PostAsync(
            $"/v1/agent/sessions/{new string('e', 32)}/objects",
            new ByteArrayContent([1, 2, 3]),
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("not_found", (await unknown.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString());

        // The ingest bound is a typed 400, not a transport-level abort; the declared
        // Content-Length alone is enough to reject before the body is read.
        harness.SessionService.MaxIngestBytes = 8;
        var oversized = await client.PostAsync(
            "/v1/agent/sessions/anything/objects",
            new ByteArrayContent(new byte[9]),
            deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal("invalid_request", (await oversized.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task MarkerTurnCommitsTheTextPartAndOneBlobDataPartPerMarker()
    {
        using var deadline = CreateDeadline(
            TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        await using var harness = await StartPersistenceHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var first = harness.ObjectStore.Ingest(new MemoryStream([1, 2, 3]));
        var second = harness.ObjectStore.Ingest(new MemoryStream([9, 8, 7, 6]));
        // The third marker's display name carries the grammar's quote/backslash escapes;
        // the server must reverse them exactly like the extension does.
        var text = $"Look\n[[maieutics:object sha256={first.Sha256} mime=image/png name=\"shot.png\"]]\n"
            + $"[[maieutics:object sha256={second.Sha256} mime=application/pdf name=\"my \\\"shot\\\"\\\\v1.pdf\"]]";

        await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
        await events.ReceiveFrameAsync(deadline.Token);
        await harness.SubmitTurnAsync(sessionId, text, deadline.Token);
        await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript", deadline.Token);
        var user = transcript
            .GetProperty("turns")[0]
            .GetProperty("messages")[0];
        Assert.Equal("user", user.GetProperty("role").GetString());
        var parts = user.GetProperty("parts");
        Assert.Equal(3, parts.GetArrayLength());

        Assert.Equal("text", parts[0].GetProperty("kind").GetString());
        Assert.Equal("Look\n\n", parts[0].GetProperty("text").GetString());

        Assert.Equal("data", parts[1].GetProperty("kind").GetString());
        var firstValue = parts[1].GetProperty("value");
        Assert.Equal(first.Sha256, firstValue.GetProperty("sha256").GetString());
        Assert.Equal(3, firstValue.GetProperty("size").GetInt64());
        Assert.Equal("image/png", firstValue.GetProperty("mediaType").GetString());
        Assert.Equal("shot.png", firstValue.GetProperty("name").GetString());

        Assert.Equal("data", parts[2].GetProperty("kind").GetString());
        var secondValue = parts[2].GetProperty("value");
        Assert.Equal(second.Sha256, secondValue.GetProperty("sha256").GetString());
        Assert.Equal("application/pdf", secondValue.GetProperty("mediaType").GetString());
        Assert.Equal("my \"shot\"\\v1.pdf", secondValue.GetProperty("name").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task MarkerWhoseObjectIsMissingFailsTheSubmissionTyped()
    {
        using var deadline = CreateDeadline(
            TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await using var harness = await StartPersistenceHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var missing = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/turns",
            new { text = $"[[maieutics:object sha256={new string('a', 64)} mime=image/png name=\"gone.png\"]]" },
            deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task NonCanonicalLookalikeStaysLiteralText()
    {
        using var deadline = CreateDeadline(
            TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        await using var harness = await StartPersistenceHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        // Uppercase hex breaks the canonical grammar, so the whole line is ordinary text:
        // a user-written look-alike can never become a reference.
        var lookalike = $"[[maieutics:object sha256={new string('A', 64)} mime=image/png name=\"no.png\"]]";
        await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
        await events.ReceiveFrameAsync(deadline.Token);
        await harness.SubmitTurnAsync(sessionId, lookalike, deadline.Token);
        await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript", deadline.Token);
        var parts = transcript.GetProperty("turns")[0].GetProperty("messages")[0].GetProperty("parts");
        Assert.Equal(1, parts.GetArrayLength());
        Assert.Equal("text", parts[0].GetProperty("kind").GetString());
        Assert.Equal(lookalike, parts[0].GetProperty("text").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task QueuedMarkerTurnParsesAtDrainAndItsObjectSurvivesPrune()
    {
        using var deadline = CreateDeadline(
            TestContext.Current.CancellationToken, TimeSpan.FromSeconds(40));
        await using var harness = await StartPersistenceHostAsync(deadline.Token);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);

        var referenced = harness.ObjectStore.Ingest(new MemoryStream([5, 5, 5]));
        var unreferenced = harness.ObjectStore.Ingest(new MemoryStream([6, 6, 6]));
        var text = $"[[maieutics:object sha256={referenced.Sha256} mime=image/png name=\"q.png\"]]";

        await using var events = await harness.OpenEventsAsync(sessionId, deadline.Token);
        await events.ReceiveFrameAsync(deadline.Token);
        var enqueue = await harness.Client.PostAsJsonAsync(
            $"/v1/agent/sessions/{sessionId}/queue",
            new { items = new[] { new { text } } },
            deadline.Token);
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript", deadline.Token);
        var parts = transcript.GetProperty("turns")[0].GetProperty("messages")[0].GetProperty("parts");
        Assert.Equal(1, parts.GetArrayLength());
        Assert.Equal("data", parts[0].GetProperty("kind").GetString());
        Assert.Equal(referenced.Sha256, parts[0].GetProperty("value").GetProperty("sha256").GetString());

        // The committed marker's blob reference is the object's live set: pruning with no
        // grace removes the unreferenced upload and keeps the referenced one.
        Assert.True(harness.SessionManager.PruneObjects(TimeSpan.Zero) >= 1);
        Assert.True(harness.ObjectStore.Exists(referenced.Sha256));
        Assert.False(harness.ObjectStore.Exists(unreferenced.Sha256));
    }

    private static Task<FrontendApiIntegrationTests.FrontendHarness> StartPersistenceHostAsync(
        CancellationToken cancellationToken)
    {
        // The object store (and the attachment flow) only exists when Agent persistence is
        // enabled; the transform turns it on for this host.
        return FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            cancellationToken,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "seen"),
            hanging: false,
            transformConfiguration: body => body.Replace(
                "\"Maieutics\": {",
                """
                "Maieutics": {
                    "Agent": { "Persistence": { "Enabled": true } },
                """,
                StringComparison.Ordinal));
    }

    private static CancellationTokenSource CreateDeadline(
        CancellationToken cancellationToken,
        TimeSpan timeout) => TestDeadlines.Create(cancellationToken, timeout);
}
