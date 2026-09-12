# Maieutics Documentation

The product frontend is the custom web protocol consumed by the VSCode notebook extension (ADR 0023). The reusable
Jupyter libraries are retained as standalone libraries with no executable consumer.

## Frontend

- [Web frontend protocol](web-frontend-protocol.md) — the primary frontend: discovery, REST, and event streams
- [ADR 0023: Custom web frontend protocol and the VSCode notebook frontend](architecture/decisions/0023-custom-web-frontend-protocol-and-vscode-notebook.md)
- [Frontend migration gaps](frontend-migration-gaps.md) — remaining gaps from the Jupyter frontend, with priorities and acceptance criteria

## Operations

- [Runtime configuration](configuration.md)
- [Logging](logging.md) — level policy, categories, and the opt-in file sink

## Architecture

- [Agent platform architecture](architecture/README.md)
- [ADR 0001: Provider-neutral model boundary](architecture/decisions/0001-provider-neutral-model-boundary.md)
- [ADR 0002: Agent sessions, runs, and content](architecture/decisions/0002-agent-session-run-content-model.md)
- [ADR 0003: Deno Jupyter REPL and notebook output bridge](architecture/decisions/0003-deno-jupyter-repl-output-bridge.md) (historical — output bridge superseded by ADR 0023)
- [ADR 0004: Out-of-process Deno extensions and lifecycle hooks](architecture/decisions/0004-deno-extension-protocol.md)
- [ADR 0005: Distributed execution control and worker planes](architecture/decisions/0005-distributed-execution.md)
- [ADR 0006: Selective Microsoft Agent Framework adoption (superseded)](architecture/decisions/0006-selective-microsoft-agent-framework-adoption.md)
- [ADR 0007: Runtime configuration location and hot reload](architecture/decisions/0007-runtime-configuration-and-hot-reload.md)
- [ADR 0008: Model profile catalog and session selection](architecture/decisions/0008-model-profile-catalog-and-session-selection.md)
- [ADR 0009: Volatile transcript and durable storage shape](architecture/decisions/0009-volatile-transcript-and-durable-storage-shape.md)
- [ADR 0010: Direct Microsoft.Extensions.AI function runtime](architecture/decisions/0010-direct-microsoft-extensions-ai-function-runtime.md)
- [ADR 0011: Deno REPL tools, lifecycle, and output routing](architecture/decisions/0011-deno-repl-tools-lifecycle-and-output-routing.md)
- [ADR 0012: Flat notebook command syntax and slash completion](architecture/decisions/0012-flat-notebook-command-syntax-and-slash-completion.md)
- [ADR 0013: Separate MCP configuration file](architecture/decisions/0013-mcp-configuration-file.md)
- [ADR 0014: Deno REPL sideband IPC and HTTP control channel](architecture/decisions/0014-deno-repl-ipc-and-http-control.md)
- [ADR 0015: Turn budgets and truncated turn commits](architecture/decisions/0015-turn-budget-and-truncation.md)
- [ADR 0016: Out-of-process script plugins and symbol-identified extension points](architecture/decisions/0016-script-plugins-and-extension-points.md)
- [ADR 0017: Terminal tool protocol and session lifecycle](architecture/decisions/0017-terminal-tool-protocol-and-session-lifecycle.md)
- [ADR 0018: Declarative permission store, variable interpolation, and the internal Deno execution module](architecture/decisions/0018-declarative-permission-store-and-deno-execution-module.md)
- [ADR 0020: Deno REPL under the extension host — process ownership, permissions, and call direction](architecture/decisions/0020-repl-extension-host-actor-boundary.md)
- [ADR 0021: Plugin HTTP UI — host-mounted zero-permission fetch handlers](architecture/decisions/0021-plugin-http-ui-host-mounted-zero-permission-handlers.md)
- [ADR 0022: Plugin web storage and platform application directories](architecture/decisions/0022-plugin-web-storage-and-application-directories.md)
- [ADR 0024: Frontend comm plane for interactive widgets](architecture/decisions/0024-frontend-comm-plane-and-widgets.md)
- ADR 0023 is listed under Frontend above.

## Historical

- [Deno.jupyter compatibility boundary](deno-jupyter-compat.md) — still describes the REPL API surface; the output path it describes is superseded by ADR 0023
- [Deno.jupyter compatibility completion plan](deno-jupyter-compat-plan.md) — one-time execution plan, kept as a record
