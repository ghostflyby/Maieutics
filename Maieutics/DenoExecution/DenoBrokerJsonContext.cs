using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.DenoExecution;

/// <summary>One permission request from an internal Deno child, as the official broker protocol
/// sends it (verified against Deno 2.9.5): <c>{"v":1,"pid":...,"id":...,"datetime":...,"permission":"read","value":"/path"}</c>.</summary>
internal sealed record DenoBrokerRequest
{
    [JsonPropertyName("v")]
    public int Version { get; init; }

    [JsonPropertyName("pid")]
    public int ProcessId { get; init; }

    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("datetime")]
    public string? Datetime { get; init; }

    [JsonPropertyName("permission")]
    public string? Permission { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary>The broker's reply to one request: <c>{"id":1,"result":"allow"}</c> or
/// <c>{"id":2,"result":"deny","reason":"..."}</c>. The reason surfaces as the child's
/// <c>NotCapable</c> message (verified).</summary>
internal sealed record DenoBrokerResponse
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    internal DenoBrokerResponse(long id, DenoBrokerDecision decision)
    {
        Id = id;
        Result = decision.IsAllowed ? "allow" : "deny";
        Reason = decision.Reason;
    }
}

/// <summary>Source-generated JSON contract for the broker wire protocol (NativeAOT-safe).</summary>
[JsonSerializable(typeof(DenoBrokerRequest))]
[JsonSerializable(typeof(DenoBrokerResponse))]
internal sealed partial class DenoBrokerJsonContext : JsonSerializerContext
{
}
