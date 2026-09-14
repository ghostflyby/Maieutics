using System.Text.Json;
using System.Text.Json.Nodes;
using Maieutics.Execution;

namespace Maieutics.Providers.OpenAI;

/// <summary>Remembers how each apply_patch call reached the model, so its tool
/// output and later history replay use the same wire shape. Bounded: the
/// oldest entries are evicted once the cap is exceeded.</summary>
internal sealed class ApplyPatchCallRegistry
{
    private const int Capacity = 512;
    private readonly object gate = new();
    private readonly Dictionary<string, ApplyPatchWireShape> shapes = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    public void Record(string callId, ApplyPatchWireShape shape)
    {
        if (string.IsNullOrEmpty(callId)) return;

        lock (gate)
        {
            if (shapes.TryAdd(callId, shape))
            {
                order.Enqueue(callId);
                while (order.Count > Capacity)
                {
                    var evicted = order.Dequeue();
                    shapes.Remove(evicted);
                }
            }
        }
    }

    public ApplyPatchWireShape? Resolve(string? callId)
    {
        if (string.IsNullOrEmpty(callId)) return null;

        lock (gate)
        {
            return shapes.TryGetValue(callId, out var shape) ? shape : null;
        }
    }
}

/// <summary>How an apply_patch tool call appears on the Responses wire: the
/// built-in structured tool (`apply_patch_call`) or the freeform custom tool
/// (`custom_tool_call`).</summary>
internal enum ApplyPatchWireShape
{
    Builtin,
    Custom
}

/// <summary>Translates between Maieutics' provider-neutral apply_patch function
/// call and the two Responses wire shapes for OpenAI's apply_patch tool. The
/// request rewrite swaps the function-tool definition for the built-in tool
/// entry and restores recorded wire shapes for replayed history; the response
/// and stream rewrites fold `apply_patch_call` and `custom_tool_call` items
/// back into plain `function_call` items the SDK and the function orchestrator
/// understand. Every rewrite is shape-matched, so retries are idempotent.</summary>
internal static class ApplyPatchWire
{
    public const string ToolName = "apply_patch";

    /// <summary>The general edit functions the Responses wire replaces with
    /// the built-in apply_patch tool.</summary>
    public static readonly IReadOnlySet<string> ResponsesHiddenToolNames =
        new HashSet<string>(["write_text", "edit_text"], StringComparer.Ordinal);

    /// <summary>The apply_patch function, which only has meaning on the
    /// Responses wire where the transport translates it.</summary>
    public static readonly IReadOnlySet<string> NonResponsesHiddenToolNames =
        new HashSet<string>([ToolName], StringComparer.Ordinal);

    private const string BuiltinCallType = "apply_patch_call";
    private const string CustomCallType = "custom_tool_call";
    private const string BuiltinOutputType = "apply_patch_call_output";
    private const string CustomOutputType = "custom_tool_call_output";

    /// <summary>Rewrites one outgoing /responses request body in place.</summary>
    public static void RewriteRequest(JsonObject body, ApplyPatchCallRegistry registry)
    {
        if (body["tools"] is JsonArray tools)
        {
            for (var index = 0; index < tools.Count; index++)
            {
                if (tools[index] is not JsonObject tool ||
                    tool["type"]?.GetValue<string>() != "function" ||
                    tool["name"]?.GetValue<string>() != ToolName)
                    continue;

                tools[index] = new JsonObject { ["type"] = ToolName };
            }
        }

        if (body["input"] is JsonArray input)
        {
            for (var index = 0; index < input.Count; index++)
            {
                if (input[index] is not JsonObject item) continue;

                var callId = item["call_id"]?.GetValue<string>();
                if (item["type"]?.GetValue<string>() == "function_call" &&
                    item["name"]?.GetValue<string>() == ToolName)
                {
                    var patch = ReadPatchArgument(item["arguments"]);
                    if (patch is null) continue;

                    var shape = registry.Resolve(callId) ?? ApplyPatchWireShape.Custom;
                    input[index] = shape == ApplyPatchWireShape.Builtin
                        ? BuildBuiltinCallItem(callId, patch, item["id"]?.GetValue<string>())
                        : BuildCustomCallItem(callId, patch, item["id"]?.GetValue<string>());
                }
                else if (item["type"]?.GetValue<string>() == "function_call_output" &&
                         callId is not null &&
                         registry.Resolve(callId) is { } outputShape)
                {
                    var output = item["output"]?.GetValue<string>();
                    input[index] = outputShape == ApplyPatchWireShape.Builtin
                        ? new JsonObject
                        {
                            ["type"] = BuiltinOutputType,
                            ["call_id"] = callId,
                            ["status"] = Failed(output) ? "failed" : "completed",
                            ["output"] = output
                        }
                        : new JsonObject
                        {
                            ["type"] = CustomOutputType,
                            ["call_id"] = callId,
                            ["output"] = output
                        };
                }
            }
        }
    }

