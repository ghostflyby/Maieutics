using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Control;

/// <summary>Versioned model-orchestration spawn request carried by the control channel
/// (ADR 0031): one subagent child run, scoped to the calling Deno process's owning Agent
/// session.</summary>
internal sealed record SubagentSpawnRequest(
    int Version,
    string Input,
    string? Instructions = null,
    string[]? Tools = null,
    string? SessionId = null);

/// <summary>The handle of one spawned subagent child run.</summary>
internal sealed record SubagentSpawnedPayload(
    string ChildSessionId,
    string RunId,
    string TaskUri,
    string Status);

/// <summary>The terminal snapshot of one subagent child run; statuses follow the task
/// plane's vocabulary (complete, fail, cancel).</summary>
internal sealed record SubagentResultPayload(
    string ChildSessionId,
    string RunId,
    string Status,
    string? Report,
    bool Truncated,
    SubagentUsagePayload? Usage);

/// <summary>Versioned task-plane cancellation request: the task URI plus the presenting
/// REPL session whose owning Agent session must own the task.</summary>
internal sealed record TaskCancelRequest(
    string Uri,
    string? SessionId = null);

/// <summary>Provider-reported token usage of one subagent child run.</summary>
internal sealed record SubagentUsagePayload(int? Input, int? Output, int? Total);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    MaxDepth = ReplControlLimits.MaximumJsonDepth)]
[JsonSerializable(typeof(SubagentSpawnRequest))]
[JsonSerializable(typeof(SubagentSpawnedPayload))]
[JsonSerializable(typeof(SubagentResultPayload))]
[JsonSerializable(typeof(SubagentUsagePayload))]
[JsonSerializable(typeof(TaskCancelRequest))]
[JsonSerializable(typeof(ToolInvokeRequest))]
[JsonSerializable(typeof(ToolInvokePayload))]
[JsonSerializable(typeof(PluginUiFramePayload))]
[JsonSerializable(typeof(PluginHttpGatewayPayload))]
[JsonSerializable(typeof(PluginHttpMountPayload))]
[JsonSerializable(typeof(CapabilityInvokePayload))]
[JsonSerializable(typeof(CapabilityResultPayload))]
[JsonSerializable(typeof(ReplEnvelope))]
[JsonSerializable(typeof(BusCancelPayload))]
[JsonSerializable(typeof(BusCommPayload))]
[JsonSerializable(typeof(BusErrorPayload))]
[JsonSerializable(typeof(BusAckPayload))]
[JsonSerializable(typeof(ToolProgressPayload))]
[JsonSerializable(typeof(PluginHelloPayload))]
[JsonSerializable(typeof(HostInvokePayload))]
[JsonSerializable(typeof(HostInvokeResultPayload))]
[JsonSerializable(typeof(HostInvokeErrorPayload))]
[JsonSerializable(typeof(ExtensionRegistryPayload))]
[JsonSerializable(typeof(PluginTriggerPayload))]
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
[JsonSerializable(typeof(SkillsInvokePayload))]

internal sealed partial class ReplControlJsonContext : JsonSerializerContext;

/// <summary>The request the kernel sends a worker's <c>Skills</c> generator export
/// (ADR 0039 stage 2b): the live workspace root, never arbitrary environment.</summary>
internal sealed record SkillsInvokePayload(string? WorkspaceRoot);

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
///     One native view-family comm frame pushed by a plugin worker through the
///     <c>ui.models</c> capability (ADR 0038 stage 3): the worker-originated mirror of the
///     comm plane's open/message/close shapes. <c>Kind</c> is <c>open</c> (requires
///     <c>TargetName</c> under <c>maieutics.view/</c>), <c>message</c>, or <c>close</c>.
/// </summary>
internal sealed record PluginUiFramePayload(
    string Kind,
    string CommId,
    string? TargetName = null,
    JsonElement? Data = null);

/// <summary>
///     Versioned message envelope shared by every control channel bus message. Payloads are
///     domain-shaped JSON; binary data never rides this envelope (native binary frames carry it).
/// </summary>
internal sealed record ReplEnvelope(
    int Version,
    string Type,
    string? CorrelationId = null,
    JsonElement? Payload = null);

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
    public const string HostInvoke = "host.invoke";
    public const string HostInvokeResult = "host.invokeResult";
    public const string HostInvokeError = "host.invokeError";
    public const string ExtensionRegistry = "extension.registry";
    public const string PluginReload = "plugin.reload";
    public const string PluginTrigger = "plugin.trigger";
    public const string HostReplSpawned = "host.repl.spawned";
    public const string HostReplExited = "host.repl.exited";
    public const string HostReplDerive = "host.repl.derive";
    public const string HostReplDeriveFailed = "host.repl.deriveFailed";
    public const string CapabilityInvoke = "capability.invoke";
    public const string CapabilityResult = "capability.result";
    public const string CapabilityError = "capability.error";
    public const string Error = "error";
}

/// <summary>The core-predefined capability catalog (ADR 0020 §7.2): the closed set of
/// internal kernel capabilities a plugin may request through the host. A capability the
/// catalog does not list is refused before any per-plugin grant check, so new capabilities
/// are always a deliberate kernel-side addition.</summary>
internal static class ReplCapabilityName
{
    public const string ToolsInvoke = "tools.invoke";

