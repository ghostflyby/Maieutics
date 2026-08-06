using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Maieutics.Providers.Anthropic;

internal sealed class AnthropicMessagesChatClient : IChatClient
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxOutputTokens = 4096;
    private readonly HttpClient httpClient;
    private readonly string model;

    internal AnthropicMessagesChatClient(
        string model,
        string apiKey,
        Uri? endpoint,
        HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        this.model = model;
        httpClient = handler is null ? new HttpClient() : new HttpClient(handler, true);
        httpClient.BaseAddress = NormalizeEndpoint(endpoint ?? new Uri("https://api.anthropic.com/"));
        httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        httpClient.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
            updates.Add(update);

        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var requestBody = CreateRequestBody(messages, options);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
        request.Content = new ByteArrayContent(requestBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, false);

        var eventData = new StringBuilder();
        var toolCalls = new Dictionary<int, StreamingToolCall>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length != 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (eventData.Length > 0) eventData.Append('\n');

                    eventData.Append(line.AsSpan("data:".Length).TrimStart());
                }

                continue;
            }

            if (eventData.Length == 0) continue;

            var update = ProcessEvent(eventData.ToString(), toolCalls);
            eventData.Clear();
            if (update is not null) yield return update;
        }

        if (eventData.Length > 0)
        {
            var update = ProcessEvent(eventData.ToString(), toolCalls);
            if (update is not null) yield return update;
        }

        if (toolCalls.Count > 0)
            throw new InvalidDataException("Anthropic ended the response before a tool call was complete.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private static ChatResponseUpdate? ProcessEvent(
        string eventData,
        IDictionary<int, StreamingToolCall> toolCalls)
    {
        using var document = JsonDocument.Parse(eventData);
        var root = document.RootElement;
        var eventType = root.GetProperty("type").GetString();
        switch (eventType)
        {
            case "error":
            {
                var error = root.GetProperty("error");
                var errorType = error.TryGetProperty("type", out var type)
                    ? type.GetString()
                    : null;
                throw new InvalidDataException(string.IsNullOrWhiteSpace(errorType)
                    ? "Anthropic reported a streaming error."
                    : $"Anthropic reported the streaming error '{errorType}'.");
            }
            case "content_block_start":
                StartContentBlock(root, toolCalls);
                return null;
            case "content_block_delta":
            {
                var delta = root.GetProperty("delta");
                switch (delta.GetProperty("type").GetString())
                {
                    case "text_delta":
                    {
                        var text = delta.GetProperty("text").GetString();
                        return string.IsNullOrEmpty(text)
                            ? null
                            : new ChatResponseUpdate(ChatRole.Assistant, text);
                    }
                    case "input_json_delta":
                    {
                        var index = root.GetProperty("index").GetInt32();
                        if (!toolCalls.TryGetValue(index, out var call))
                            throw new InvalidDataException(
                                $"Anthropic streamed tool arguments for unknown content block {index}.");

                        call.Arguments.Append(delta.GetProperty("partial_json").GetString());
                        return null;
                    }
                    default:
                        return null;
                }
            }
            case "content_block_stop":
            {
                var index = root.GetProperty("index").GetInt32();
                if (!toolCalls.Remove(index, out var call)) return null;

                return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(call.CallId, call.Name, ParseArguments(call.Arguments.ToString()))]);
            }
            default:
                return null;
        }
    }

    private byte[] CreateRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteNumber("max_tokens", options?.MaxOutputTokens ?? DefaultMaxOutputTokens);
        writer.WriteBoolean("stream", true);

        var systemText = string.Join('\n', materialized
            .Where(static message => message.Role == ChatRole.System)
            .SelectMany(static message => message.Contents.OfType<TextContent>())
            .Select(static content => content.Text));
        if (systemText.Length > 0) writer.WriteString("system", systemText);

        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        foreach (var message in materialized.Where(static message => message.Role != ChatRole.System))
            WriteMessage(writer, message);

        writer.WriteEndArray();
        WriteTools(writer, options?.Tools);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role == ChatRole.Assistant ? "assistant" : "user");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        foreach (var content in message.Contents)
            switch (content)
            {
                case TextContent text:
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    writer.WriteEndObject();
                    break;
                case FunctionCallContent call:
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", call.CallId);
                    writer.WriteString("name", call.Name);
                    writer.WritePropertyName("input");
                    WriteArguments(writer, call.Arguments);
                    writer.WriteEndObject();
                    break;
                case FunctionResultContent result:
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_result");
                    writer.WriteString("tool_use_id", result.CallId);
                    writer.WriteString("content", FormatResult(result.Result));
                    writer.WriteEndObject();
                    break;
                case UsageContent:
                    break;
                default:
                    throw new NotSupportedException(
                        $"Anthropic Messages does not support content type '{content.GetType().Name}'.");
            }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteTools(Utf8JsonWriter writer, IList<AITool>? tools)
    {
        if (tools is not { Count: > 0 }) return;

        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        foreach (var tool in tools)
        {
            if (tool is not AIFunctionDeclaration function)
                throw new NotSupportedException(
                    $"Anthropic Messages does not support tool type '{tool.GetType().Name}'.");

            writer.WriteStartObject();
            writer.WriteString("name", function.Name);
            if (!string.IsNullOrWhiteSpace(function.Description))
                writer.WriteString("description", function.Description);

            writer.WritePropertyName("input_schema");
            function.JsonSchema.WriteTo(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteArguments(Utf8JsonWriter writer, IDictionary<string, object?>? arguments)
    {
        writer.WriteStartObject();
        if (arguments is not null)
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                WriteValue(writer, value);
            }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int integer:
                writer.WriteNumberValue(integer);
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                throw new NotSupportedException(
                    $"Anthropic Messages cannot serialize argument value type '{value.GetType().Name}'.");
        }
    }

    private static string FormatResult(object? result)
    {
        return result switch
        {
            null => "null",
            string text => text,
            JsonElement element => element.GetRawText(),
            _ => result.ToString() ?? string.Empty
        };
    }

    private static void StartContentBlock(
        JsonElement root,
        IDictionary<int, StreamingToolCall> toolCalls)
    {
        var block = root.GetProperty("content_block");
        if (!string.Equals(block.GetProperty("type").GetString(), "tool_use", StringComparison.Ordinal)) return;

        var index = root.GetProperty("index").GetInt32();
        var call = new StreamingToolCall(
            block.GetProperty("id").GetString()
            ?? throw new InvalidDataException("Anthropic tool_use omitted its id."),
            block.GetProperty("name").GetString()
            ?? throw new InvalidDataException("Anthropic tool_use omitted its name."));
        if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object &&
            input.EnumerateObject().Any())
            call.Arguments.Append(input.GetRawText());

        if (!toolCalls.TryAdd(index, call))
            throw new InvalidDataException($"Anthropic repeated content block index {index}.");
    }

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Anthropic tool arguments must be a JSON object.");

        return document.RootElement.EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
    }

    private static Uri NormalizeEndpoint(Uri endpoint)
    {
        var value = endpoint.AbsoluteUri;
        return value.EndsWith('/') ? endpoint : new Uri(value + '/');
    }

    private sealed class StreamingToolCall(string callId, string name)
    {
        internal string CallId { get; } = callId;

        internal string Name { get; } = name;

        internal StringBuilder Arguments { get; } = new();
    }
}