# ADR 0013: Separate MCP Configuration File

Status: Accepted

Date: 2026-08-03

## Context

MCP servers were configured inside the active `maieutics.json` under `Maieutics.Mcp.Servers`. That schema was
Maieutics-specific (PascalCase keys, a per-server `Enabled` flag, and a mandatory non-empty `Tools` allowlist with
rename mapping), so it could not be reused across Claude Code, Cursor, VS Code, or JetBrains clients. Users who already
maintain an `mcp.json` had to hand-translate server blocks, and the allowlist requirement made copied blocks invalid.

## Decision

MCP server configuration moves out of `maieutics.json` into an optional `mcp.json` beside the active `maieutics.json`.
The file follows the conventional lowercase format shared by Claude Code and Cursor:

- The top-level key is `mcpServers` (Claude Code/Cursor) or `servers` (VS Code); combining both is rejected.
- Stdio servers use `command`, `args`, and `env`; HTTP servers use `type: "http"`, `url`, and `headers`.
- `type` defaults to stdio; an explicit `url` without `type` implies HTTP; `type: "sse"` fails explicitly.
- `enabled` defaults to true so copied blocks work immediately.
- Maieutics extensions: `workingDirectory`, `initializationTimeout`, `requestTimeout`, `shutdownTimeout`, and
  `connectionTimeout`.

The embedded `Maieutics.Mcp` section and the `Tools` allowlist/rename mapping are removed. Every tool discovered from a
connected server is exposed to the model under its remote name. A tool whose name collides with a built-in tool is
hidden and reported unavailable instead of failing the server.

`mcp.json` participates in the same polling reload as `maieutics.json`: a change to either file triggers one atomic
candidate rebuild, and an invalid update retains the last-known-good snapshot. A syntactically invalid `mcp.json` that
exists at startup fails startup like an invalid `maieutics.json`.

## Consequences

- Existing server blocks can be copied directly from common MCP clients.
- Model-visible tool names now equal remote names; rename support is removed during the prototype phase.
- The removed embedded section and allowlist are breaking changes accepted during prototyping.
- Exposing all discovered tools widens the model-visible surface; operators must trust the servers they configure.
- `Configuration/AGENTS.md` discovery and reload rules now cover a second file.
