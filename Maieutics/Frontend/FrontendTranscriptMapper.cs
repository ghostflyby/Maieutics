using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Frontend;

/// <summary>
///     Renders committed Agent history and tool progress into provider-neutral frontend wire
///     values. Microsoft.Extensions.AI content types are consumed here (the Agent boundary's
///     neutral currency) but never serialized as such — the wire shapes are frontend-owned.
/// </summary>
internal static class FrontendTranscriptMapper
{
    // The agent transcript's own serializer options carry a source-generated resolver, so
    // obtaining the object type info statically keeps arbitrary argument values on the
    // NativeAOT path.
    private static readonly JsonTypeInfo<object?> ObjectTypeInfo =
        (JsonTypeInfo<object?>)AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object));

    internal static FrontendMessage ToMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var parts = new List<FrontendMessagePart>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            var part = ToPart(content);
            if (part is not null) parts.Add(part);
        }

        return new FrontendMessage(ToRole(message.Role), parts);
    }

    internal static FrontendProgressContent ToProgressContent(AIContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content switch
        {
            TextContent text => new FrontendProgressContent("text", Text: text.Text),
            DataContent data => new FrontendProgressContent("json", Value: ParseJsonData(data.Data.Span)),
            _ => new FrontendProgressContent("unknown")
        };
    }

    internal static FrontendTranscript ToTranscript(AgentTranscript transcript)
    {
        var turns = new List<FrontendTranscriptTurn>(transcript.Turns.Length);
        foreach (var turn in transcript.Turns)
        {
            var messages = new List<FrontendMessage>(turn.Messages.Count);
            foreach (var message in turn.Messages) messages.Add(ToMessage(message));

            turns.Add(new FrontendTranscriptTurn(
                turn.RunId.Value.ToString("N"),
                turn.Truncated,
                turn.ModelIdentity is { } identity
                    ? new FrontendModelIdentity(identity.ProfileId.Value, identity.Provider, identity.Model)
                    : null,
                messages));
        }

        return new FrontendTranscript(
            transcript.SessionId.Value.ToString("N"),
            transcript.Version,
            turns);
    }

    private static FrontendMessagePart? ToPart(AIContent content)
    {
        return content switch
        {
            TextContent text => new FrontendMessagePart("text", Text: text.Text),
            DataContent data => new FrontendMessagePart("data", Value: ParseJsonData(data.Data.Span)),
            FunctionCallContent call => new FrontendMessagePart(
                "tool_call",
                CallId: call.CallId,
                Name: call.Name,
                Value: SerializeArguments(call.Arguments)),
            FunctionResultContent result => new FrontendMessagePart(
                "tool_result",
                CallId: result.CallId,
                Value: SerializeValue(result.Result)),
            // Provider-hosted tools are executed by the provider; the frontend shows the search
            // that ran (its queries) and the results as an ordinary tool pair so existing
            // rendering applies without a new wire kind.
            WebSearchToolCallContent search => new FrontendMessagePart(
                "tool_call",
                CallId: search.CallId,
                Name: WebSearchToolName,
                Value: SerializeValue(search.Queries)),
            WebSearchToolResultContent searchResult => new FrontendMessagePart(
                "tool_result",
                CallId: searchResult.CallId,
                Value: SerializeSearchResults(searchResult)),
            // Some providers (the OpenAI Responses bridge among them) surface web citations as a
            // stand-alone content item that only carries annotations. They belong to the answer
            // text, so project them as a text part followed by its sources.
            _ when content.Annotations is { Count: > 0 } => new FrontendMessagePart(
                "text",
                Text: string.Empty,
                Value: SerializeCitations(content.Annotations)),
            TextReasoningContent => null,
            UsageContent => null,
            _ => new FrontendMessagePart("unknown")
        };
    }

    /// <summary>Projects citation annotations into {url,title} pairs.</summary>
    private static JsonElement? SerializeCitations(IEnumerable<AIAnnotation> annotations)
    {
        var sources = new List<Dictionary<string, object?>>();
        foreach (var annotation in annotations)
        {
            if (annotation is not CitationAnnotation citation) continue;
            sources.Add(new Dictionary<string, object?>
            {
                ["url"] = citation.Url?.ToString(),
                ["title"] = citation.Title
            });
        }

        return sources.Count == 0 ? null : SerializeValue(sources);
    }

    /// <summary>The provider-neutral name hosted web search content projects onto.</summary>
    internal const string WebSearchToolName = "web_search";

    /// <summary>Projects search results as url/title pairs. The provider's opaque
    /// encrypted_content is deliberately not exposed to the frontend.</summary>
    private static JsonElement? SerializeSearchResults(WebSearchToolResultContent content)
    {
        var results = new List<Dictionary<string, object?>>();
        foreach (var output in content.Outputs ?? [])
        {
            if (output is ErrorContent error)
            {
                results.Add(new Dictionary<string, object?>
                {
                    ["error"] = error.Message
                });
                continue;
            }

            if (output.AdditionalProperties is { } properties &&
                properties.TryGetValue("anthropicRawBlock", out var raw) &&
                raw is JsonElement element)
                AddAnthropicResult(results, element);
        }

        return SerializeValue(results);
    }

    private static void AddAnthropicResult(List<Dictionary<string, object?>> results, JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("content", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in items.EnumerateArray())
        {
            results.Add(new Dictionary<string, object?>
            {
                ["url"] = item.TryGetProperty("url", out var url) ? url.GetString() : null,
                ["title"] = item.TryGetProperty("title", out var title) ? title.GetString() : null
            });
        }
    }

    private static string ToRole(ChatRole role) =>
        role == ChatRole.User ? "user"
        : role == ChatRole.Assistant ? "assistant"
        : role == ChatRole.Tool ? "tool"
        : role == ChatRole.System ? "system"
        : role.Value;

    private static JsonElement ParseJsonData(ReadOnlySpan<byte> data)
    {
        try
        {
            using var document = JsonDocument.Parse(data.ToArray());
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(
                Encoding.UTF8.GetString(data),
                FrontendJsonContext.Default.String);
        }
    }

    private static JsonElement SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>(),
                FrontendJsonContext.Default.DictionaryStringObject);

        var converted = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
            converted[name] = value is JsonElement element ? element.Clone() : value;

        return SerializeArbitrary(converted) ?? EmptyObject();
    }

    private static JsonElement? SerializeValue(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement element) return element.Clone();

        return SerializeArbitrary(value);
    }

    private static JsonElement? SerializeArbitrary(object? value)
    {
        try
        {
            return JsonSerializer.SerializeToElement(value, ObjectTypeInfo);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Unsupported argument shapes degrade to an absent value instead of failing the
            // whole transcript render.
            return null;
        }
    }

    private static JsonElement EmptyObject()
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>(),
            FrontendJsonContext.Default.DictionaryStringObject);
    }
}
