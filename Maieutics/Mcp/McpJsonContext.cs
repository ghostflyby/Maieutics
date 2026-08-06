using System.Text.Json.Serialization;

namespace Maieutics.Mcp;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(McpTransportDefinition))]
internal sealed partial class McpJsonContext : JsonSerializerContext;