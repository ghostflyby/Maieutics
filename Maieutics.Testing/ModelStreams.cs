using System.Text;
using System.Text.Json;

namespace Maieutics.Testing;

/// <summary>The SSE answer-stream builders for the fake model providers: one home for
/// every flavor and flow shape the suite serves.</summary>
public static class ModelStreams
{
    public static string CreateChatCompletionsStream(string text, string? reasoning = null)
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

    public static string CreateChatCompletionsToolStream(string toolName, string argumentsJson)
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

    public static string CreateResponsesStream(string text)
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

    /// <summary>A Responses turn where the provider ran a web search: web_search_call items,
    /// then the assistant message with a url_citation annotation. Server-executed, so no
    /// tool-result round trip follows. The stream carries <paramref name="calls"/> search
    /// items so a per-request built-in call ceiling can be exercised.</summary>
    public static string CreateResponsesWebSearchStream(int calls)
    {
        var searchItems = new List<string>();
        for (var index = 0; index < calls; index++)
        {
            searchItems.Add(
                "{\"id\":\"ws" + index + "\",\"type\":\"web_search_call\",\"status\":\"completed\"," +
                "\"action\":{\"type\":\"search\",\"query\":\"zhipu ai\"}}");
        }

        const string messageItem =
            "{\"id\":\"msg1\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\"," +
            "\"content\":[{\"type\":\"output_text\",\"text\":\"Zhipu AI is a Chinese AI company.\"," +
            "\"annotations\":[{\"type\":\"url_citation\",\"url\":\"https://example.com/zhipu\"," +
            "\"title\":\"Zhipu AI\",\"start_index\":0,\"end_index\":8}]}]}";

        const string inProgress =
            "{\"id\":\"resp-ws\",\"object\":\"response\",\"created_at\":0,\"status\":\"in_progress\"," +
            "\"error\":null,\"incomplete_details\":null,\"instructions\":null,\"max_output_tokens\":null," +
            "\"model\":\"test-model\",\"output\":[],\"parallel_tool_calls\":true,\"previous_response_id\":null," +
            "\"reasoning\":null,\"store\":false,\"temperature\":null,\"text\":{\"format\":{\"type\":\"text\"}}," +
            "\"tool_choice\":\"auto\",\"tools\":[],\"top_p\":null,\"truncation\":\"disabled\",\"usage\":null," +
            "\"metadata\":{}}";
        var completed =
            "{\"id\":\"resp-ws\",\"object\":\"response\",\"created_at\":0,\"status\":\"completed\"," +
            "\"error\":null,\"incomplete_details\":null,\"instructions\":null,\"max_output_tokens\":null," +
            "\"model\":\"test-model\",\"output\":[" + string.Join(",", searchItems) + "," + messageItem + "]," +
            "\"parallel_tool_calls\":true,\"previous_response_id\":null,\"reasoning\":null,\"store\":false," +
            "\"temperature\":null,\"text\":{\"format\":{\"type\":\"text\"}},\"tool_choice\":\"auto\"," +
            "\"tools\":[],\"top_p\":null,\"truncation\":\"disabled\",\"usage\":{\"input_tokens\":1," +
            "\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":1," +
            "\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":2},\"metadata\":{}}";

        var stream = new StringBuilder();
        var sequence = 0;
        stream.Append(
            "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":" +
            sequence + "," + "\"response\":" + inProgress + "}\n\n");
        sequence++;
        for (var index = 0; index < searchItems.Count; index++)
        {
            stream.Append(
                "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\"," +
                "\"sequence_number\":" + sequence + ",\"output_index\":" + index +
                ",\"item\":" + searchItems[index] + "}\n\n");
            sequence++;
        }

