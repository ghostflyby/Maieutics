# Maieutics.Jupyter adapter instructions

Use `.agents/skills/maieutics-agent-runtime/SKILL.md` and
`.agents/skills/maieutics-jupyter-protocol/SKILL.md` for cross-domain mapping rules.

## Ownership

This folder is the product adapter between `Maieutics.Agent` and `Maieutics.Jupyter.Kernel`. It owns conversion of one
ordinary cell into one Agent run, semantic Agent events into ordered Jupyter output, product control commands, command
completion, batching, and safe execution error mapping.

It is the only production code allowed to understand both Agent and Jupyter domains.

## Adapter constraints

- Empty cells are successful no-ops. Ordinary non-command cells create one Agent turn.
- `%model` and `%workspace` subcommands are Kernel control cells (the legacy `%maieutics model|workspace` form remains
  accepted): they do not call a model, alter the transcript, reveal credentials/endpoints, or affect an active run.
  Workspace selection is process-local and applies to subsequent workspace-tool invocations.
- `%status` is a synchronous read-only snapshot command. It must not call a model, mutate the transcript, wait for
  readiness, refresh discovery, or expose absolute workspace/REPL paths, credentials, endpoints, or control addresses.
- Completion for control commands and dynamic profile IDs must use protocol code-point cursor offsets and return exact
  replacement ranges.
- Capture flush options at execution start. Configuration reload must not change batching during an active execution.
- Start the run explicitly, consume its single event stream, await `Completion`, and dispose it on every path.
- Jupyter interrupt propagates cancellation through the run. Partial display remains visible, but failed or canceled
  turns do not enter the transcript.
- `silent` still executes turns and commands while the kernel context suppresses display output.
- Stream assistant text through tracked display/update operations in event order. Do not expose tool lifecycle events in
  the Notebook until their presentation semantics are explicitly designed.
- Preserve rich content and structured MIME until this boundary and always provide a useful `text/plain` fallback.
- Never expose private chain-of-thought. Only explicitly permitted provider reasoning summaries may be rendered.
- Convert expected Agent failures to stable, non-sensitive Jupyter errors; log full exceptions only through protected
  process logging.
