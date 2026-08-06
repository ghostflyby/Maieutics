using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Agent;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ToolSuccessEnvelope))]
[JsonSerializable(typeof(ToolFailureEnvelope))]
[JsonSerializable(typeof(ToolCancelledEnvelope))]
internal sealed partial class AgentToolJsonSerializerContext : JsonSerializerContext;

internal sealed record ToolSuccessEnvelope(
    string Status,
    JsonElement? Value);

internal sealed record ToolFailureEnvelope(
    string Status,
    string Code,
    string Message);

internal sealed record ToolCancelledEnvelope(
    string Status);