        var messageIndex = searchItems.Count;
        stream.Append(
            "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\"," +
            "\"sequence_number\":" + sequence + ",\"output_index\":" + messageIndex +
            ",\"item\":{\"id\":\"msg1\",\"type\":\"message\",\"status\":\"in_progress\"," +
            "\"role\":\"assistant\",\"content\":[]}}\n\n");
        sequence++;
        stream.Append(
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\"," +
            "\"sequence_number\":" + sequence + ",\"item_id\":\"msg1\",\"output_index\":" + messageIndex +
            ",\"content_index\":0,\"delta\":\"Zhipu AI is a Chinese AI company.\"}\n\n");
        sequence++;
        stream.Append(
            "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\"," +
            "\"sequence_number\":" + sequence + ",\"output_index\":" + messageIndex +
            ",\"item\":" + messageItem + "}\n\n");
        sequence++;
        stream.Append(
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":" +
            sequence + "," + "\"response\":" + completed + "}\n\n");

        return stream.ToString();
    }

    public static string CreateResponsesApplyPatchStream(string operationJson)
    {
        const string inProgressResponse =
            "{\"id\":\"resp-apc\",\"object\":\"response\",\"created_at\":0," +
            "\"status\":\"in_progress\",\"error\":null,\"incomplete_details\":null," +
            "\"instructions\":null,\"max_output_tokens\":null,\"model\":\"test-model\"," +
            "\"output\":[],\"parallel_tool_calls\":true,\"previous_response_id\":null," +
            "\"reasoning\":null,\"store\":false,\"temperature\":null," +
            "\"text\":{\"format\":{\"type\":\"text\"}},\"tool_choice\":\"auto\"," +
            "\"tools\":[],\"top_p\":null,\"truncation\":\"disabled\",\"usage\":null," +
            "\"metadata\":{}}";
        var completedItem =
            "{\"id\":\"apc-test\",\"type\":\"apply_patch_call\",\"status\":\"completed\"," +
            "\"call_id\":\"call-apc\",\"operation\":" + operationJson + "}";
        var completedResponse =
            "{\"id\":\"resp-apc\",\"object\":\"response\",\"created_at\":0," +
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
            "\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"apc-test\"," +
            "\"type\":\"apply_patch_call\",\"status\":\"in_progress\",\"call_id\":\"call-apc\"," +
            "\"operation\":" + operationJson + "}}\n\n" +
            "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\"," +
            "\"sequence_number\":2,\"output_index\":0,\"item\":" + completedItem + "}\n\n" +
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":3," +
            "\"response\":" + completedResponse + "}\n\n";
    }

    public static string CreateResponsesToolStream(string toolName, string argumentsJson)
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
    public static string CreateTextStream(string text)
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

    /// <summary>A server-executed web search turn: a text preamble, the server tool use block
    /// (arguments stream as input_json_delta), the complete result block, then the answer text.
    /// Mirrors Anthropic's documented ordering; the result arrives whole inside one
    /// content_block_start with its encrypted_content intact.</summary>
    public static string CreateWebSearchStream()
    {
        return """
               event: message_start
               data: {"type":"message_start","message":{"id":"msg_search","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

               event: content_block_start
               data: {"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"srvtoolu_test","name":"web_search","input":{}}}

               event: content_block_delta
               data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\":\"zhipu ai\"}"}}

               event: content_block_stop
               data: {"type":"content_block_stop","index":0}

               event: content_block_start
               data: {"type":"content_block_start","index":1,"content_block":{"type":"web_search_tool_result","tool_use_id":"srvtoolu_test","content":[{"type":"web_search_result","title":"Zhipu AI","url":"https://example.com/zhipu","encrypted_content":"ENCRYPTED_PAYLOAD","page_age":"April 30, 2025"}]}}

               event: content_block_stop
               data: {"type":"content_block_stop","index":1}

               event: content_block_start
               data: {"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}

               event: content_block_delta
               data: {"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"Zhipu AI is a Chinese AI company."}}

               event: content_block_delta
               data: {"type":"content_block_delta","index":2,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"https://example.com/zhipu","title":"Zhipu AI","encrypted_index":"ENCRYPTED_INDEX","cited_text":"Zhipu AI is a Chinese AI company."}}}

               event: content_block_stop
               data: {"type":"content_block_stop","index":2}

               event: message_delta
               data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5,"server_tool_use":{"web_search_requests":1}}}

               event: message_stop
               data: {"type":"message_stop"}

               """;
    }

    public static string CreateToolStream()
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
