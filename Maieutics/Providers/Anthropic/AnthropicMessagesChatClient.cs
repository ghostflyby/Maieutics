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

    /// <summary>Anthropic's server-executed web search tool. The version selects behavior; no
    /// beta header is involved.</summary>
    private const string WebSearchToolType = "web_search_20250305";
    private const string WebSearchToolName = "web_search";

    /// <summary>Additional-property key carrying a server tool block exactly as Anthropic sent
    /// it. It is replayed verbatim, which is what keeps <c>encrypted_content</c> and citation
    /// indices valid across turns (an altered or dropped value is a 400).</summary>
    internal const string RawBlockProperty = "anthropicRawBlock";
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
        var blocks = new Dictionary<int, StreamingBlock>();
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

            foreach (var update in ProcessEvent(eventData.ToString(), blocks))
                yield return update;

            eventData.Clear();
        }

        if (eventData.Length > 0)
        {
            foreach (var update in ProcessEvent(eventData.ToString(), blocks))
                yield return update;
        }

        if (blocks.Values.Any(static block => block.Kind == StreamingBlockKind.ToolCall))
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

    private static IReadOnlyList<ChatResponseUpdate> ProcessEvent(
        string eventData,
        IDictionary<int, StreamingBlock> blocks)
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
                return StartContentBlock(root, blocks);
            case "content_block_delta":
                return ApplyContentBlockDelta(root, blocks);
            case "content_block_stop":
                return FinishContentBlock(root, blocks);
            default:
                return [];
        }
    }

    /// <summary>Applies one content-block delta. Text and server-tool argument deltas accumulate
    /// into their block; a citation delta attaches a citation to the text block it belongs to.
    /// Nothing is emitted until the block stops, because the accumulated block is what must be
    /// preserved and replayed verbatim.</summary>
    private static IReadOnlyList<ChatResponseUpdate> ApplyContentBlockDelta(
        JsonElement root,
        IDictionary<int, StreamingBlock> blocks)
    {
        var delta = root.GetProperty("delta");
        var index = root.GetProperty("index").GetInt32();
        switch (delta.GetProperty("type").GetString())
        {
            case "text_delta":
                {
                    var text = delta.GetProperty("text").GetString();
                    if (string.IsNullOrEmpty(text)) return [];
                    // Streamed through immediately so partial output survives a cancellation or a
                    // truncated stream. A delta for a block that never announced itself still
                    // opens one rather than failing the whole stream.
                    if (!blocks.TryGetValue(index, out var textBlock))
                    {
                        textBlock = StreamingBlock.Text();
                        blocks[index] = textBlock;
                    }

                    if (textBlock.Kind != StreamingBlockKind.Text)
                        throw new InvalidDataException(
                            $"Anthropic streamed text for non-text content block {index}.");

                    textBlock.TextBuilder.Append(text);
                    return [new ChatResponseUpdate(ChatRole.Assistant, text)];
                }
            case "input_json_delta":
                {
                    // Both a client tool_use block and a server_tool_use block stream their
                    // arguments as input_json_delta.
                    if (!blocks.TryGetValue(index, out var call) ||
                        call.Kind is not (StreamingBlockKind.ToolCall or StreamingBlockKind.ServerToolUse))
                        throw new InvalidDataException(
                            $"Anthropic streamed tool arguments for unknown content block {index}.");

                    call.Arguments.Append(delta.GetProperty("partial_json").GetString());
                    return [];
                }
            case "citations_delta":
                {
                    if (blocks.TryGetValue(index, out var cited) &&
                        cited.Kind == StreamingBlockKind.Text &&
                        delta.TryGetProperty("citation", out var citation))
                        cited.Citations.Add(citation.Clone());

                    return [];
                }
            default:
                return [];
        }
    }

    private static IReadOnlyList<ChatResponseUpdate> FinishContentBlock(
        JsonElement root,
        IDictionary<int, StreamingBlock> blocks)
    {
        var index = root.GetProperty("index").GetInt32();
        if (!blocks.Remove(index, out var block)) return [];

        switch (block.Kind)
        {
            case StreamingBlockKind.ToolCall:
                return
                [
                    new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                            block.CallId,
                            block.Name,
                            ParseArguments(block.Arguments.ToString()))])
                ];
            case StreamingBlockKind.ServerToolUse:
                return
                [
                    new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [CreateServerToolUseContent(block)])
                ];
            case StreamingBlockKind.Text:
                {
                    // The text and its citations both arrive as deltas, so the complete block can
                    // only be built here. It is reconstructed rather than replayed from the start
                    // event (which is an empty stub) so citations keep their encrypted_index.
                    // The text itself already streamed with its deltas. When the block carried
                    // citations, emit them as a trailing empty-text item: Microsoft.Extensions.AI
                    // aggregates its annotations without repeating any text, and the preserved
                    // wire block is what the next turn replays. The write path merges the run of
                    // text items back into the single block Anthropic expects.
                    if (block.Citations.Count == 0) return [];

                    var citations = new TextContent(string.Empty);
                    var annotations = new List<AIAnnotation>(block.Citations.Count);
                    foreach (var citation in block.Citations)
                        annotations.Add(CreateCitationAnnotation(citation));

                    citations.Annotations = annotations;
                    ShadeWithRawBlock(citations, BuildTextBlock(block));
                    return [new ChatResponseUpdate(ChatRole.Assistant, [citations])];
                }
            default:
                return [];
        }
    }

    /// <summary>Rebuilds a complete text block, citations included. The streaming start event
    /// carries an empty stub, so replay needs the assembled form; the citation objects are
    /// emitted exactly as received, which preserves their opaque <c>encrypted_index</c>.</summary>
    private static JsonElement BuildTextBlock(StreamingBlock block)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", block.TextBuilder.ToString());
        writer.WritePropertyName("citations");
        writer.WriteStartArray();
        foreach (var citation in block.Citations) citation.WriteTo(writer);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Builds the provider-neutral server tool call content and attaches the exact wire
    /// block, which is what a later turn replays.</summary>
    private static WebSearchToolCallContent CreateServerToolUseContent(StreamingBlock block)
    {
        var content = new WebSearchToolCallContent(block.CallId)
        {
            Queries = new List<string>()
        };
        var arguments = ParseArguments(block.Arguments.ToString());
        // ParseArguments preserves values as JsonElement, so read the text through the element.
        if (arguments.TryGetValue("query", out var query))
        {
            var text = query switch
            {
                string direct => direct,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            };
            if (!string.IsNullOrEmpty(text)) content.Queries.Add(text);
        }

        ShadeWithRawBlock(content, block.RawBlock ?? BuildServerToolUseBlock(block, arguments));
        return content;
    }

    private static JsonElement BuildServerToolUseBlock(
        StreamingBlock block,
        Dictionary<string, object?> arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("type", "server_tool_use");
        writer.WriteString("id", block.CallId);
        writer.WriteString("name", block.Name);
        writer.WritePropertyName("input");
        WriteArguments(writer, arguments);
        writer.WriteEndObject();
        writer.Flush();
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static CitationAnnotation CreateCitationAnnotation(JsonElement citation)
    {
        var annotation = new CitationAnnotation();
        if (citation.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            annotation.Title = title.GetString();

        if (citation.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
            annotation.Url = uri;

        if (citation.TryGetProperty("cited_text", out var citedText) &&
            citedText.ValueKind == JsonValueKind.String)
            annotation.Snippet = citedText.GetString();

        annotation.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [RawBlockProperty] = citation
        };
        return annotation;
    }

    private static void ShadeWithRawBlock(AIContent content, JsonElement? rawBlock)
    {
        if (rawBlock is not { } block) return;

        content.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [RawBlockProperty] = block
        };
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
        foreach (var content in MergeAssistantText(message))
            switch (content)
            {
                case TextContent text:
                    // The merged block carries a preserved wire form when the provider's text
                    // block had citations, and is rebuilt from the neutral fields otherwise.
                    if (TryWriteRawBlock(writer, text)) break;

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
                case WebSearchToolCallContent search:
                    // Replay the exact server_tool_use block Anthropic sent. Its input and id
                    // are what the following result block is bound to.
                    if (TryWriteRawBlock(writer, search)) break;

                    throw new NotSupportedException(
                        "An Anthropic web search call cannot be replayed without its original wire block.");
                case WebSearchToolResultContent searchResult:
                    // Replay verbatim so encrypted_content survives; an altered or dropped
                    // value makes Anthropic reject the follow-up request.
                    if (TryWriteRawBlock(writer, searchResult)) break;

                    throw new NotSupportedException(
                        "An Anthropic web search result cannot be replayed without its original wire block.");
                case UsageContent:
                    break;
                default:
                    throw new NotSupportedException(
                        $"Anthropic Messages does not support content type '{content.GetType().Name}'.");
            }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Collapses a run of adjacent text items back into the single block Anthropic
    /// renders. Streaming emits the text as deltas and any citations as a trailing empty-text
    /// item, so replay has to rejoin them; the merged item takes the preserved wire block when
    /// one of the run carried it.</summary>
    private static IReadOnlyList<AIContent> MergeAssistantText(ChatMessage message)
    {
        if (!message.Contents.Any(static content => content is TextContent)) return [.. message.Contents];

        var merged = new List<AIContent>(message.Contents.Count);
        TextContent? pending = null;
        foreach (var content in message.Contents)
        {
            if (content is not TextContent text)
            {
                Flush(merged, ref pending);
                merged.Add(content);
                continue;
            }

            if (pending is null)
            {
                pending = text;
                continue;
            }

            pending = new TextContent(pending.Text + text.Text)
            {
                Annotations = [.. pending.Annotations ?? [], .. text.Annotations ?? []],
                AdditionalProperties = pending.AdditionalProperties ?? text.AdditionalProperties
            };
        }

        Flush(merged, ref pending);
        return merged;

        static void Flush(List<AIContent> target, ref TextContent? pending)
        {
            if (pending is null) return;

            // The wire block belongs on the merged item, whichever part of the run carried it.
            target.Add(pending);
            pending = null;
        }
    }

    /// <summary>Writes the content's preserved wire block, when it has one. Returns false when
    /// there is nothing to replay and the caller should use its neutral projection.</summary>
    private static bool TryWriteRawBlock(Utf8JsonWriter writer, AIContent content)
    {
        if (content.AdditionalProperties is not { } properties ||
            !properties.TryGetValue(RawBlockProperty, out var value) ||
            value is not JsonElement block)
            return false;

        block.WriteTo(writer);
        return true;
    }

    private static void WriteTools(Utf8JsonWriter writer, IList<AITool>? tools)
    {
        if (tools is not { Count: > 0 }) return;

        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        foreach (var tool in tools)
        {
            switch (tool)
            {
                case AIFunctionDeclaration function:
                    writer.WriteStartObject();
                    writer.WriteString("name", function.Name);
                    if (!string.IsNullOrWhiteSpace(function.Description))
                        writer.WriteString("description", function.Description);

                    writer.WritePropertyName("input_schema");
                    function.JsonSchema.WriteTo(writer);
                    writer.WriteEndObject();
                    break;
                case HostedWebSearchTool:
                    // Anthropic's server tool: declared by type and name, with no input schema.
                    // The provider runs the search and returns the results in the response.
                    writer.WriteStartObject();
                    writer.WriteString("type", WebSearchToolType);
                    writer.WriteString("name", WebSearchToolName);
                    writer.WriteEndObject();
                    break;
                default:
                    throw new NotSupportedException(
                        $"Anthropic Messages does not support tool type '{tool.GetType().Name}'.");
            }
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

    /// <summary>Registers a starting content block. A web search result block arrives complete, so
    /// it is emitted immediately; text and server tool use blocks accumulate deltas and are
    /// emitted when they stop.</summary>
    private static IReadOnlyList<ChatResponseUpdate> StartContentBlock(
        JsonElement root,
        IDictionary<int, StreamingBlock> blocks)
    {
        var block = root.GetProperty("content_block");
        var index = root.GetProperty("index").GetInt32();
        switch (block.GetProperty("type").GetString())
        {
            case "text":
                if (!blocks.TryAdd(index, StreamingBlock.Text(block)))
                    throw new InvalidDataException($"Anthropic repeated content block index {index}.");

                return [];
            case "tool_use":
                {
                    var call = StreamingBlock.ToolCall(
                        block.GetProperty("id").GetString()
                        ?? throw new InvalidDataException("Anthropic tool_use omitted its id."),
                        block.GetProperty("name").GetString()
                        ?? throw new InvalidDataException("Anthropic tool_use omitted its name."));
                    if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object &&
                        input.EnumerateObject().Any())
                        call.Arguments.Append(input.GetRawText());

                    if (!blocks.TryAdd(index, call))
                        throw new InvalidDataException($"Anthropic repeated content block index {index}.");

                    return [];
                }
            case "server_tool_use":
                {
                    var call = StreamingBlock.ServerToolUse(
                        block.GetProperty("id").GetString()
                        ?? throw new InvalidDataException("Anthropic server_tool_use omitted its id."),
                        block.GetProperty("name").GetString()
                        ?? throw new InvalidDataException("Anthropic server_tool_use omitted its name."));
                    if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object &&
                        input.EnumerateObject().Any())
                        call.Arguments.Append(input.GetRawText());

                    if (!blocks.TryAdd(index, call))
                        throw new InvalidDataException($"Anthropic repeated content block index {index}.");

                    return [];
                }
            case "web_search_tool_result":
                {
                    var toolUseId = block.GetProperty("tool_use_id").GetString()
                        ?? throw new InvalidDataException(
                            "Anthropic web_search_tool_result omitted its tool_use_id.");
                    var content = new WebSearchToolResultContent(toolUseId)
                    {
                        Outputs = new List<AIContent>()
                    };
                    var raw = block.Clone();
                    content.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [RawBlockProperty] = raw
                    };
                    // A failed search carries an error object instead of a result list; the
                    // provider-neutral error content keeps that visible to the transcript.
                    if (raw.TryGetProperty("content", out var results) &&
                        results.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var result in results.EnumerateArray())
                        {
                            if (result.TryGetProperty("type", out var resultType) &&
                                resultType.GetString() == "web_search_tool_result_error")
                                content.Outputs.Add(new ErrorContent(
                                    result.TryGetProperty("error_code", out var code)
                                        ? code.GetString()
                                        : "web_search_tool_result_error"));
                        }
                    }

                    blocks.Remove(index);
                    return [new ChatResponseUpdate(ChatRole.Assistant, [content])];
                }
            default:
                return [];
        }
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

    private sealed class StreamingBlock(StreamingBlockKind kind, string callId = "", string name = "")
    {
        internal StreamingBlockKind Kind { get; } = kind;

        internal string CallId { get; } = callId;

        internal string Name { get; } = name;

        /// <summary>Accumulated text of a text block.</summary>
        internal StringBuilder TextBuilder { get; } = new();

        /// <summary>Accumulated argument JSON for tool call and server tool use blocks.</summary>
        internal StringBuilder Arguments { get; } = new();

        /// <summary>Citations attached to a text block, in arrival order.</summary>
        internal List<JsonElement> Citations { get; } = [];

        /// <summary>The exact wire block, when Anthropic supplied one whole.</summary>
        internal JsonElement? RawBlock { get; set; }

        internal static StreamingBlock Text(JsonElement? block = null)
        {
            return new StreamingBlock(StreamingBlockKind.Text)
            {
                RawBlock = block?.Clone()
            };
        }

        internal static StreamingBlock ToolCall(string callId, string name)
        {
            return new StreamingBlock(StreamingBlockKind.ToolCall, callId, name);
        }

        internal static StreamingBlock ServerToolUse(string callId, string name)
        {
            return new StreamingBlock(StreamingBlockKind.ServerToolUse, callId, name);
        }
    }

    private enum StreamingBlockKind
    {
        Text,
        ToolCall,
        ServerToolUse
    }
}