using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace Maieutics.Providers.OpenAI;

#pragma warning disable OPENAI001 // The OpenAI .NET Responses surface is currently marked experimental.

/// <summary>Adapts the OpenAI Responses client for apply_patch using the SDK's native support.
/// The SDK models the built-in tool, its call items, and their outputs; the runtime works in the
/// provider-neutral function contract. This client bridges the two without touching the wire:
/// <list type="bullet">
/// <item>the built-in tool is declared on every request while the same-named local function is
/// kept off it (declaring both would send two conflicting definitions);</item>
/// <item>an incoming apply_patch call becomes a normal <see cref="FunctionCallContent"/> whose
/// <c>patch</c> argument is the equivalent V4A patch text, so the ordinary function loop invokes
/// the runtime's apply_patch function;</item>
/// <item>the outbound call and its result carry the SDK's own items as
/// <see cref="AIContent.RawRepresentation"/>, which the Responses client serializes as
/// <c>apply_patch_call</c> and <c>apply_patch_call_output</c>.</item>
/// </list>
/// The patch semantics themselves (parsing, context matching, file safety) stay in
/// <c>WorkspaceEditFunctions</c>: the SDK carries the operation, not the patch format.</summary>
internal sealed class ResponsesApplyPatchChatClient : DelegatingChatClient
{
    /// <summary>Additional-property key holding the structured operation JSON of an apply_patch
    /// call. The operation is stored as JSON rather than as a serialized SDK model because a
    /// serialized model loses the extensible fields (a <c>create_file</c> diff among them) when it
    /// round-trips through the transcript's JSON.</summary>
    internal const string OperationProperty = "openaiApplyPatchOperation";

    private static readonly ModelReaderWriterOptions JsonFormat = new("J");

    /// <summary>The SDK's source-generated serialization context, which keeps these reads and
    /// writes on the NativeAOT path.</summary>
    internal static readonly ModelReaderWriterContext ModelContext = OpenAIContext.Default;

    private readonly string toolName;

    /// <summary>Initializes the adapter.</summary>
    /// <param name="inner">The Responses client to wrap.</param>
    /// <param name="toolName">The local apply_patch function name.</param>
    internal ResponsesApplyPatchChatClient(IChatClient inner, string toolName)
        : base(inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        this.toolName = toolName;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var response = await base.GetResponseAsync(
                ConvertOutboundMessages(messages),
                PrepareOptions(options),
                cancellationToken)
            .ConfigureAwait(false);

        var converted = ConvertInboundMessages(response.Messages);
        if (converted is null) return response;

        return new ChatResponse([.. converted])
        {
            ResponseId = response.ResponseId,
            ConversationId = response.ConversationId,
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            AdditionalProperties = response.AdditionalProperties
        };
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await foreach (var update in base
                           .GetStreamingResponseAsync(
                               ConvertOutboundMessages(messages),
                               PrepareOptions(options),
                               cancellationToken)
                           .ConfigureAwait(false))
            yield return ConvertInboundUpdate(update);
    }

    /// <summary>Declares the built-in tool alongside whatever the caller passed. The same-named
    /// local function is dropped here as well, so the request never carries two definitions for
    /// one name even when a caller forgot to hide it.
    /// <para>
    /// The built-in tool is added to the request options the outer decorators already build, by
    /// wrapping their factory: assigning a fresh factory here would be overwritten by any
    /// ConfigureOptions-style decorator sitting outside this adapter.
    /// </para></summary>
    private ChatOptions PrepareOptions(ChatOptions? options)
    {
        var prepared = options?.Clone() ?? new ChatOptions();
        var tools = new List<AITool>();
        foreach (var tool in prepared.Tools ?? [])
        {
            if (tool is AIFunction function &&
                string.Equals(function.Name, toolName, StringComparison.Ordinal))
                continue;

            tools.Add(tool);
        }

        var previousFactory = prepared.RawRepresentationFactory;
        prepared.Tools = tools;
        prepared.RawRepresentationFactory = client =>
        {
            var request = previousFactory?.Invoke(client) as CreateResponseOptions ?? CreateRequestOptions();
            request.Tools.Add(ResponseTool.CreateApplyPatchTool());
            return request;
        };
        return prepared;
    }

    private static CreateResponseOptions CreateRequestOptions()
    {
        return new CreateResponseOptions { StoredOutputEnabled = false };
    }