    /// <summary>Folds apply_patch call items anywhere in a buffered response
    /// body back into function_call items.</summary>
    public static void RewriteResponse(JsonNode node, ApplyPatchCallRegistry registry)
    {
        RewriteNode(node, registry);
    }

    /// <summary>Rewrites one streaming event payload into the zero or more
    /// events the SDK should see. Suppressed: custom tool input deltas (the
    /// converted item carries the full input). Synthesized: function-call
    /// argument events after a converted item, because the SDK materializes
    /// function calls from argument events and the built-in apply_patch item
    /// has none.</summary>
    public static IReadOnlyList<JsonNode> RewriteStreamEvent(JsonObject payload, ApplyPatchCallRegistry registry)
    {
        var type = payload["type"]?.GetValue<string>();
        if (type is not null && type.StartsWith("response.custom_tool_call_input", StringComparison.Ordinal))
            return [];

        if (type == "response.output_item.added" &&
            payload["item"] is JsonObject item &&
            IsApplyPatchCallItem(item))
        {
            var callId = item["call_id"]?.GetValue<string>();
            ConvertCallItem(item, registry);
            var arguments = item["arguments"]?.GetValue<string>() ?? "{}";
            var itemId = item["id"]?.GetValue<string>() ?? callId ?? string.Empty;
            var outputIndex = payload["output_index"]?.DeepClone() ?? JsonValue.Create(0);
            var delta = new JsonObject
            {
                ["type"] = "response.function_call_arguments.delta",
                ["sequence_number"] = payload["sequence_number"]?.DeepClone() ?? JsonValue.Create(0),
                ["item_id"] = itemId,
                ["output_index"] = outputIndex.DeepClone(),
                ["delta"] = arguments
            };
            var done = new JsonObject
            {
                ["type"] = "response.function_call_arguments.done",
                ["sequence_number"] = payload["sequence_number"]?.DeepClone() ?? JsonValue.Create(0),
                ["item_id"] = itemId,
                ["output_index"] = outputIndex.DeepClone(),
                ["arguments"] = arguments
            };
            return [payload, delta, done];
        }

        RewriteNode(payload, registry);
        return [payload];
    }

    private static bool IsApplyPatchCallItem(JsonObject item)
    {
        var type = item["type"]?.GetValue<string>();
        if (type != CustomCallType && type != BuiltinCallType) return false;

        return type == BuiltinCallType || item["name"]?.GetValue<string>() == ToolName;
    }

    private static void RewriteNode(JsonNode node, ApplyPatchCallRegistry registry)
    {
        if (node is JsonObject obj)
        {
            var type = obj["type"]?.GetValue<string>();
            if (type is CustomCallType or BuiltinCallType)
            {
                var name = obj["name"]?.GetValue<string>();
                if (name == ToolName || type == BuiltinCallType)
                {
                    // The converted node has no nested apply_patch items, so
                    // the walk stops here.
                    ConvertCallItem(obj, registry);
                    return;
                }
            }

            foreach (var property in obj.ToArray())
                if (property.Value is { } value)
                    RewriteNode(value, registry);
        }
        else if (node is JsonArray array)
        {
            // ReplaceWith swaps array elements, so iterate a snapshot.
            foreach (var element in array.ToArray())
                if (element is { } elementNode)
                    RewriteNode(elementNode, registry);
        }
    }

