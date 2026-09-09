using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Frontend;

/// <summary>Protocol-level constants shared by the frontend host and its tests.</summary>
internal static class FrontendProtocol
{
    /// <summary>Frontend protocol version implemented by this executable.</summary>
    public const int Version = 1;
}

/// <summary>Identifies the executable's capabilities to a frontend.</summary>
internal sealed record FrontendCapabilities(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("serverVersion")] string ServerVersion,
    [property: JsonPropertyName("session")] FrontendSessionInfo Session,
    [property: JsonPropertyName("workspaceRoot")] string? WorkspaceRoot = null,
    [property: JsonPropertyName("comm")] FrontendCommCapability? Comm = null);

/// <summary>Advertises the comm plane (ADR 0024); null means the build serves no comms.</summary>
internal sealed record FrontendCommCapability(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("maxMessageBytes")] int MaxMessageBytes);

/// <summary>Describes the active session to a frontend.</summary>
internal sealed record FrontendSessionInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("turns")] long Turns,
    [property: JsonPropertyName("persistenceEnabled")] bool PersistenceEnabled,
    [property: JsonPropertyName("title")] string? Title = null);

/// <summary>Describes one stored (persisted) session.</summary>
internal sealed record FrontendStoredSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("turns")] long Turns,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("lastActivityAt")] DateTimeOffset LastActivityAt,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("preview")] string? Preview = null,
    [property: JsonPropertyName("workspaceRoot")] string? WorkspaceRoot = null,
    [property: JsonPropertyName("parentSessionId")] string? ParentSessionId = null,
    [property: JsonPropertyName("forkPointSeq")] int? ForkPointSeq = null);

/// <summary>A session rename body; an empty title clears.</summary>
internal sealed record FrontendRenameRequest([property: JsonPropertyName("title")] string Title);

/// <summary>A rename answer carrying the stored (normalized) title.</summary>
internal sealed record FrontendRenameResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>A fork request body: exactly one of <c>runId</c> (a committed turn of the source
/// the fork rewinds to — the fork re-runs from that point) or <c>seq</c> (the number of the
/// source's committed turns the fork keeps, zero-based count), plus an optional
/// <c>profileId</c> that switches the model profile before the fork's first turn.</summary>
internal sealed record FrontendForkRequest(
    [property: JsonPropertyName("runId")] string? RunId = null,
    [property: JsonPropertyName("seq")] int? Seq = null,
    [property: JsonPropertyName("profileId")] string? ProfileId = null);

/// <summary>A fork answer carrying the new head's identity and stored title.</summary>
internal sealed record FrontendForkResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>A turn submission body.</summary>
internal sealed record FrontendTurnRequest([property: JsonPropertyName("text")] string Text);

/// <summary>A turn acceptance body.</summary>
internal sealed record FrontendTurnAccepted([property: JsonPropertyName("runId")] string RunId);

/// <summary>A command execution body.</summary>
internal sealed record FrontendCommandRequest([property: JsonPropertyName("text")] string Text);

/// <summary>A command execution answer. When the command switched the active session,
/// <c>sessionId</c> carries the new active session so the frontend can re-pin.</summary>
internal sealed record FrontendCommandResponse(
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("sessionId")] string? SessionId = null);

/// <summary>A completion request body; the cursor is a UTF-16 code-unit offset.</summary>
internal sealed record FrontendCompleteRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("cursor")] int Cursor);

/// <summary>A completion answer.</summary>
internal sealed record FrontendCompleteResponse(
    [property: JsonPropertyName("matches")] string[] Matches,
    [property: JsonPropertyName("tokenStart")] int TokenStart,
    [property: JsonPropertyName("tokenEnd")] int TokenEnd);

/// <summary>Token usage a provider reported for one run.</summary>
internal sealed record FrontendUsage(
    [property: JsonPropertyName("inputTokens")] long InputTokens,
    [property: JsonPropertyName("outputTokens")] long OutputTokens,
    [property: JsonPropertyName("totalTokens")] long? TotalTokens = null);

/// <summary>One selectable model profile (read-only listing for frontends).</summary>
internal sealed record FrontendModelProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("selected")] bool Selected);

/// <summary>A status answer.</summary>
internal sealed record FrontendStatusResponse([property: JsonPropertyName("markdown")] string Markdown);

/// <summary>An input request announcement (server to frontend).</summary>
internal sealed record FrontendInputRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("password")] bool Password);

/// <summary>An input answer body (frontend to server).</summary>
internal sealed record FrontendInputAnswer(
    [property: JsonPropertyName("value")] string Value);

/// <summary>A typed protocol error body.</summary>
internal sealed record FrontendError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>One provider-neutral transcript content part.</summary>
internal sealed record FrontendMessagePart(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("callId")] string? CallId = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("value")] JsonElement? Value = null);

