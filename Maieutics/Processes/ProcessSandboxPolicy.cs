namespace Maieutics.Processes;

/// <summary>Future process-sandbox enforcement seam for general starts (terminal, MCP). No sandbox
/// enforcer is written in any phase of the ADR 0018 plan; this type only fixes the shape a future
/// seatbelt/bwrap enforcer will implement. A future enforcer renders the same <c>EffectivePolicy</c>
/// snapshot the Deno renderer consumes (ADR 0018 §12, §7).</summary>
internal interface IProcessSandboxPolicy
{
}
