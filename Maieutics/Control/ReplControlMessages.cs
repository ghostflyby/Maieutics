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
[JsonSerializable(typeof(ExtensionRegistryPlugin))]
[JsonSerializable(typeof(PluginStatePayload))]
[JsonSerializable(typeof(ToolHookContextPayload))]
[JsonSerializable(typeof(ToolPostHookContextPayload))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(DiscoverContextPayload))]
[JsonSerializable(typeof(HostReplSpawnedPayload))]
[JsonSerializable(typeof(HostReplExitedPayload))]
[JsonSerializable(typeof(HostReplDerivePayload))]
[JsonSerializable(typeof(HostReplDeriveFailedPayload))]
[JsonSerializable(typeof(HostReplPermissions))]
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
    public const string PluginReload = "plugin.reload";
    public const string HostReplSpawned = "host.repl.spawned";
    public const string HostReplExited = "host.repl.exited";
    public const string HostReplDerive = "host.repl.derive";
    public const string HostReplDeriveFailed = "host.repl.deriveFailed";
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
internal sealed record ExtensionRegistryPayload(
    IReadOnlyList<ExtensionRegistryPlugin> Plugins,
    IReadOnlyList<PluginStatePayload>? States = null);

internal sealed record ExtensionRegistryPlugin(
    string PluginId,
    string ExportName,
    IReadOnlyList<string> ExtensionPoints,
    string? Specifier = null);

/// <summary>Per-worker lifecycle state published with every registry snapshot.</summary>
internal sealed record PluginStatePayload(
    string PluginId,
    string ExportName,
    string Specifier,
    string State,
    string? Failure = null);

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

/// <summary>
///     Host-to-kernel report that the plugin host derived a Deno REPL process for a session
///     (ADR 0020). Carries the REPL child's self-reported <c>Deno.pid</c> so the kernel can
///     register the permission-broker policy and the control-channel identity by pid, exactly as
///     it does for a kernel-derived REPL. Aligns with <c>host.repl.spawned</c> in
///     <c>deno/maieutics-plugin-host/host_repl_protocol.ts</c>.
/// </summary>
internal sealed record HostReplSpawnedPayload(string SessionId, int Generation, int Pid);

/// <summary>
///     Host-to-kernel report that a host-derived Deno REPL process exited (ADR 0020). Releases
///     the pid-scoped permission-broker policy and control-channel session identity. Aligns with
///     <c>host.repl.exited</c> in <c>deno/maieutics-plugin-host/host_repl_protocol.ts</c>; the
///     optional <see cref="Failure"/> mirrors the draft's optional <c>failure</c> reason.
/// </summary>
internal sealed record HostReplExitedPayload(
    string SessionId,
    int Generation,
    int Pid,
    string? Failure = null);

/// <summary>
///     Kernel-to-host instruction to derive a Deno REPL process (ADR 0020, B5). The host is the
///     spawner; the kernel decides the entry module, the complete child environment, and the static
///     permission shell. The host answers with <c>host.repl.spawned</c> / <c>host.repl.exited</c> /
///     <c>host.repl.deriveFailed</c>. Aligns with <c>HostReplDerivePayload</c> in
///     <c>deno/maieutics-plugin-host/host_repl_protocol.ts</c>; field names are CamelCase and
///     <see cref="HostReplPermissions"/> mirrors the draft's <c>boolean | string[]</c> kinds.
/// </summary>
internal sealed record HostReplDerivePayload(
    string SessionId,
    int Generation,
    string EntryUrl,
    Dictionary<string, string> Env,
    HostReplPermissions? Permissions = null,
    bool Report = true);

/// <summary>
///     Host-to-kernel report that a <c>host.repl.derive</c> instruction could not be executed
///     BEFORE any pid existed (validation or spawn failure). A failure after the spawn report is
///     reported as <c>host.repl.exited</c> instead, so the kernel never sees both. Aligns with
///     <c>host.repl.deriveFailed</c> in <c>deno/maieutics-plugin-host/host_repl_protocol.ts</c>.
/// </summary>
internal sealed record HostReplDeriveFailedPayload(
    string SessionId,
    int Generation,
    string Message);

/// <summary>
///     Static permission shell the kernel ships with a <c>host.repl.derive</c> instruction, in the
///     <c>Deno.PermissionOptionsObject</c> shape worker-actor <c>spawnProcess</c> accepts:
///     <c>true</c> = allow all, <c>string[]</c> = allowlist, absent = deny. Each kind is a
///     <see cref="JsonElement"/> so both shapes survive source-generated serialization (the same
///     approach <c>PluginHostConfigPermissions</c> uses). Denied kinds are <see langword="null"/>
///     and omitted from the wire (the host's parser accepts only booleans and string arrays).
///     This is the broker's fallback baseline, NOT a security boundary (ADR 0020 decision 1).
///     Aligns with <c>HostReplPermissions</c> in <c>deno/maieutics-plugin-host/host_repl_protocol.ts</c>.
/// </summary>
internal sealed record HostReplPermissions(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Read = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Write = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Net = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Env = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Run = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Ffi = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Sys = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Import = null);
