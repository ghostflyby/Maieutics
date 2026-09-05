using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using System.Text.Json;

namespace Maieutics.Jupyter.Tests;

internal sealed class FakeAnthropicServer : IAsyncDisposable
{
    private readonly string answer;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly string model;
    private readonly bool toolFlow;

    public FakeAnthropicServer(string model, string answer, bool toolFlow = false)
    {
        this.model = model;
        this.answer = answer;
        this.toolFlow = toolFlow;
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}");
        Completion = ServeAsync(cancellation.Token);
    }

    public Uri Endpoint { get; }

    public Task Completion { get; }

    public ConcurrentQueue<JsonElement> RequestBodies { get; } = new();

    public JsonElement RequestBody => RequestBodies.Single();

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
        // Same keep-alive handling as FakeOpenAiServer: the Anthropic SDK's
        // HttpClient pools connections, so keep each connection open for the
        // next request instead of racing the client's connection reuse with a
        // per-request "Connection: close".
        var requestCount = toolFlow ? 2 : 1;
        var served = 0;
        while (served < requestCount)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            while (served < requestCount)
            {
                FakeOpenAiServer.HttpRequest request;
                try
                {
                    request = await FakeOpenAiServer.ReadRequestAsync(stream, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    break; // client closed this connection; accept the next one
                }
                request.Method.Should().Be("POST");
                request.Path.Should().Be("/v1/messages");
                request.Body.GetProperty("model").GetString().Should().Be(model);
                request.Body.GetProperty("stream").GetBoolean().Should().BeTrue();
                RequestBodies.Enqueue(request.Body);
                if (toolFlow) request.Body.GetRawText().Should().Contain("echo");

                var data = toolFlow && served == 0 ? CreateToolStream() : CreateTextStream(answer);
                var body = Encoding.UTF8.GetBytes(data);
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                served++;
            }
        }
    }

    private static string CreateTextStream(string text)
    {
        return """
               event: message_start
               data: {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

               event: content_block_start
               data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

               event: content_block_delta
               data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":TEXT_PLACEHOLDER}}

               event: content_block_stop
               data: {"type":"content_block_stop","index":0}

               event: message_delta
               data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

               event: message_stop
               data: {"type":"message_stop"}

               """.Replace("TEXT_PLACEHOLDER",
            JsonSerializer.Serialize(text), StringComparison.Ordinal);
    }

    private static string CreateToolStream()
    {
        return """
               event: message_start
               data: {"type":"message_start","message":{"id":"msg_tool","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

               event: content_block_start
               data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_test","name":"echo","input":{}}}

               event: content_block_delta
               data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"text\":"}}

               event: content_block_delta
               data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"hello\"}"}}

               event: content_block_stop
               data: {"type":"content_block_stop","index":0}

               event: message_delta
               data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":1}}

               event: message_stop
               data: {"type":"message_stop"}

               """;
    }
}
