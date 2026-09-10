# Maieutics.Jupyter.Tests instructions

Use `.agents/skills/maieutics-dotnet-testing/SKILL.md` for shared xUnit, assertion, deadline, socket, process, and
verification rules. Use `.agents/skills/maieutics-jupyter-protocol/SKILL.md` for cross-layer protocol expectations.

## Ownership

This project owns tests for the retained reusable Jupyter assemblies only (`Maieutics.Jupyter.Shared`,
`Maieutics.Jupyter.Client`, `Maieutics.Jupyter.Kernel`). It references no Agent or executable project. Tests for the
executable, its provider/configuration/permission namespaces, and Agent-to-product behavior belong in
`Maieutics.Product.Tests`; Agent runtime-only unit tests belong in `Maieutics.Agent.Tests`.

## Coverage ownership

- Shared: frame layout, signatures, wire names, source-generated DTO round trips, unknown fields, buffers, connection
  validation, cursors, MIME, display IDs, and malformed messages.
- Client transport: socket ownership, five channels, identities, heartbeat, queue failure, startup cancellation,
  disconnect, and concurrent disposal.
- Client protocol: parent/channel/type correlation, reply-idle permutations, stdin parents, ordered and late output,
  concurrent requests, cancellation, and terminal propagation.
- Kernel: busy/reply/idle order, shell serialization, responsive control and heartbeat, interrupt, shutdown, silent,
  stdin, language services, display/update/clear, and exception completion.
- Interoperability: keep self-hosted `JupyterKernelHost` plus `JupyterClient` coverage, and real Deno
  interoperability through unit and transport coverage, with `deno` from `PATH`. Never depend on a user kernelspec
  path or absolute Deno executable. Process tests use external temporary configuration and connection files and must
  not contact real model services.