    private static void ConvertCallItem(JsonObject item, ApplyPatchCallRegistry registry)
    {
        var callId = item["call_id"]?.GetValue<string>();
        var isBuiltin = item["type"]?.GetValue<string>() == BuiltinCallType;
        if (callId is not null)
            registry.Record(callId, isBuiltin ? ApplyPatchWireShape.Builtin : ApplyPatchWireShape.Custom);

        var patch = isBuiltin
            ? RenderOperationPatch(item["operation"] as JsonObject)
            : item["input"]?.GetValue<string>();
        var id = item["id"]?.DeepClone() ?? JsonValue.Create($"fc_{callId}");
        var status = item["status"]?.DeepClone() ?? JsonValue.Create("completed");

        // Mutate in place: JsonNode.ReplaceWith<T> is flagged for NativeAOT, and
        // the function-call shape shares no members with either source item.
        // An unrenderable operation yields empty arguments; the function
        // orchestrator then fails the call as a typed argument error the model
        // can recover from.
        var arguments = new JsonObject { ["patch"] = patch ?? "" };
        item.Clear();
        item["type"] = "function_call";
        item["id"] = id;
        item["call_id"] = callId;
        item["name"] = ToolName;
        item["arguments"] = arguments.ToJsonString();
        item["status"] = status;
    }

    /// <summary>Rebuilds a complete V4A patch document from one structured
    /// apply_patch_call operation, so the runtime's patch-text tool contract
    /// handles both wire shapes.</summary>
    private static string? RenderOperationPatch(JsonObject? operation)
    {
        if (operation is null) return null;

        var kind = operation["type"]?.GetValue<string>();
        var path = operation["path"]?.GetValue<string>();
        if (kind is null || path is null) return null;

        var patch = "*** Begin Patch\n";
        patch += kind switch
        {
            "create_file" => $"*** Add File: {path}\n{RequireTrailingNewline(operation["diff"]?.GetValue<string>() ?? "")}",
            "update_file" => $"*** Update File: {path}\n"
                + (operation["move_to"] is { } moveNode ? $"*** Move to: {moveNode.GetValue<string>()}\n" : "")
                + RequireTrailingNewline(operation["diff"]?.GetValue<string>() ?? ""),
            "delete_file" => $"*** Delete File: {path}\n",
            _ => throw new InvalidOperationException($"Unsupported apply_patch operation '{kind}'.")
        };
        return patch + "*** End Patch\n";
    }

    private static JsonObject BuildBuiltinCallItem(string? callId, string patch, string? itemId)
    {
        // A recorded builtin replay whose text no longer parses (or never was
        // a full document) falls back to the freeform shape the API accepts.
        JsonObject operation;
        try
        {
            operation = DescribeSingleOperation(patch);
        }
        catch (Exception exception) when (exception is WorkspaceException or InvalidOperationException)
        {
            return BuildCustomCallItem(callId, patch, itemId);
        }

        return new JsonObject
        {
            ["type"] = BuiltinCallType,
            ["id"] = itemId ?? $"apc_{callId}",
            ["call_id"] = callId,
            ["status"] = "completed",
            ["operation"] = operation
        };
    }

    private static JsonObject BuildCustomCallItem(string? callId, string patch, string? itemId)
    {
        return new JsonObject
        {
            ["type"] = CustomCallType,
            ["id"] = itemId ?? $"ctc_{callId}",
            ["call_id"] = callId,
            ["name"] = ToolName,
            ["input"] = patch,
            ["status"] = "completed"
        };
    }

    /// <summary>Inverts the patch renderer for single-operation patches so a
    /// replayed apply_patch call keeps its structured shape when the runtime
    /// has no record of the original wire form.</summary>
    private static JsonObject DescribeSingleOperation(string patch)
    {
        var document = Execution.ApplyPatchParser.Parse(patch);
        if (document.Files.Length != 1)
            throw new InvalidOperationException(
                "A structured apply_patch replay must describe exactly one file operation.");

        var change = document.Files[0];
        var operation = new JsonObject
        {
            ["type"] = change.Kind,
            ["path"] = change.Path
        };
        if (change.MoveToPath is { } moveTo) operation["move_to"] = moveTo;
        if (change.Diff is { } diff) operation["diff"] = diff;
        return operation;
    }

    private static string? ReadPatchArgument(JsonNode? arguments)
    {
        if (arguments is null) return null;

        try
        {
            using var document = JsonDocument.Parse(arguments.GetValue<string>());
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("patch", out var patch) &&
                patch.ValueKind == JsonValueKind.String)
                return patch.GetString();

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Failed(string? output)
    {
        if (output is null) return false;

        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("status", out var status) &&
                   status.ValueKind == JsonValueKind.String &&
                   status.GetString() != "ok" &&
                   status.GetString() != "cancelled";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RequireTrailingNewline(string text)
    {
        return text.Length > 0 && text.EndsWith('\n') ? text : text + "\n";
    }
}
