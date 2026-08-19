namespace Maieutics.DenoExecution;

/// <summary>Marks the two internal Deno child kinds supervised by <see cref="DenoRunProcess"/>.
/// Internal children (Deno REPL, plugin host) are privileged by Deno permissions and are never
/// process-sandbox targets; sandboxes apply only to general starts (terminal, MCP)
/// (AGENTS.md invariant 22; ADR 0018 §8).</summary>
internal enum InternalDenoProcessKind
{
    DenoRepl,
    PluginHost
}
