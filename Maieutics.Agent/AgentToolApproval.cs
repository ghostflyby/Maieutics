using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>
///     The classification hints an approval gate consults for one tool. Producers attach it
///     to <see cref="AIFunction.AdditionalProperties" /> under
///     <see cref="AgentToolClassification.PropertiesKey" />; the Agent runtime never
///     interprets the hints itself, it forwards them to the gate, which owns the policy.
/// </summary>
/// <param name="ReadOnly">The tool changes no state.</param>
/// <param name="Destructive">The tool may destroy data or resources.</param>
public sealed record AgentToolClassification(bool ReadOnly, bool Destructive)
{
    /// <summary>The additional-properties key a producer uses to publish its classification.
    /// Provider adapters never read additional properties, so forwarding hints here cannot
    /// widen the provider wire.</summary>
    public const string PropertiesKey = "maieutics.toolClassification";

    /// <summary>Reads the classification a function's producer attached, or null when it
    /// declared none.</summary>
    public static AgentToolClassification? From(AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return function.AdditionalProperties is { } properties &&
               properties.TryGetValue(PropertiesKey, out var value) &&
               value is AgentToolClassification classification
            ? classification
            : null;
    }
}

/// <summary>One tool call suspended for approval.</summary>
/// <param name="SessionId">The owning session.</param>
/// <param name="RunId">The owning run.</param>
/// <param name="CallId">The Maieutics tool-call identity the decision binds to.</param>
/// <param name="ToolName">The provider-visible function name.</param>
/// <param name="Arguments">Detached untrusted model arguments.</param>
/// <param name="Classification">The tool's declared hints, or null when it declares none.</param>
/// <param name="Reason">Why this call needs approval. This is policy text, never model output.</param>
public sealed record AgentToolApprovalRequest(
    AgentSessionId SessionId,
    AgentRunId RunId,
    AgentToolCallId CallId,
    string ToolName,
    JsonElement Arguments,
    AgentToolClassification? Classification,
    string Reason);

/// <summary>The terminal decision for one approval request.</summary>
/// <param name="Approved">Whether the call may proceed.</param>
/// <param name="Reason">A short explanation; always populated so the model and the UI can see why.</param>
/// <param name="AlwaysAllow">Whether the gate should skip approval for this tool for the rest
/// of the session. Ignored when <paramref name="Approved" /> is false.</param>
public sealed record AgentToolApprovalDecision(bool Approved, string Reason, bool AlwaysAllow)
{
    /// <summary>A denial with the given explanation.</summary>
    public static AgentToolApprovalDecision Deny(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new AgentToolApprovalDecision(false, reason, false);
    }

    /// <summary>An approval for this call only.</summary>
    public static AgentToolApprovalDecision Allow(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new AgentToolApprovalDecision(true, reason, false);
    }
}