/// <summary>One provider-neutral transcript message.</summary>
internal sealed record FrontendMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("parts")] IReadOnlyList<FrontendMessagePart> Parts);

/// <summary>The model identity that produced a transcript turn.</summary>
internal sealed record FrontendModelIdentity(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model);

/// <summary>One committed transcript turn.</summary>
internal sealed record FrontendTranscriptTurn(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("model")] FrontendModelIdentity? Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<FrontendMessage> Messages);

/// <summary>The authoritative committed history of one session.</summary>
internal sealed record FrontendTranscript(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("turns")] IReadOnlyList<FrontendTranscriptTurn> Turns);

/// <summary>One tool progress content value (text or embedded JSON).</summary>
internal sealed record FrontendProgressContent(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("value")] JsonElement? Value = null);

/// <summary>
///     One WebSocket event frame. Frames are a flat versioned record: exactly the fields named
///     by <c>Type</c> are populated and nulls are omitted on write, which keeps a single
///     source-generated type on the NativeAOT path (see docs/web-frontend-protocol.md).
/// </summary>
internal sealed record FrontendEventFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("runId")] string? RunId = null,
    [property: JsonPropertyName("sequence")] long? Sequence = null,
    [property: JsonPropertyName("messageId")] string? MessageId = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("callId")] string? CallId = null,
    [property: JsonPropertyName("tool")] string? Tool = null,
    [property: JsonPropertyName("arguments")] JsonElement? Arguments = null,
    [property: JsonPropertyName("content")] FrontendProgressContent? Content = null,
    [property: JsonPropertyName("result")] JsonElement? Result = null,
    [property: JsonPropertyName("displayId")] string? DisplayId = null,
    [property: JsonPropertyName("commId")] string? CommId = null,
    [property: JsonPropertyName("data")] JsonElement? Data = null,
    [property: JsonPropertyName("mime")] string? Mime = null,
    [property: JsonPropertyName("agentMessage")] FrontendMessage? AgentMessage = null,
    [property: JsonPropertyName("truncated")] bool? Truncated = null,
    [property: JsonPropertyName("code")] string? Code = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("requestId")] string? RequestId = null,
    [property: JsonPropertyName("prompt")] string? Prompt = null,
    [property: JsonPropertyName("password")] bool? Password = null,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("session")] FrontendSessionInfo? Session = null,
    [property: JsonPropertyName("replayed")] bool? Replayed = null,
    [property: JsonPropertyName("model")] FrontendModelIdentity? Model = null,
    [property: JsonPropertyName("usage")] FrontendUsage? Usage = null);

/// <summary>Source-generated JSON binding for the frontend wire (NativeAOT path).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FrontendCapabilities))]
[JsonSerializable(typeof(FrontendSessionInfo))]
[JsonSerializable(typeof(FrontendStoredSession))]
[JsonSerializable(typeof(FrontendSessionInfo[]))]
[JsonSerializable(typeof(FrontendTurnRequest))]
[JsonSerializable(typeof(FrontendTurnAccepted))]
[JsonSerializable(typeof(FrontendRenameRequest))]
[JsonSerializable(typeof(FrontendRenameResponse))]
[JsonSerializable(typeof(FrontendForkRequest))]
[JsonSerializable(typeof(FrontendForkResponse))]
[JsonSerializable(typeof(FrontendCommandRequest))]
[JsonSerializable(typeof(FrontendCommandResponse))]
[JsonSerializable(typeof(FrontendCompleteRequest))]
[JsonSerializable(typeof(FrontendCompleteResponse))]
[JsonSerializable(typeof(FrontendInputRequest))]
[JsonSerializable(typeof(FrontendUsage))]
[JsonSerializable(typeof(FrontendModelProfile))]
[JsonSerializable(typeof(FrontendModelProfile[]))]
[JsonSerializable(typeof(FrontendStatusResponse))]
[JsonSerializable(typeof(FrontendInputAnswer))]
[JsonSerializable(typeof(FrontendError))]
[JsonSerializable(typeof(FrontendCommHello))]
[JsonSerializable(typeof(FrontendCommDescriptor[]))]
[JsonSerializable(typeof(FrontendCommCapability))]
[JsonSerializable(typeof(FrontendTranscript))]
[JsonSerializable(typeof(FrontendEventFrame))]
[JsonSerializable(typeof(FrontendDiscoveryFile))]
[JsonSerializable(typeof(FrontendStoredSession[]))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, object?>))]
[JsonSerializable(typeof(System.Collections.Generic.IReadOnlyDictionary<string, System.Text.Json.JsonElement>))]
internal sealed partial class FrontendJsonContext : JsonSerializerContext;
