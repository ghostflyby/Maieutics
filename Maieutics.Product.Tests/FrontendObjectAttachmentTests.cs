using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
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
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var uploaded = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        var sha256 = uploaded.GetProperty("sha256").GetString();
        sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        uploaded.GetProperty("byteLength").GetInt64().Should().Be(payload.Length);

        // The content address is the same object store the display-bundle GET serves.
        var served = await client.GetAsync($"/v1/objects/{sha256}", deadline.Token);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync(deadline.Token)).Should().Equal(payload);

        // Content addressing: identical bytes deduplicate to the same address.
        var again = await client.PostAsync(
            $"/v1/agent/sessions/{sessionId}/objects",
            new ByteArrayContent(payload),
            deadline.Token);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("sha256").GetString().Should().Be(sha256);
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
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await unknown.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString().Should().Be("not_found");

        // The ingest bound is a typed 400, not a transport-level abort; the declared
        // Content-Length alone is enough to reject before the body is read.
        harness.SessionService.MaxIngestBytes = 8;
        var oversized = await client.PostAsync(
            "/v1/agent/sessions/anything/objects",
            new ByteArrayContent(new byte[9]),
            deadline.Token);
        oversized.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await oversized.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString().Should().Be("invalid_request");
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
        user.GetProperty("role").GetString().Should().Be("user");
        var parts = user.GetProperty("parts");
        parts.GetArrayLength().Should().Be(3);

        parts[0].GetProperty("kind").GetString().Should().Be("text");
        parts[0].GetProperty("text").GetString().Should().Be("Look\n\n");

        parts[1].GetProperty("kind").GetString().Should().Be("data");
        var firstValue = parts[1].GetProperty("value");
        firstValue.GetProperty("sha256").GetString().Should().Be(first.Sha256);
        firstValue.GetProperty("size").GetInt64().Should().Be(3);
        firstValue.GetProperty("mediaType").GetString().Should().Be("image/png");
        firstValue.GetProperty("name").GetString().Should().Be("shot.png");

        parts[2].GetProperty("kind").GetString().Should().Be("data");
        var secondValue = parts[2].GetProperty("value");
        secondValue.GetProperty("sha256").GetString().Should().Be(second.Sha256);
        secondValue.GetProperty("mediaType").GetString().Should().Be("application/pdf");
        secondValue.GetProperty("name").GetString().Should().Be("my \"shot\"\\v1.pdf");
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
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
            .GetProperty("code").GetString().Should().Be("not_found");
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
        parts.GetArrayLength().Should().Be(1);
        parts[0].GetProperty("kind").GetString().Should().Be("text");
        parts[0].GetProperty("text").GetString().Should().Be(lookalike);
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
        enqueue.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await events.CollectUntilAsync(
            frame => frame.GetProperty("type").GetString() == "run.status" &&
                     frame.GetProperty("state").GetString() == "idle",
            deadline.Token);

        var transcript = await harness.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/agent/sessions/{sessionId}/transcript", deadline.Token);
        var parts = transcript.GetProperty("turns")[0].GetProperty("messages")[0].GetProperty("parts");
        parts.GetArrayLength().Should().Be(1);
        parts[0].GetProperty("kind").GetString().Should().Be("data");
        parts[0].GetProperty("value").GetProperty("sha256").GetString().Should().Be(referenced.Sha256);

        // The committed marker's blob reference is the object's live set: pruning with no
        // grace removes the unreferenced upload and keeps the referenced one.
        harness.SessionManager.PruneObjects(TimeSpan.Zero).Should().BeGreaterThanOrEqualTo(1);
        harness.ObjectStore.Exists(referenced.Sha256).Should().BeTrue();
        harness.ObjectStore.Exists(unreferenced.Sha256).Should().BeFalse();
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

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }
}
