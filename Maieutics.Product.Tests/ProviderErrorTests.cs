using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Frontend;
using Maieutics.Providers;
using Maieutics.Providers.Anthropic;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class ProviderErrorTests
{
    private const string SensitiveText = "sk-test-secret <script>request content must stay private</script>";

    public static TheoryData<ModelApiFlavor, int, string, AgentProviderFailureKind> HttpFailures
    {
        get
        {
            var data = new TheoryData<ModelApiFlavor, int, string, AgentProviderFailureKind>();
            (int Status, string Code, AgentProviderFailureKind Kind)[] cases =
            [
                (401, "invalid_api_key", AgentProviderFailureKind.Authentication),
                (403, "permission_error", AgentProviderFailureKind.PermissionDenied),
                (404, "model_not_found", AgentProviderFailureKind.NotFound),
                (400, "context_length_exceeded", AgentProviderFailureKind.ContextLimit),
                (429, "rate_limit_error", AgentProviderFailureKind.RateLimited),
                (429, "insufficient_quota", AgentProviderFailureKind.QuotaExceeded),
                (408, "", AgentProviderFailureKind.Timeout),
                (503, "", AgentProviderFailureKind.Unavailable),
                (529, "overloaded_error", AgentProviderFailureKind.Unavailable),
                (400, SensitiveText, AgentProviderFailureKind.InvalidRequest),
            ];
            foreach (var flavor in Enum.GetValues<ModelApiFlavor>())
                foreach (var (status, code, kind) in cases)
                    data.Add(flavor, status, code, kind);
            return data;
        }
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(HttpFailures))]
    public async Task RealAdaptersPreserveSafeHttpFailureDetails(
        ModelApiFlavor flavor, int status, string code, AgentProviderFailureKind kind)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        var body = JsonSerializer.Serialize(new { error = new { code, type = code, message = SensitiveText } });
        await using var server = new ErrorServer(status, body);
        using var client = CreateClient(flavor, server.Endpoint);
        var failure = await Assert.ThrowsAsync<AgentProviderException>(() => ConsumeAsync(client, budget.Token));
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(status, failure.StatusCode);
        Assert.Contains($"HTTP {status}", failure.Message);
        Assert.DoesNotContain("sk-test-secret", failure.Message);
        Assert.DoesNotContain("<script>", failure.Message);
        Assert.Equal(string.IsNullOrEmpty(code) || code == SensitiveText ? null : code, failure.ProviderCode);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(ModelApiFlavor.ChatCompletions, false)]
    [InlineData(ModelApiFlavor.Responses, false)]
    [InlineData(ModelApiFlavor.Anthropic, false)]
    [InlineData(ModelApiFlavor.ChatCompletions, true)]
    [InlineData(ModelApiFlavor.Responses, true)]
    [InlineData(ModelApiFlavor.Anthropic, true)]
    public async Task FailureClassificationSurvivesAgentAndFrontend(ModelApiFlavor flavor, bool streaming)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        await using var server = streaming
            ? new ErrorServer(200, PartialStream(flavor) + ErrorEvent(flavor), streaming: true)
            : new ErrorServer(401, "<html>" + SensitiveText + "</html>");
        using var client = CreateClient(flavor, server.Endpoint);
        var session = new AgentSession(client);
        await using var run = await session.StartTurnAsync(AgentTurn.FromText("hello"), budget.Token);
        await using var stream = FrontendRunStream.Create(session.Id, run, null, NullLogger<FrontendRunStream>.Instance);
        stream.Start(null);
        var (replay, channel) = stream.Subscribe(0);
        var frames = replay.ToList();
        await foreach (var frame in channel.Reader.ReadAllAsync(budget.Token)) frames.Add(frame);
        stream.Unsubscribe(channel);
        var failed = Assert.Single(frames, frame => frame.Type == "run.failed");
        Assert.Equal(streaming ? "agent_provider_rate_limited" : "agent_provider_authentication", failed.Code);
        Assert.Contains(streaming ? "rate limit" : "API key", failed.Message ?? "");
        if (streaming)
            Assert.Contains(frames, frame => frame.Type == "text.delta" && frame.Text == "partial");
        else
            Assert.Contains("HTTP 401", failed.Message ?? "");
        Assert.DoesNotContain("sk-test-secret", failed.Message ?? "");
        Assert.Empty(session.GetTranscriptSnapshot().Turns);
        await Assert.ThrowsAsync<AgentProviderException>(() => run.Completion);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(ModelApiFlavor.ChatCompletions)]
    [InlineData(ModelApiFlavor.Responses)]
    [InlineData(ModelApiFlavor.Anthropic)]
    public async Task NonStreamingRequestsUseTheSameClassification(ModelApiFlavor flavor)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        await using var server = new ErrorServer(403, "{}");
        using var client = CreateClient(flavor, server.Endpoint);
        var failure = await Assert.ThrowsAsync<AgentProviderException>(() =>
            client.GetResponseAsync("hello", cancellationToken: budget.Token));
        Assert.Equal(AgentProviderFailureKind.PermissionDenied, failure.Kind);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(ModelApiFlavor.ChatCompletions)]
    [InlineData(ModelApiFlavor.Responses)]
    [InlineData(ModelApiFlavor.Anthropic)]
    public async Task StreamingErrorEventsAreClassifiedEvenAfterHttpSuccess(ModelApiFlavor flavor)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        await using var server = new ErrorServer(200, ErrorEvent(flavor), streaming: true);
        using var client = CreateClient(flavor, server.Endpoint);
        var failure = await Assert.ThrowsAsync<AgentProviderException>(() => ConsumeAsync(client, budget.Token));
        Assert.Equal(AgentProviderFailureKind.RateLimited, failure.Kind);
        Assert.Null(failure.StatusCode);
        Assert.DoesNotContain("sk-test-secret", failure.Message);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponsesFailedResultsAreNotTreatedAsSuccessfulEmptyAnswers(bool streaming)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        var response = JsonSerializer.Serialize(new { id = "resp-failed", @object = "response", created_at = 0,
            status = "failed", model = "test-model", output = Array.Empty<object>(),
            error = new { code = "context_length_exceeded", message = SensitiveText } });
        var body = streaming ? $"event: response.failed\ndata: {{\"type\":\"response.failed\",\"sequence_number\":1,\"response\":{response}}}\n\n" : response;
        await using var server = new ErrorServer(200, body, streaming);
        using var client = CreateClient(ModelApiFlavor.Responses, server.Endpoint);
        var failure = await Assert.ThrowsAsync<AgentProviderException>(async () =>
        {
            if (streaming) await ConsumeAsync(client, budget.Token);
            else await client.GetResponseAsync("hello", cancellationToken: budget.Token);
        });
        Assert.Equal(AgentProviderFailureKind.ContextLimit, failure.Kind);
        Assert.DoesNotContain("sk-test-secret", failure.Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task TimeoutConnectionAndMalformedResponseRemainDistinct()
    {
        Exception[] exceptions = [new OperationCanceledException(SensitiveText),
            new HttpRequestException(SensitiveText), new InvalidDataException(SensitiveText),
            new JsonException(SensitiveText), new InvalidOperationException(SensitiveText)];
        AgentProviderFailureKind[] kinds = [AgentProviderFailureKind.Timeout, AgentProviderFailureKind.Connection,
            AgentProviderFailureKind.InvalidResponse, AgentProviderFailureKind.InvalidResponse,
            AgentProviderFailureKind.Unknown];
        for (var i = 0; i < exceptions.Length; i++)
        {
            var exception = exceptions[i];
            using var client = new ProviderErrorChatClient(new ScriptedChatClient((_, _) => FailAsync(exception)));
            var failure = await Assert.ThrowsAsync<AgentProviderException>(() => ConsumeAsync(client, TestContext.Current.CancellationToken));
            Assert.Equal(kinds[i], failure.Kind);
            Assert.Same(exception, failure.InnerException);
            Assert.DoesNotContain("sk-test-secret", failure.Message);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task CallerCancellationAndTypedAgentFailuresPassThrough()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
        var cancellation = new OperationCanceledException(cancelled.Token);
        var typed = new AgentResponseLimitExceededException(10);
        foreach (var exception in new Exception[] { cancellation, typed })
        {
            using var client = new ProviderErrorChatClient(new ScriptedChatClient((_, _) => FailAsync(exception)));
            var actual = await Record.ExceptionAsync(() => ConsumeAsync(client, cancelled.Token));
            Assert.Same(exception, actual);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FailAsync(Exception exception)
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
        throw exception;
    }

    private static async Task ConsumeAsync(IChatClient client, CancellationToken token)
    {
        await foreach (var unused in client.GetStreamingResponseAsync("hello", cancellationToken: token)) { }
    }

    private static string ErrorEvent(ModelApiFlavor flavor)
    {
        var data = JsonSerializer.Serialize(new { type = "error", code = "rate_limit_exceeded", message = SensitiveText,
            error = new { type = "rate_limit_error", code = "rate_limit_exceeded", message = SensitiveText } });
        return (flavor == ModelApiFlavor.ChatCompletions ? "" : "event: error\n") + $"data: {data}\n\n";
    }

    private static string PartialStream(ModelApiFlavor flavor)
    {
        if (flavor == ModelApiFlavor.Responses)
        {
            var stream = ModelStreams.CreateResponsesStream("partial");
            return stream[..stream.IndexOf("event: response.output_text.done", StringComparison.Ordinal)];
        }
        return flavor == ModelApiFlavor.Anthropic
            ? "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n"
            : "data: {\"id\":\"chat-test\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"partial\"},\"finish_reason\":null}]}\n\n";
    }

    private static IChatClient CreateClient(ModelApiFlavor flavor, Uri endpoint)
    {
        if (flavor == ModelApiFlavor.Anthropic)
            return AnthropicChatClientFactory.Create("test-model", new AnthropicSourceOptions("sk-test-secret", endpoint));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Source:ApiKey"] = "sk-test-secret", ["Source:Endpoint"] = new Uri(endpoint, "v1/").AbsoluteUri,
            ["Source:ApiFlavor"] = flavor.ToString(),
        }).Build();
        return new OpenAiChatClientFactory().BindSource("test", config.GetSection("Source")).Create("test-model");
    }

    internal sealed class ErrorServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _completion;

        internal ErrorServer(int status, string body, bool streaming = false)
        {
            _listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _completion = ServeAsync(status, Encoding.UTF8.GetBytes(body), streaming, _stop.Token);
        }

        internal Uri Endpoint { get; }

        private async Task ServeAsync(int status, byte[] body, bool streaming, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(token);
                    await using var stream = client.GetStream();
                    // Read exactly the headers, leaving request-body bytes for the explicit drain.
                    var headers = new StringBuilder();
                    var next = new byte[1];
                    while (headers.Length < 64 * 1024)
                    {
                        await stream.ReadExactlyAsync(next, token);
                        headers.Append((char)next[0]);
                        if (headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                    var lengthHeader = headers.ToString().Split("\r\n")
                        .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var length = int.Parse(lengthHeader.AsSpan("Content-Length:".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    await stream.ReadExactlyAsync(new byte[length], token);
                    var responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: {(streaming ? "text/event-stream" : "application/json")}\r\n" +
                        $"Content-Length: {body.Length}\r\nx-should-retry: false\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(responseHeaders, token);
                    await stream.WriteAsync(body, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (SocketException) when (token.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _completion; }
            finally { _stop.Dispose(); }
        }
    }
}