    /// <summary>Plugin-owned UI models (ADR 0038 stage 3): a granted worker pushes native
    /// view-family comm frames through the kernel into the session's comm plane.</summary>
    public const string UiModels = "ui.models";

    /// <summary>Plugin skill publication (ADR 0039 stage 3): a granted worker replaces its
    /// published skill set at runtime; the entries merge into the plugin's contribution
    /// slot below the declarative roots and the generated part.</summary>
    public const string SkillsPublish = "skills.publish";
}

internal static class PluginCapabilityCatalog
{
    public static readonly IReadOnlyList<string> All =
        [ReplCapabilityName.ToolsInvoke, ReplCapabilityName.UiModels, ReplCapabilityName.SkillsPublish];

    public static bool Contains(string capability)
    {
        return All.Any(entry => entry == capability);
    }
}

internal static class ReplExtensionPointName
{
    public const string McpDiscover = "McpDiscover";
    public const string McpAdjust = "McpAdjust";
    public const string ToolPreInvoke = "ToolPreInvoke";
    public const string ToolPostInvoke = "ToolPostInvoke";

    /// <summary>Plugin UI uplink events (ADR 0038 stage 3): the kernel invokes the
    /// worker's registered UiEvent export with {commId, name, payload} when the
    /// frontend sends a frame for one of the plugin's models.</summary>
    public const string UiEvent = "UiEvent";

    /// <summary>Plugin skill-catalog generator (ADR 0039 stage 2): the kernel invokes the
    /// worker's registered Skills export at load/reload and reconciles the returned
    /// descriptor array into the plugin's skill contribution slot. Workers report the
    /// canonical spelling; matching is case-insensitive and the lowercase form is the
    /// recommended authoring spelling.</summary>
    public const string Skills = "Skills";

    public static bool IsKnown(string name)
    {
        return name.Equals(McpDiscover, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(McpAdjust, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(ToolPreInvoke, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(ToolPostInvoke, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(UiEvent, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(Skills, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The canonical constant for a known name (any spelling), or null.
    /// Registrations record the canonical spelling so downstream exact comparisons stay
    /// correct regardless of what the reporting side spelled.</summary>
    public static string? Canonicalize(string name)
    {
        foreach (var candidate in (string[]) [McpDiscover, McpAdjust, ToolPreInvoke, ToolPostInvoke, UiEvent, Skills])
            if (name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return candidate;
        return null;
    }
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

/// <summary>
///     Kernel-to-host request to invoke one extension point on one plugin worker (replaces the
///     retired <c>extension.invoke</c> protocol, ADR 0020 §7.2). The host calls the plugin
///     worker's <c>Remote&lt;T&gt;</c> surface directly in-process and answers with
///     <c>host.invokeResult</c> / <c>host.invokeError</c>, echoing the envelope correlationId.
/// </summary>
/// <summary>A plugin worker's request for one core-predefined kernel capability, relayed
/// by the trusted plugin host (ADR 0018 decision 8) with the worker's plugin identity
/// derived from the worker→plugin mapping, never from the frame.</summary>
internal sealed record CapabilityInvokePayload(
    string PluginId,
    string Capability,
    JsonElement? Payload = null);

/// <summary>The kernel's answer to a <see cref="CapabilityInvokePayload" />; the payload is
/// the capability's own result document (for <c>tools.invoke</c>, the tool result
/// envelope).</summary>
internal sealed record CapabilityResultPayload(JsonElement Result);

internal sealed record HostInvokePayload(
    string PluginId,
    string ExportName,
    string ExtensionPoint,
    JsonElement? Request = null);

/// <summary>Host-to-kernel response carrying the extension point result.</summary>
internal sealed record HostInvokeResultPayload(JsonElement? Value = null);

/// <summary>Host-to-kernel typed failure for an extension point call.</summary>
internal sealed record HostInvokeErrorPayload(string Code, string Message);

/// <summary>Host-to-kernel registry snapshot of scanned extension points per worker.</summary>
/// <summary>A trigger fired on the host (ADR 0036): the kernel republishes the named
/// plugin's MCP registration subset — discovery re-runs and the coordinator recomposes.
/// No worker wake: the rediscover action is pure runtime.</summary>
internal sealed record PluginTriggerPayload(string PluginId, string Trigger);

internal sealed record ExtensionRegistryPayload(
    IReadOnlyList<ExtensionRegistryPlugin> Plugins,
    IReadOnlyList<PluginStatePayload>? States = null,
    PluginHttpGatewayPayload? HttpGateway = null);

/// <summary>The plugin HTTP gateway's entrance (ADR 0021/0038 stage 4): the host
/// reserves the loopback address and entrance token; the token is a capability
/// carrier — it reaches only bearer-authed frontend surfaces, never logs.</summary>
internal sealed record PluginHttpGatewayPayload(
    string Hostname,
    int Port,
    string Token,
    IReadOnlyList<PluginHttpMountPayload> Mounts);

/// <summary>One live plugin page mount on the gateway.</summary>
internal sealed record PluginHttpMountPayload(
    string PluginId,
    string Specifier,
    bool Live);

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
    JsonElement? Result = null,
    string? Origin = null,
    string? SessionId = null);

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
