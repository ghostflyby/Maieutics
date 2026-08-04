using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Control;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ToolInvokeRequest))]
[JsonSerializable(typeof(ReplEnvelope))]
[JsonSerializable(typeof(BusCancelPayload))]
[JsonSerializable(typeof(BusCommPayload))]
[JsonSerializable(typeof(BusErrorPayload))]
[JsonSerializable(typeof(BusAckPayload))]
internal sealed partial class ReplControlJsonContext : JsonSerializerContext;

/// <summary>Versioned script tool invocation request carried by the control channel.</summary>
internal sealed record ToolInvokeRequest(
    int Version,
    string Tool,
    JsonElement Arguments,
    string? CorrelationId = null);

/// <summary>
/// Versioned message envelope shared by every control channel bus message. Payloads are
/// domain-shaped JSON; binary data rides the <see cref="Buffers"/> list as base64 for now.
/// </summary>
internal sealed record ReplEnvelope(
    int Version,
    string Type,
    string? CorrelationId = null,
    JsonElement? Payload = null,
    IReadOnlyList<string>? Buffers = null);

internal static class ReplMessageType
{
    public const string ControlHello = "control.hello";
    public const string ControlReady = "control.ready";
    public const string ControlPing = "control.ping";
    public const string ControlPong = "control.pong";
    public const string ControlCancel = "control.cancel";
    public const string ControlCancelled = "control.cancelled";
    public const string CommOpen = "comm.open";
    public const string CommMsg = "comm.msg";
    public const string CommClose = "comm.close";
    public const string CommAck = "comm.ack";
    public const string Error = "error";
}

internal sealed record BusCancelPayload(string CorrelationId);

internal sealed record BusCommPayload(
    string CommId,
    string? TargetName = null,
    JsonElement? Data = null);

internal sealed record BusErrorPayload(string Code, string Message);

internal sealed record BusAckPayload(string CommId, bool Ok, string? Error = null);
