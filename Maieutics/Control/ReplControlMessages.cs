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
[JsonSerializable(typeof(ToolProgressPayload))]
internal sealed partial class ReplControlJsonContext : JsonSerializerContext;

/// <summary>Versioned script tool invocation request carried by the control channel.</summary>
internal sealed record ToolInvokeRequest(
    int Version,
    string Tool,
    JsonElement Arguments,
    string? CorrelationId = null,
    string? SessionId = null);

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
    public const string ToolProgress = "tool.progress";
    public const string Error = "error";
}

internal sealed record BusCancelPayload(string CorrelationId);

internal sealed record BusCommPayload(
    string CommId,
    string? TargetName = null,
    JsonElement? Data = null);

internal sealed record BusErrorPayload(string Code, string Message);

internal sealed record BusAckPayload(string CommId, bool Ok, string? Error = null);

/// <summary>Tool progress pushed over the bus, keyed by the originating tool call.</summary>
internal sealed record ToolProgressPayload(
    int? Progress = null,
    int? Total = null,
    string? Stage = null,
    string? Message = null,
    string? Status = null,
    JsonElement? Data = null);

/// <summary>Progress reporter a tool can pull from its invocation context.</summary>
internal sealed class ReplToolProgress
{
    private readonly Func<ToolProgressPayload, CancellationToken, ValueTask> report;

    public ReplToolProgress(Func<ToolProgressPayload, CancellationToken, ValueTask> report)
    {
        this.report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public ValueTask ReportAsync(ToolProgressPayload progress, CancellationToken cancellationToken) =>
        report(progress, cancellationToken);
}
