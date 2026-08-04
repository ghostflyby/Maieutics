using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Control;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ToolInvokeRequest))]
internal sealed partial class ReplControlJsonContext : JsonSerializerContext;

/// <summary>Versioned script tool invocation request carried by the control channel.</summary>
internal sealed record ToolInvokeRequest(
    int Version,
    string Tool,
    JsonElement Arguments);