/// <summary>
///     Decides whether one tool call may execute. The Agent runtime calls this immediately
///     before invoking a function, after <see cref="AgentToolStarted" /> and before any side
///     effect. Implementations own their waiting budget and must return a terminal decision:
///     they never throw for user absence, and a cancelled
///     <paramref name="cancellationToken" /> means the run ended, not that the user declined.
/// </summary>
/// <remarks>
///     A denial is a recoverable tool failure — the model receives a typed
///     <c>tool_denied</c> result and the turn continues. Implementations must not implement
///     this by throwing.
/// </remarks>
public interface IAgentToolApprovalGate
{
    /// <summary>Decides one call. Must return a decision; must not throw for a missing user.</summary>
    ValueTask<AgentToolApprovalDecision> RequestApprovalAsync(
        AgentToolApprovalRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
///     Keeps the session-scoped "always allow" set an approval gate consults. Registered as a
///     singleton over the process's Agent session, the set outlives individual runs. It is
///     deliberately separate from the layered permission store: allowing a tool is tool-call
///     authorization, not a change to the process or Deno grants of any child.
/// </summary>
/// <remarks>
///     There is no session-close signal in this process, so the set is bounded by a FIFO
///     rather than released: the oldest allowance is evicted once the cap is reached, exactly
///     as <c>PermissionOverrideRegistry</c> bounds its child-scope registrations. A live
///     session that re-prompts after an eviction is a safe failure (the user decides again);
///     an unbounded map would not be.
/// </remarks>
public sealed class AgentToolApprovalMemory
{
    /// <summary>The retained allowance cap. Each entry is a session id and a tool name, so the
    /// bound is far above any realistic session count while still being finite.</summary>
    private const int Capacity = 1024;

    private readonly Lock gate = new();
    private readonly Dictionary<(AgentSessionId SessionId, string Tool), bool> perSession =
        new(SessionKeyComparer.Instance);
    private readonly Queue<(AgentSessionId SessionId, string Tool)> order = new();

    /// <summary>Records that the session's user allowed the tool for the rest of the session.</summary>
    public void AllowAlways(AgentSessionId sessionId, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        lock (gate)
        {
            if (!perSession.TryAdd((sessionId, toolName), true)) return;
            order.Enqueue((sessionId, toolName));
            while (order.Count > Capacity) perSession.Remove(order.Dequeue());
        }
    }

    /// <summary>Whether the session's user already allowed the tool.</summary>
    public bool IsAllowed(AgentSessionId sessionId, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        lock (gate)
        {
            return perSession.ContainsKey((sessionId, toolName));
        }
    }

    /// <summary>Drops every remembered allowance for one session. No production surface calls
    /// this today (there is no session-close signal); it exists for the owner that gains one.</summary>
    public void Clear(AgentSessionId sessionId)
    {
        lock (gate)
        {
            foreach (var key in perSession.Keys.Where(key => key.SessionId == sessionId).ToArray())
                perSession.Remove(key);
        }
    }

    /// <summary>Drops every remembered allowance.</summary>
    public void ClearAll()
    {
        lock (gate)
        {
            perSession.Clear();
            order.Clear();
        }
    }

    private sealed class SessionKeyComparer : IEqualityComparer<(AgentSessionId SessionId, string Tool)>
    {
        internal static readonly SessionKeyComparer Instance = new();

        public bool Equals(
            (AgentSessionId SessionId, string Tool) left,
            (AgentSessionId SessionId, string Tool) right) =>
            left.SessionId == right.SessionId &&
            string.Equals(left.Tool, right.Tool, StringComparison.Ordinal);

        public int GetHashCode((AgentSessionId SessionId, string Tool) key) =>
            HashCode.Combine(key.SessionId, key.Tool.GetHashCode(StringComparison.Ordinal));
    }
}

/// <summary>The approval policy: which tools need a decision, and from what evidence.</summary>
public sealed record AgentToolApprovalPolicy
{
    /// <summary>Gets the tool-name patterns that always require approval (glob, case-insensitive).</summary>
    public IReadOnlyList<string> Require { get; init; } = [];

    /// <summary>Gets whether a tool the producer declared destructive requires approval.
    /// Defaults to true: a destructive tool is exactly what the gate exists for.</summary>
    public bool RequireForDestructive { get; init; } = true;

    /// <summary>Gets whether a tool the producer declared read-only is exempt even when its
    /// name matches <see cref="Require" />. Defaults to true — a hint in the widening direction.</summary>
    public bool ExemptReadOnly { get; init; } = true;

    /// <summary>Whether the given tool needs a decision, and why.</summary>
    public bool RequiresApproval(
        string toolName,
        AgentToolClassification? classification,
        out string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        // A producer's read-only declaration wins over a name pattern: the hint only ever
        // widens, so a misdeclared read-only tool cannot bypass the gate for a mutating one
        // that never claimed to be read-only.
        if (ExemptReadOnly && classification is { ReadOnly: true, Destructive: false })
        {
            reason = string.Empty;
            return false;
        }

        if (classification is { Destructive: true } && RequireForDestructive)
        {
            reason = $"The tool '{toolName}' declares that it may destroy data or resources.";
            return true;
        }

        foreach (var pattern in Require)
        {
            if (!Matches(toolName, pattern)) continue;
            reason = $"The tool '{toolName}' matches the configured approval rule '{pattern}'.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool Matches(string toolName, string pattern)
    {
        if (pattern.Length == 0) return false;
        if (pattern is "*" or "**") return true;

        // Only '*' is special: tool names are restricted to ASCII letters, digits, '_' and
        // '-', so a character-by-character matcher is enough and avoids a regex dependency.
        var name = toolName.AsSpan();
        var mask = pattern.AsSpan();
        var nameIndex = 0;
        var maskIndex = 0;
        var starIndex = -1;
        var starNameIndex = 0;
        while (nameIndex < name.Length)
        {
            if (maskIndex < mask.Length &&
                (char.ToLowerInvariant(mask[maskIndex]) == char.ToLowerInvariant(name[nameIndex]) ||
                 mask[maskIndex] == '?'))
            {
                nameIndex++;
                maskIndex++;
            }
            else if (maskIndex < mask.Length && mask[maskIndex] == '*')
            {
                starIndex = maskIndex++;
                starNameIndex = nameIndex;
            }
            else if (starIndex >= 0)
            {
                maskIndex = starIndex + 1;
                nameIndex = ++starNameIndex;
            }
            else
            {
                return false;
            }
        }

        while (maskIndex < mask.Length && mask[maskIndex] == '*') maskIndex++;
        return maskIndex == mask.Length;
    }
}
