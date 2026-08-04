using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Providers.Anthropic;
using Microsoft.Extensions.AI;

namespace Maieutics.Jupyter.Tests;

public sealed class AnthropicProviderTests
{
    [Fact]
    public async Task StreamsTextUsingConfiguredEndpointApiKeyAndModel()
    {
        await using var server = new FakeAnthropicServer(CreateTextStream("Hello from Claude"));
        using var client = new AnthropicChatClientFactory().Create(
            "claude-test",
            new AnthropicSourceOptions("configured-key", server.Endpoint));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           "Say hello",
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var request = await server.Request.WaitAsync(TestContext.Current.CancellationToken);
        request.Method.Should().Be("POST");
        request.Path.Should().Be("/v1/messages");
        request.Headers["x-api-key"].Should().Be("configured-key");
        request.Body.GetProperty("model").GetString().Should().Be("claude-test");
        request.Body.GetProperty("stream").GetBoolean().Should().BeTrue();
        string.Concat(updates.SelectMany(static update => update.Contents)
                .OfType<TextContent>()
                .Select(static content => content.Text))
            .Should().Be("Hello from Claude");
    }

    [Fact]
    public async Task MapsStreamingToolUseToFunctionCallContent()
    {
        await using var server = new FakeAnthropicServer(CreateToolStream());
        using var client = new AnthropicChatClientFactory().Create(
            "claude-test",
            new AnthropicSourceOptions("configured-key", server.Endpoint));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           "Call the echo tool",
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        await server.Request.WaitAsync(TestContext.Current.CancellationToken);
        var call = updates.SelectMany(static update => update.Contents)
            .OfType<FunctionCallContent>()
            .Should().ContainSingle().Subject;
        call.CallId.Should().Be("toolu_test");
        call.Name.Should().Be("echo");
        JsonSerializer.Serialize(call.Arguments).Should().Contain("hello");
    }

    [Fact]
    public async Task StreamingErrorEventFailsTheProviderStream()
    {
        await using var server = new FakeAnthropicServer("""
                                                         event: error
                                                         data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}
                                                         """);
        using var client = new AnthropicChatClientFactory().Create(
            "claude-test",
            new AnthropicSourceOptions("configured-key", server.Endpoint));

        await client.Awaiting(state => ConsumeAsync(state, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*overloaded_error*");
        await server.Request.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 10_000)]
    public async Task CancellationInterruptsAnActiveProviderStream()
    {
        using var client = new AnthropicMessagesChatClient(
            "claude-test",
            "configured-key",
            endpoint: null,
            handler: new StreamingResponseHandler(
                "event: content_block_delta\n" +
                "data: {\"type\":\"content_block_delta\",\"index\":0," +
                "\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var updates = client.GetStreamingResponseAsync(
                "Say hello",
                cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        (await updates.MoveNextAsync()).Should().BeTrue();
        updates.Current.Text.Should().Be("partial");
        await cancellation.CancelAsync();

        await updates.Awaiting(static state => state.MoveNextAsync())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task ConsumeAsync(IChatClient client, CancellationToken cancellationToken)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(
                           "Say hello",
                           cancellationToken: cancellationToken))
        {
        }
    }

    private static string CreateTextStream(string text) => """
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

    private static string CreateToolStream() => """
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

    private sealed class FakeAnthropicServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();

        public FakeAnthropicServer(string response)
        {
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}");
            Request = ServeAsync(response, cancellation.Token);
        }

        public Uri Endpoint { get; }

        public Task<ObservedRequest> Request { get; }

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync();
            listener.Stop();
            try
            {
                await Request.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (SocketException) when (cancellation.IsCancellationRequested)
            {
            }

            cancellation.Dispose();
        }

        private async Task<ObservedRequest> ServeAsync(string response, CancellationToken cancellationToken)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);

            var body = Encoding.UTF8.GetBytes(response);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            return request;
        }

        private static async Task<ObservedRequest> ReadRequestAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            var headerBytes = new List<byte>();
            var terminator = "\r\n\r\n"u8.ToArray();
            while (!headerBytes.TakeLast(terminator.Length).SequenceEqual(terminator))
            {
                var buffer = new byte[1];
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("HTTP request ended before its headers completed.");
                }

                headerBytes.Add(buffer[0]);
            }

            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1)
                .Select(static line => line.Split(':', 2, StringSplitOptions.TrimEntries))
                .ToDictionary(static parts => parts[0], static parts => parts[1],
                    StringComparer.OrdinalIgnoreCase);
            var contentLength = int.Parse(headers["Content-Length"]);
            var bodyBytes = new byte[contentLength];
            await stream.ReadExactlyAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bodyBytes);
            return new ObservedRequest(
                requestLine[0],
                requestLine[1],
                headers,
                document.RootElement.Clone());
        }
    }

    private sealed record ObservedRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        JsonElement Body);

    private sealed class StreamingResponseHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingSseStream(Encoding.UTF8.GetBytes(response)))
            };
            responseMessage.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(responseMessage);
        }
    }

    private sealed class BlockingSseStream(byte[] contents) : Stream
    {
        private int offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int _, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (offset < contents.Length)
            {
                var count = Math.Min(buffer.Length, contents.Length - offset);
                contents.AsMemory(offset, count).CopyTo(buffer);
                offset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long _, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int _, int count) => throw new NotSupportedException();
    }
}