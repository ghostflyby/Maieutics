# Maieutics.Product.Tests instructions

Use `.agents/skills/maieutics-dotnet-testing/SKILL.md` for shared xUnit, assertion, deadline, socket, process, and
verification rules.

## Ownership

This project owns tests for the `Maieutics` executable and its product composition: the frontend web API and process
smoke surfaces, provider adapters, configuration reload, permissions, processes, Deno execution/REPL, plugins, control
plane, commands, and persistence. It references `Maieutics` and `Maieutics.Agent` and must not reference the retained
`Maieutics.Jupyter.*` libraries. Jupyter library tests belong in `Maieutics.Jupyter.Tests`; Agent runtime-only unit
tests belong in `Maieutics.Agent.Tests`.

## Coverage ownership

- Frontend API: discovery, auth, turn lifecycle, replay/resume, cancel, commands, completion, sessions/forks, input
  answers, comm plane, and the published-executable smoke tests (`Category=Smoke`).
- Providers: deterministic fake HTTP/SSE servers for OpenAI Responses, Chat Completions, and Anthropic Messages,
  including tool continuation, provider switching, and cancellation. Never contact real model services.
- Configuration, permissions, processes: layered overlays, variable interpolation, reload, process environment
  allowlists.
- Deno execution/REPL and control plane: sessions, eval/output/control IPC, comm channels, host derive, policy broker.
- Commands: command language, completion (UTF-16 token ranges), status rendering, markdown escaping, session manager,
  persistence stores.
- Tests that mutate process-wide state (environment variables, the polling file-watcher flag) or bind sockets must
  join the non-parallel `ProductIntegrationCollection`.
