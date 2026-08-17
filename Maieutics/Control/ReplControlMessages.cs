using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Control;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    MaxDepth = ReplControlLimits.MaximumJsonDepth)]
[JsonSerializable(typeof(ToolInvokeRequest))]
[JsonSerializable(typeof(ToolInvokePayload))]
[JsonSerializable(typeof(ReplEnvelope))]
[JsonSerializable(typeof(BusCancelPayload))]
[JsonSerializable(typeof(BusCommPayload))]
[JsonSerializable(typeof(BusErrorPayload))]
[JsonSerializable(typeof(BusAckPayload))]
[JsonSerializable(typeof(ToolProgressPayload))]
[JsonSerializable(typeof(PluginHelloPayload))]
[JsonSerializable(typeof(ExtensionInvokePayload))]
[JsonSerializable(typeof(ExtensionResultPayload))]
[JsonSerializable(typeof(ExtensionErrorPayload))]
[JsonSerializable(typeof(ExtensionRegistryPayload))]
[JsonSerializable(typeof(ToolHookContextPayload))]
[JsonSerializable(typeof(ToolPostHookContextPayload))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(DiscoverContextPayload))]
internal sealed partial class ReplControlJsonContext : JsonSerializerContext;

internal static class ReplControlJson
{
    internal static byte[] Serialize(ReplEnvelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ReplControlJsonContext.Default.ReplEnvelope);
        if (bytes.Length > ReplControlLimits.MaximumInboundMessageBytes)
            throw new InvalidOperationException("The control message exceeds the maximum message size.");
        return bytes;
    }
}

/// <summary>Versioned script tool invocation request carried by the control channel.</summary>
internal sealed record ToolInvokeRequest(
    int Version,
    string Tool,
    JsonElement Arguments,
    string? CorrelationId = null,
    string? SessionId = null);

/// <summary>Script tool invocation carried inside a control WebSocket envelope.</summary>
internal sealed record ToolInvokePayload(string Tool, JsonElement Arguments);

/// <summary>
///     Versioned message envelope shared by every control channel bus message. Payloads are
///     domain-shaped JSON; binary data rides the <see cref="Buffers" /> list as base64 for now.
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
    public const string ToolInvoke = "tool.invoke";
    public const string ToolProgress = "tool.progress";
    public const string ToolResult = "tool.result";
    public const string ExtensionInvoke = "extension.invoke";
    public const string ExtensionResult = "extension.result";
    public const string ExtensionError = "extension.error";
    public const string ExtensionRegistry = "extension.registry";
    public const string Error = "error";
}

internal static class ReplExtensionPointName
{
    public const string McpDiscover = "McpDiscover";
    public const string ToolPreInvoke = "ToolPreInvoke";
    public const string ToolPostInvoke = "ToolPostInvoke";
}

internal sealed record BusCancelPayload(string CorrelationId);

/// <summary>Plugin host hello handshake payload, used in place of a session id.</summary>
internal sealed record PluginHelloPayload(string HostId);

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
internal sealed class ReplToolProgress(Func<ToolProgressPayload, CancellationToken, ValueTask> report)
{
    private readonly Func<ToolProgressPayload, CancellationToken, ValueTask> report =
        report ?? throw new ArgumentNullException(nameof(report));

    public ValueTask ReportAsync(ToolProgressPayload progress, CancellationToken cancellationToken)
    {
        return report(progress, cancellationToken);
    }
}

/// <summary>Kernel-to-host request to invoke one extension point on one plugin worker.</summary>
internal sealed record ExtensionInvokePayload(
    string PluginId,
    string ExportName,
    string ExtensionPoint,
    JsonElement? Request = null);

/// <summary>Host-to-kernel response carrying the extension point result.</summary>
internal sealed record ExtensionResultPayload(JsonElement? Value = null);

/// <summary>Host-to-kernel typed failure for an extension point call.</summary>
internal sealed record ExtensionErrorPayload(string Code, string Message);

/// <summary>Host-to-kernel registry snapshot of scanned extension points per worker.</summary>
internal sealed record ExtensionRegistryPayload(IReadOnlyList<ExtensionRegistryPlugin> Plugins);

internal sealed record ExtensionRegistryPlugin(
    string PluginId,
    string ExportName,
    IReadOnlyList<string> ExtensionPoints);

/// <summary>Context passed to a plugin's pre-invoke hook.</summary>
internal sealed record ToolHookContextPayload(
    string Tool,
    JsonElement Arguments,
    string CallId);

/// <summary>Context passed to a plugin's post-invoke hook; observation only.</summary>
internal sealed record ToolPostHookContextPayload(
    string Tool,
    JsonElement Arguments,
    string CallId,
    string Status,
    JsonElement? Result = null);

/// <summary>Context passed to a plugin's MCP discovery extension point.</summary>
internal sealed record DiscoverContextPayload(string Reason);
