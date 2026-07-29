# Maieutics.Jupyter.Tests instructions

Use `.agents/skills/maieutics-dotnet-testing/SKILL.md` for shared xUnit, assertion, deadline, socket, process, and
verification rules. Use `.agents/skills/maieutics-jupyter-protocol/SKILL.md` for cross-layer protocol expectations.

## Ownership

This project owns tests for all reusable Jupyter assemblies plus Agent-to-Jupyter, executable configuration/provider,
self-hosted kernel, real Deno, process, and NativeAOT integration behavior. Agent runtime-only unit tests belong in
`Maieutics.Agent.Tests`.

## Coverage ownership

- Shared: frame layout, signatures, wire names, source-generated DTO round trips, unknown fields, buffers, connection
  validation, cursors, MIME, display IDs, and malformed messages.
- Client transport: socket ownership, five channels, identities, heartbeat, queue failure, startup cancellation,
  disconnect, and concurrent disposal.
- Client protocol: parent/channel/type correlation, reply-idle permutations, stdin parents, ordered and late output,
  concurrent requests, cancellation, and terminal propagation.
- Kernel: busy/reply/idle order, shell serialization, responsive control and heartbeat, interrupt, shutdown, silent,
  stdin, language services, display/update/clear, and exception conversion.
- Product adapter: streaming display/update, partial failure, interrupt, model commands and completion, no transcript
  pollution, multi-cell context, provider switching, and configuration boundary behavior.

## Interoperability and process tests

- Keep self-hosted `JupyterKernelHost` plus `JupyterClient` coverage.
- Keep real Deno interoperability through `TestData/kernels/deno/kernel.json`, resolved from test output, with `deno`
  from `PATH`. Never depend on a user kernelspec path or absolute Deno executable.
- Process tests use external temporary configuration and connection files and must not contact real model services.
- Provider conformance uses deterministic fake HTTP/SSE servers for OpenAI Responses, Chat Completions, and Anthropic
  Messages, including tool continuation and cancellation.
- NativeAOT smoke tests must execute the Microsoft.Extensions.AI function runtime and provider paths in the published
  binary, including a real `AIFunctionFactory` workspace function continuation. Do not suppress new trimming or
  dynamic-code warnings broadly to make them pass.
