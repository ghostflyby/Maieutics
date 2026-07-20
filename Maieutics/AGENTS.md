# Maieutics executable instructions

Use `.agents/skills/maieutics-agent-runtime/SKILL.md` for product composition semantics and
`.agents/skills/maieutics-structured-concurrency/SKILL.md` for host/process lifetime changes.

## Ownership

This project is the `maieutics` NativeAOT executable and composition root. It owns process startup and shutdown, Generic
Host and DI registration, external configuration selection, product provider factories, the Agent-to-Jupyter adapter,
kernelspec deployment, and process-level logging.

Logical namespaces under this project are product modules, not independent SDK assemblies.

## Forbidden responsibilities

- Do not place reusable Jupyter protocol, client, kernel-host, Agent runtime, tool, or persistence logic here.
- Do not expose product provider or configuration implementation types as reusable public API.
- Do not move provider SDK types into `Maieutics.Agent` or Jupyter projects.
- Do not add HTTP hosting merely to obtain DI; use Generic Host unless an actual HTTP requirement exists.

## Process lifecycle

- Keep `Program` and constructors side-effect free; compose services in the host setup.
- The process requires a connection file, starts one `JupyterKernelHost`, observes its completion, and stops the Generic
  Host after Jupyter shutdown.
- Process-level connection information is captured at startup. Reloadable model and presentation settings apply only at
  operation boundaries.
- Never log full configuration, API keys, provider authorization, or connection-file credentials.
- Keep the executable compatible with trimming and NativeAOT. Do not bypass new incompatibilities with broad warning
  suppression; provider and framework paths require publish and process smoke coverage.
- The kernelspec remains a product deployment asset and uses `interrupt_mode: message`.

Read the narrower instructions before changing:

- `Configuration/AGENTS.md`
- `Providers/AGENTS.md`
- `Jupyter/AGENTS.md`
