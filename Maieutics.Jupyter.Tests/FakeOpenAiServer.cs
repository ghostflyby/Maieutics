using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;

namespace Maieutics.Jupyter.Tests;

internal sealed class FakeOpenAiServer : IAsyncDisposable
{
    private readonly string answer;
    private readonly OpenAiApiFlavor apiFlavor;
    private readonly CancellationTokenSource cancellation = new();
    private readonly string? expectedToolResultText;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly string model;
    private readonly string? reasoning;
    private readonly int requestCount;
    private readonly string toolArgumentsJson;

    private readonly bool toolFlow;
    private readonly string toolName;

    public FakeOpenAiServer(
        OpenAiApiFlavor apiFlavor,
        bool toolFlow = false,
        string model = "test-model",
        string answer = "native answer",
        string? reasoning = null,
        int? requestCount = null,
        string toolName = "echo",
        string toolArgumentsJson = "{\"text\":\"hello\"}",
        string? expectedToolResultText = null)
    {
        this.apiFlavor = apiFlavor;
        this.toolFlow = toolFlow;
        this.model = model;
        this.answer = answer;
        this.reasoning = reasoning;
        this.toolName = toolName;
        this.toolArgumentsJson = toolArgumentsJson;
        this.expectedToolResultText = expectedToolResultText;
        this.requestCount = requestCount ?? (toolFlow ? 2 : 1);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
        Completion = ServeAsync(cancellation.Token);
    }

    public Uri Endpoint { get; }

    public Task Completion { get; }

    public ConcurrentQueue<JsonElement> RequestBodies { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        listener.Stop();
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellation.IsCancellationRequested)
        {
        }

