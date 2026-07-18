using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Agent;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ToolResultEnvelope))]
internal sealed partial class AgentToolJsonSerializerContext : JsonSerializerContext;

internal sealed record ToolResultEnvelope(
    string Status,
    ImmutableArray<ToolResultContentEnvelope>? Content = null,
    string? Code = null,
    string? Message = null);

internal sealed record ToolResultContentEnvelope(
    string Type,
    string? Text = null,
    JsonElement? Value = null);