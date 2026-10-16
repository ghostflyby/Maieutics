namespace Maieutics.Agent;

/// <summary>
///     Per-call identity for script-invoked tools (REPL <c>tools.invoke</c>, plugin
///     <c>capability.invoke</c>) whose arguments carry no <see cref="AgentToolContext" />.
///     The control host binds the calling connection's owning session before invoking, so
///     session-scoped gates — the IDE workspace bridge (ADR 0042 decision 12) — apply the
///     caller's own session instead of degrading to the process level.
/// </summary>
public sealed record ScriptInvocationContext(string? SessionId)
{
    /// <summary>Reads the context from function arguments, or null when the caller is
    /// not a session-bound script invocation (plugin relays without a session).</summary>
    public static ScriptInvocationContext? TryGet(Microsoft.Extensions.AI.AIFunctionArguments arguments)
    {
        return arguments.Context?.TryGetValue(typeof(ScriptInvocationContext), out var value) == true &&
               value is ScriptInvocationContext context
            ? context
            : null;
    }
}