        cancellation.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        // Handle the expected requests across connections. The OpenAI SDK's
        // HttpClient pools connections (keep-alive by default), so a client
        // may reuse the same TCP connection for consecutive requests or open
        // a fresh one. Loop on a per-connection basis: read one request,
        // answer it, and keep the connection open for the next request. A
        // "Connection: close" response per request would race the client's
        // connection reuse (the pooled connection may already be closed when
        // the next request arrives), surfacing as an EndOfStreamException on
        // slow CI.
        var served = 0;
        while (served < requestCount)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var stream = client.GetStream();
            while (served < requestCount)
            {
                HttpRequest request;
                try
                {
                    request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    // The client closed this connection (dispose or a fresh
                    // connection for the next request); accept the next one.
                    break;
                }
                RequestBodies.Enqueue(request.Body.Clone());
                AssertRequest(request, served);

                var data = (apiFlavor, toolFlow, served) switch
                {
                    (OpenAiApiFlavor.Responses, true, 0) => CreateResponsesToolStream(
                        toolName,
                        toolArgumentsJson),
                    (OpenAiApiFlavor.ChatCompletions, true, 0) => CreateChatCompletionsToolStream(
                        toolName,
                        toolArgumentsJson),
                    (OpenAiApiFlavor.Responses, _, _) => CreateResponsesStream(
                        toolFlow ? "tool-backed answer" : answer),
                    (OpenAiApiFlavor.ChatCompletions, _, _) => CreateChatCompletionsStream(
                        toolFlow ? "tool-backed answer" : answer,
                        reasoning),
                    _ => throw new InvalidOperationException(
                        $"Unsupported test API flavor '{apiFlavor}'.")
                };
                var body = Encoding.UTF8.GetBytes(data);
                // Keep the connection open (no "Connection: close") so the
                // client's pooled connection can carry the next request; the
                // outer loop accepts a fresh connection when this one ends.
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
                    $"Content-Length: {body.Length}\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                served++;
            }
        }
    }

    private void AssertRequest(HttpRequest request, int requestIndex)
    {
        request.Method.Should().Be("POST");
        request.Path.Should().Be(apiFlavor switch
        {
            OpenAiApiFlavor.Responses => "/v1/responses",
            OpenAiApiFlavor.ChatCompletions => "/v1/chat/completions",
            _ => throw new InvalidOperationException($"Unsupported test API flavor '{apiFlavor}'.")
        });
        request.Body.GetProperty("model").GetString().Should().Be(model);
        request.Body.GetProperty("stream").GetBoolean().Should().BeTrue();
        request.Body.GetProperty("store").GetBoolean().Should().BeFalse();
        if (!toolFlow) return;

        request.Body.GetProperty("tools").GetArrayLength().Should().BeGreaterThan(0);
        request.Body.GetRawText().Should().Contain(toolName);
        if (requestIndex > 0)
        {
            request.Body.GetRawText().Should().Contain("status").And.Contain("ok");
            if (expectedToolResultText is not null)
                request.Body.GetRawText().Should().Contain(expectedToolResultText);
        }
    }

    private static string CreateChatCompletionsStream(string text, string? reasoning = null)
    {
        return (reasoning is null
                   ? string.Empty
                   : "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":0," +
                     "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{" +
                     "\"role\":\"assistant\",\"reasoning_content\":" + JsonSerializer.Serialize(reasoning) +
                     "},\"finish_reason\":null}]}\n\n") +
               "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":0," +
               "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{" +
               "\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) +
               "},\"finish_reason\":null}]}\n\n" +
               "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":0," +
               "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
               "data: [DONE]\n\n";
    }

    private static string CreateChatCompletionsToolStream(string toolName, string argumentsJson)
    {
        return "data: {\"id\":\"chatcmpl-tool\",\"object\":\"chat.completion.chunk\",\"created\":0," +
               "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{" +
               "\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call-test\"," +
               "\"type\":\"function\",\"function\":{\"name\":" + JsonSerializer.Serialize(toolName) + "," +
               "\"arguments\":" + JsonSerializer.Serialize(argumentsJson) + "}}]},\"finish_reason\":null}]}\n\n" +
               "data: {\"id\":\"chatcmpl-tool\",\"object\":\"chat.completion.chunk\",\"created\":0," +
               "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{}," +
               "\"finish_reason\":\"tool_calls\"}]}\n\n" +
               "data: [DONE]\n\n";
    }

    private static string CreateResponsesStream(string text)
    {
        const string inProgressResponse =
            "{\"id\":\"resp-test\",\"object\":\"response\",\"created_at\":0," +
            "\"status\":\"in_progress\",\"error\":null,\"incomplete_details\":null," +
            "\"instructions\":null,\"max_output_tokens\":null,\"model\":\"test-model\"," +
            "\"output\":[],\"parallel_tool_calls\":true,\"previous_response_id\":null," +
            "\"reasoning\":null,\"store\":false,\"temperature\":null," +
            "\"text\":{\"format\":{\"type\":\"text\"}},\"tool_choice\":\"auto\"," +
            "\"tools\":[],\"top_p\":null,\"truncation\":\"disabled\",\"usage\":null," +
            "\"metadata\":{}}";
        var completedItem =
            "{\"id\":\"msg-test\",\"type\":\"message\",\"status\":\"completed\"," +
            "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\"," +
            "\"text\":" + JsonSerializer.Serialize(text) +
            ",\"annotations\":[],\"logprobs\":[]}]}";
        var completedResponse =
            "{\"id\":\"resp-test\",\"object\":\"response\",\"created_at\":0," +
            "\"status\":\"completed\",\"error\":null,\"incomplete_details\":null," +
            "\"instructions\":null,\"max_output_tokens\":null,\"model\":\"test-model\"," +
            "\"output\":[" + completedItem + "],\"parallel_tool_calls\":true," +
            "\"previous_response_id\":null,\"reasoning\":null,\"store\":false," +
            "\"temperature\":null,\"text\":{\"format\":{\"type\":\"text\"}}," +
            "\"tool_choice\":\"auto\",\"tools\":[],\"top_p\":null," +
            "\"truncation\":\"disabled\",\"usage\":{\"input_tokens\":1," +
            "\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":1," +
            "\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":2}," +
            "\"metadata\":{}}";

        return
            "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0," +
            "\"response\":" + inProgressResponse + "}\n\n" +
            "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\"," +
            "\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"msg-test\"," +
            "\"type\":\"message\",\"status\":\"in_progress\",\"role\":\"assistant\"," +
            "\"content\":[]}}\n\n" +
            "event: response.content_part.added\ndata: {\"type\":\"response.content_part.added\"," +
            "\"sequence_number\":2,\"item_id\":\"msg-test\",\"output_index\":0," +
            "\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\"," +
            "\"annotations\":[],\"logprobs\":[]}}\n\n" +
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\"," +
            "\"sequence_number\":3,\"item_id\":\"msg-test\",\"output_index\":0," +
            "\"content_index\":0,\"delta\":" + JsonSerializer.Serialize(text) +
            ",\"logprobs\":[]}\n\n" +
            "event: response.output_text.done\ndata: {\"type\":\"response.output_text.done\"," +
            "\"sequence_number\":4,\"item_id\":\"msg-test\",\"output_index\":0," +
            "\"content_index\":0,\"text\":" + JsonSerializer.Serialize(text) +
            ",\"logprobs\":[]}\n\n" +
            "event: response.content_part.done\ndata: {\"type\":\"response.content_part.done\"," +
            "\"sequence_number\":5,\"item_id\":\"msg-test\",\"output_index\":0," +
            "\"content_index\":0,\"part\":{\"type\":\"output_text\"," +
            "\"text\":" + JsonSerializer.Serialize(text) +
            ",\"annotations\":[],\"logprobs\":[]}}\n\n" +
            "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\"," +
            "\"sequence_number\":6,\"output_index\":0,\"item\":" + completedItem + "}\n\n" +
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":7," +
            "\"response\":" + completedResponse + "}\n\n";
    }

    private static string CreateResponsesToolStream(string toolName, string argumentsJson)
    {
        const string inProgressResponse =
            "{\"id\":\"resp-tool\",\"object\":\"response\",\"created_at\":0," +
            "\"status\":\"in_progress\",\"error\":null,\"incomplete_details\":null," +
            "\"instructions\":null,\"max_output_tokens\":null,\"model\":\"test-model\"," +
            "\"output\":[],\"parallel_tool_calls\":true,\"previous_response_id\":null," +
            "\"reasoning\":null,\"store\":false,\"temperature\":null," +
            "\"text\":{\"format\":{\"type\":\"text\"}},\"tool_choice\":\"auto\"," +
            "\"tools\":[],\"top_p\":null,\"truncation\":\"disabled\",\"usage\":null," +
            "\"metadata\":{}}";
        var completedItem =
            "{\"id\":\"fc-test\",\"type\":\"function_call\",\"status\":\"completed\"," +
            "\"arguments\":" + JsonSerializer.Serialize(argumentsJson) + ",\"call_id\":\"call-test\"," +
            "\"name\":" + JsonSerializer.Serialize(toolName) + "}";
        var completedResponse =
            "{\"id\":\"resp-tool\",\"object\":\"response\",\"created_at\":0," +
            "\"status\":\"completed\",\"error\":null,\"incomplete_details\":null," +
            "\"instructions\":null,\"max_output_tokens\":null,\"model\":\"test-model\"," +
            "\"output\":[" + completedItem + "],\"parallel_tool_calls\":true," +
            "\"previous_response_id\":null,\"reasoning\":null,\"store\":false," +
            "\"temperature\":null,\"text\":{\"format\":{\"type\":\"text\"}}," +
            "\"tool_choice\":\"auto\",\"tools\":[],\"top_p\":null," +
            "\"truncation\":\"disabled\",\"usage\":{\"input_tokens\":1," +
            "\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":1," +
            "\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":2}," +
            "\"metadata\":{}}";

        return
            "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0," +
            "\"response\":" + inProgressResponse + "}\n\n" +
            "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\"," +
            "\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"fc-test\"," +
            "\"type\":\"function_call\",\"status\":\"in_progress\",\"arguments\":\"\"," +
            "\"call_id\":\"call-test\",\"name\":" + JsonSerializer.Serialize(toolName) + "}}\n\n" +
            "event: response.function_call_arguments.delta\ndata: {" +
            "\"type\":\"response.function_call_arguments.delta\",\"sequence_number\":2," +
            "\"item_id\":\"fc-test\",\"output_index\":0," +
            "\"delta\":" + JsonSerializer.Serialize(argumentsJson) + "}\n\n" +
            "event: response.function_call_arguments.done\ndata: {" +
            "\"type\":\"response.function_call_arguments.done\",\"sequence_number\":3," +
            "\"item_id\":\"fc-test\",\"output_index\":0," +
            "\"arguments\":" + JsonSerializer.Serialize(argumentsJson) + "}\n\n" +
            "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\"," +
            "\"sequence_number\":4,\"output_index\":0,\"item\":" + completedItem + "}\n\n" +
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":5," +
            "\"response\":" + completedResponse + "}\n\n";
    }

    internal static async Task<HttpRequest> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var request = new MemoryStream();
        var headerLength = -1;
        var contentLength = 0;

        while (headerLength < 0 || request.Length < headerLength + contentLength)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException("The OpenAI-compatible request ended before its body was complete.");

            request.Write(buffer, 0, count);
            if (headerLength >= 0) continue;

            var bytes = request.GetBuffer().AsSpan(0, checked((int)request.Length));
            var delimiter = "\r\n\r\n"u8;
            var delimiterIndex = bytes.IndexOf(delimiter);
            if (delimiterIndex < 0) continue;

            headerLength = delimiterIndex + delimiter.Length;
            var headers = Encoding.ASCII.GetString(bytes[..delimiterIndex]);
            foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                    break;
                }
        }

        var requestBytes = request.GetBuffer().AsSpan(0, checked((int)request.Length));
        var requestLineEnd = requestBytes.IndexOf("\r\n"u8);
        if (requestLineEnd < 0)
            throw new InvalidDataException("The OpenAI-compatible request did not contain a request line.");

        var requestLine = Encoding.ASCII.GetString(requestBytes[..requestLineEnd]);
        var requestLineParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLineParts.Length != 3)
            throw new InvalidDataException($"Invalid OpenAI-compatible request line: '{requestLine}'.");

        var bodyBytes = request.GetBuffer().AsMemory(headerLength, contentLength);
        var body = JsonDocument.Parse(bodyBytes).RootElement.Clone();
        return new HttpRequest(requestLineParts[0], requestLineParts[1], body);
    }

    internal sealed record HttpRequest(string Method, string Path, JsonElement Body);
}

internal static class FrontendTestFunctions
{
internal static AIFunction CreateEchoFunction()
{
    return AIFunctionFactory.Create(
        (string text) => JsonSerializer.SerializeToElement(
            text,
            HostIntegrationJsonContext.Default.String),
        new AIFunctionFactoryOptions
        {
            Name = "echo",
            Description = "Returns the supplied text.",
            SerializerOptions = HostIntegrationJsonContext.Default.Options
        });
}

}



[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class HostIntegrationJsonContext : JsonSerializerContext;
