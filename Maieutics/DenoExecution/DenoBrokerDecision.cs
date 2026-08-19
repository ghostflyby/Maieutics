namespace Maieutics.DenoExecution;

/// <summary>Broker-side permission check result for one request, matching the wire
/// response shape the Deno child expects (verified against Deno 2.9.5).</summary>
internal readonly record struct DenoBrokerDecision(bool IsAllowed, string? Reason)
{
    internal static DenoBrokerDecision Allow() => new(true, null);

    internal static DenoBrokerDecision Deny(string reason) => new(false, reason);
}
