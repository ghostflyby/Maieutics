using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Maieutics.DenoRepl;

internal static class ReplEvalProtocol
{
    internal const int Version = 1;
    internal const string WebSocketPath = "/v1/repl/eval/ws";
    internal const int MaximumMessageBytes = 1024 * 1024;
    internal const int QueueCapacity = 64;

    internal static ReplEvalEnvelope Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
            throw new ReplEvalProtocolException(
                "message_too_large",
                $"REPL eval messages must not exceed {MaximumMessageBytes} bytes.");

        ReplEvalEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(json, ReplEvalJsonContext.Default.ReplEvalEnvelope)
                       ?? throw new JsonException("The REPL eval envelope is null.");
        }
        catch (JsonException exception)
        {
            throw new ReplEvalProtocolException(
                "invalid_json",
                "The REPL eval message is not valid JSON.",
                innerException: exception);
        }

        if (envelope.Version != Version)
            throw new ReplEvalProtocolException(
                "unsupported_version",
                $"Unsupported REPL eval protocol version '{envelope.Version}'.",
                envelope.CorrelationId);
        if (string.IsNullOrWhiteSpace(envelope.Type))
            throw new ReplEvalProtocolException(
                "invalid_envelope",
                "The REPL eval envelope requires a message type.",
                envelope.CorrelationId);
        if (!ReplEvalMessageType.All.Contains(envelope.Type))
            throw new ReplEvalProtocolException(
                "unknown_message_type",
                $"Unknown REPL eval message type '{envelope.Type}'.",
                envelope.CorrelationId);
        if (string.IsNullOrWhiteSpace(envelope.CorrelationId))
            throw new ReplEvalProtocolException(
                "invalid_envelope",
                "The REPL eval envelope requires a correlation id.");

        return envelope;
    }

    internal static byte[] Serialize(ReplEvalEnvelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ReplEvalJsonContext.Default.ReplEvalEnvelope);
        if (bytes.Length > MaximumMessageBytes)
            throw new ReplEvalProtocolException(
                "message_too_large",
                $"REPL eval messages must not exceed {MaximumMessageBytes} bytes.",
                envelope.CorrelationId);
        return bytes;
    }

    internal static T ParsePayload<T>(ReplEvalEnvelope envelope, JsonTypeInfo<T> typeInfo)
    {
        if (envelope.Payload is not { } payload || payload.ValueKind != JsonValueKind.Object)
            throw new ReplEvalProtocolException(
                "invalid_payload",
                $"Message '{envelope.Type}' requires an object payload.",
                envelope.CorrelationId);

        try
        {
            return payload.Deserialize(typeInfo)
                   ?? throw new JsonException($"The '{envelope.Type}' payload is null.");
        }
        catch (JsonException exception)
        {
            throw new ReplEvalProtocolException(
                "invalid_payload",
                $"Message '{envelope.Type}' has an invalid payload.",
                envelope.CorrelationId,
                exception);
        }
    }

    internal static JsonElement Payload<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return JsonSerializer.SerializeToElement(value, typeInfo);
    }
}

internal static class ReplEvalMessageType
{
    internal const string Hello = "repl.eval.hello";
    internal const string Ready = "repl.eval.ready";
    internal const string Execute = "repl.eval.execute";
    internal const string Cancel = "repl.eval.cancel";
    internal const string Dispose = "repl.eval.dispose";
    internal const string Result = "repl.eval.result";
    internal const string Error = "repl.eval.error";
    internal const string Cancelled = "repl.eval.cancelled";
    internal const string InputRequest = "repl.eval.inputRequest";
    internal const string InputReply = "repl.eval.inputReply";

    internal static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Hello,
        Ready,
        Execute,
        Cancel,
        Dispose,
        Result,
        Error,
        Cancelled,
        InputRequest,
        InputReply
    };
}

internal sealed record ReplEvalEnvelope(
    int Version,
    string Type,
    string CorrelationId,
    JsonElement? Payload = null);

internal sealed record ReplEvalIdentity(
    string SessionId,
    int Generation,
    string? Credential = null);

internal sealed record ReplEvalExecutePayload(string ExecutionId, string Code);

internal sealed record ReplEvalCancelPayload(string ExecutionId);

internal sealed record ReplEvalInputRequestPayload(
    string ExecutionId,
    long Sequence,
    string RequestId,
    string Prompt,
    bool Password);

internal sealed record ReplEvalInputReplyPayload(
    string ExecutionId,
    string RequestId,
    string Value);

internal sealed record ReplEvalResultPayload(
    string? ExecutionId = null,
    JsonElement? Value = null);

internal sealed record ReplEvalErrorPayload(
    string Code,
    string Message,
    string? ExecutionId = null,
    bool? Fatal = null);

internal sealed record ReplEvalCancelledPayload(string ExecutionId);

internal abstract record ReplEvalEvent(string ExecutionId, long Sequence);

internal sealed record ReplEvalInputRequestEvent(
    string ExecutionId,
    long Sequence,
    string RequestId,
    string Prompt,
    bool Password) : ReplEvalEvent(ExecutionId, Sequence);

internal abstract record ReplEvalTerminal(string ExecutionId);

internal sealed record ReplEvalResultTerminal(
    string ExecutionId,
    JsonElement? Value) : ReplEvalTerminal(ExecutionId);

internal sealed record ReplEvalErrorTerminal(
    string ExecutionId,
    string Code,
    string Message,
    bool Fatal) : ReplEvalTerminal(ExecutionId);

internal sealed record ReplEvalCancelledTerminal(
    string ExecutionId) : ReplEvalTerminal(ExecutionId);

internal sealed class ReplEvalProtocolException(
    string code,
    string message,
    string? correlationId = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    internal string Code { get; } = code;

    internal string? CorrelationId { get; } = correlationId;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 64)]
[JsonSerializable(typeof(ReplEvalEnvelope))]
[JsonSerializable(typeof(ReplEvalIdentity))]
[JsonSerializable(typeof(ReplEvalExecutePayload))]
[JsonSerializable(typeof(ReplEvalCancelPayload))]
[JsonSerializable(typeof(ReplEvalInputRequestPayload))]
[JsonSerializable(typeof(ReplEvalInputReplyPayload))]
[JsonSerializable(typeof(ReplEvalResultPayload))]
[JsonSerializable(typeof(ReplEvalErrorPayload))]
[JsonSerializable(typeof(ReplEvalCancelledPayload))]
internal sealed partial class ReplEvalJsonContext : JsonSerializerContext;