    /// <summary>Projects the apply_patch call items the SDK produced onto the function contract.
    /// Returns null when nothing needed converting, so the common path allocates nothing.</summary>
    private IReadOnlyList<ChatMessage>? ConvertInboundMessages(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage>? converted = null;
        var anyChanged = false;
        foreach (var message in messages)
        {
            var contents = new List<AIContent>(message.Contents.Count);
            var changed = false;
            foreach (var content in message.Contents)
            {
                if (IsApplyPatchCall(content))
                {
                    contents.Add(ConvertInboundCall(content));
                    changed = true;
                }
                else
                {
                    contents.Add(content);
                }
            }

            converted ??= [];
            anyChanged |= changed;
            converted.Add(changed
                ? new ChatMessage(message.Role, contents)
                {
                    AuthorName = message.AuthorName,
                    MessageId = message.MessageId,
                    CreatedAt = message.CreatedAt,
                    AdditionalProperties = message.AdditionalProperties
                }
                : message);
        }

        return anyChanged ? converted : null;
    }

    private ChatResponseUpdate ConvertInboundUpdate(ChatResponseUpdate update)
    {
        var converted = ConvertInboundContents(update.Contents);
        if (converted is null) return update;

        return new ChatResponseUpdate(update.Role, [.. converted])
        {
            AuthorName = update.AuthorName,
            MessageId = update.MessageId,
            ResponseId = update.ResponseId,
            ConversationId = update.ConversationId,
            CreatedAt = update.CreatedAt,
            FinishReason = update.FinishReason,
            ModelId = update.ModelId,
            ContinuationToken = update.ContinuationToken,
            RawRepresentation = update.RawRepresentation,
            AdditionalProperties = update.AdditionalProperties
        };
    }

    private IReadOnlyList<AIContent>? ConvertInboundContents(IList<AIContent> contents)
    {
        List<AIContent>? converted = null;
        for (var index = 0; index < contents.Count; index++)
        {
            if (!IsApplyPatchCall(contents[index])) continue;

            converted ??= [.. contents.Take(index)];
            converted.Add(ConvertInboundCall(contents[index]));
        }

        if (converted is null) return null;

        for (var index = contents.Count - converted.Count; index < contents.Count; index++)
            converted.Add(contents[index]);

        return converted;
    }

    private static bool IsApplyPatchCall(AIContent content)
    {
        return content.RawRepresentation is ApplyPatchCallItem { Operation: not null };
    }

    private FunctionCallContent ConvertInboundCall(AIContent content)
    {
        var item = (ApplyPatchCallItem)content.RawRepresentation!;
        var callId = item.CallId ?? item.Id ?? string.Empty;
        var call = new FunctionCallContent(
            callId,
            toolName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["patch"] = ApplyPatchText.Render(item.Operation!)
            });
        call.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [OperationProperty] = JsonDocument
                .Parse(ModelReaderWriter.Write(item.Operation!, JsonFormat, ModelContext).ToString())
                .RootElement.Clone()
        };
        return call;
    }

    /// <summary>Restores the SDK's items on the outbound call and result so the Responses client
    /// serializes the provider's native wire forms. Content instances are replaced rather than
    /// mutated: the committed transcript owns the originals.</summary>
    private IReadOnlyList<ChatMessage> ConvertOutboundMessages(IEnumerable<ChatMessage> messages)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var applyPatchCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in materialized)
            foreach (var content in message.Contents)
                if (content is FunctionCallContent call && IsApplyPatchCall(call))
                    applyPatchCalls.Add(call.CallId);

        if (applyPatchCalls.Count == 0) return materialized;

        var converted = new List<ChatMessage>(materialized.Count);
        foreach (var message in materialized)
        {
            var contents = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                contents.Add(content switch
                {
                    FunctionCallContent call when applyPatchCalls.Contains(call.CallId) => RestoreCall(call),
                    FunctionResultContent result when applyPatchCalls.Contains(result.CallId) => RestoreResult(result),
                    _ => content
                });
            }

            converted.Add(new ChatMessage(message.Role, contents)
            {
                AuthorName = message.AuthorName,
                MessageId = message.MessageId,
                CreatedAt = message.CreatedAt,
                AdditionalProperties = message.AdditionalProperties
            });
        }

        return converted;
    }

    private static bool IsApplyPatchCall(FunctionCallContent call)
    {
        return string.Equals(call.Name, "apply_patch", StringComparison.Ordinal) &&
               call.AdditionalProperties?.ContainsKey(OperationProperty) == true;
    }

    /// <summary>Rebuilds the structured call from the preserved operation JSON.</summary>
    private FunctionCallContent RestoreCall(FunctionCallContent call)
    {
        if (call.AdditionalProperties is not { } properties ||
            !properties.TryGetValue(OperationProperty, out var operation) ||
            operation is not JsonElement operationJson)
            return call;

        var restored = new FunctionCallContent(call.CallId, call.Name, call.Arguments)
        {
            AdditionalProperties = call.AdditionalProperties,
            RawRepresentation = ReadCallItem(call.CallId, operationJson)
        };
        return restored;
    }

    private static ApplyPatchCallItem? ReadCallItem(string callId, JsonElement operationJson)
    {
        var wrapper = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "apply_patch_call",
            ["call_id"] = callId,
            ["status"] = "completed",
            ["operation"] = JsonDocument.Parse(operationJson.GetRawText()).RootElement.Clone()
        };
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in wrapper)
            {
                writer.WritePropertyName(name);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return ModelReaderWriter.Read<ApplyPatchCallItem>(
            BinaryData.FromBytes(buffer.ToArray()),
            JsonFormat,
            ModelContext);
    }

    /// <summary>Rebuilds the structured output. The envelope status decides the wire status and
    /// its text becomes the output the model reads.</summary>
    private static FunctionResultContent RestoreResult(FunctionResultContent result)
    {
        var output = result.Result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement element => element.GetRawText(),
            var value => value.ToString() ?? string.Empty
        };
        var failed = !Succeeded(output);
        var wrapper = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "apply_patch_call_output",
            ["call_id"] = result.CallId,
            ["status"] = failed ? "failed" : "completed",
            ["output"] = output
        };
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in wrapper)
            {
                writer.WritePropertyName(name);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        var item = ModelReaderWriter.Read<ApplyPatchCallOutputItem>(
            BinaryData.FromBytes(buffer.ToArray()),
            JsonFormat,
            ModelContext);
        if (item is null) return result;

        var restored = new FunctionResultContent(result.CallId, result.Result)
        {
            RawRepresentation = item
        };
        if (result.Exception is not null) restored.Exception = result.Exception;
        return restored;
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
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static bool Succeeded(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return true;

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String)
                return true;

            var value = status.GetString();
            return value is null or "ok" or "cancelled";
        }
        catch (JsonException)
        {
            return true;
        }
    }
}

/// <summary>Renders an SDK apply_patch operation into the V4A patch text the runtime's apply_patch
/// function consumes, so the structured wire form and patch text reach one implementation.</summary>
internal static class ApplyPatchText
{
    /// <summary>Renders one operation as a complete V4A document.</summary>
    internal static string Render(ApplyPatchOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var document = JsonDocument.Parse(ModelReaderWriter.Write(
            operation,
            new ModelReaderWriterOptions("J"),
            ResponsesApplyPatchChatClient.ModelContext).ToString());
        var json = document.RootElement;
        var kind = json.TryGetProperty("type", out var kindElement) ? kindElement.GetString() : null;
        var filePath = json.TryGetProperty("path", out var path) ? path.GetString() : null;
        if (string.IsNullOrEmpty(filePath))
            throw new InvalidOperationException("An apply_patch operation must name a file path.");

        var diff = json.TryGetProperty("diff", out var diffElement) ? diffElement.GetString() ?? "" : "";
        var body = kind switch
        {
            "create_file" => $"*** Add File: {filePath}\n{WithTrailingNewline(diff)}",
            "update_file" => $"*** Update File: {filePath}\n"
                             + (json.TryGetProperty("move_to", out var move) &&
                                move.ValueKind == JsonValueKind.String
                                 ? $"*** Move to: {move.GetString()}\n"
                                 : "")
                             + WithTrailingNewline(diff),
            "delete_file" => $"*** Delete File: {filePath}\n",
            _ => throw new InvalidOperationException($"Unsupported apply_patch operation '{kind}'.")
        };

        return $"*** Begin Patch\n{body}*** End Patch\n";
    }

    private static string WithTrailingNewline(string text)
    {
        return text.Length > 0 && text.EndsWith('\n') ? text : text + "\n";
    }
}
#pragma warning restore OPENAI001
