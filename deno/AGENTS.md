# Deno workspace instructions

## Domains

- `maieutics-repl-client/` — Deno-side client for the REPL control channel. It is the single source
  of truth for the client; the kernel embeds and materializes it per process and binds it as the
  `maieutics` namespace. Domains: unix socket HTTP/WS transport, the versioned message bus
  (`control.*` / `comm.*` / `event.*` / `tool.*`), script tool invocation, comm channels, and
  AbortSignal-based cancellation.
- `maieutics-plugin-sdk/` — SDK for writing Maieutics script plugins. A plugin is a standard Deno
  package whose deno.json carries a `maieutics` marker; extension points are identified by versioned
  `Symbol.for` markers (`maieutics/extensionPoint/v1/...`), and implementations are objects with a
  `handler` method or callable functions. Domains: extension point markers, per-extension-point
  context and result types, `defineExtensionPoint`, discovery descriptors.
- `maieutics-plugin-host/` — Out-of-process plugin host. The kernel spawns this process with a
  plugin configuration file and control channel address; it creates one permission-scoped worker per
  plugin export subpath, scans workers for extension points, and bridges `extension.*` bus messages
  between the kernel and the workers. Domains: worker lifecycle, permission grant mapping (positive
  grants only), postMessage invoke protocol, crash/restart policy.
- `shared/` — Control channel wire contract used by `maieutics-repl-client` and
  `maieutics-plugin-host` only. `protocol.ts` carries the versioned envelope and version constant;
  `bus.ts` opens the `/ws` connection, sends the hello handshake, and dispatches envelopes. The
  plugin SDK must stay self-contained and must not import `shared/`.
- Future Deno modules (for example out-of-process extensions and hooks from ADR 0004) live here as
  separate submodules with their own `deno.json`.

## API and TypeScript conventions

- Prefer Web platform standards first for both the surface exposed to scripts and internal
  implementations: `fetch`, `WebSocket`, `EventTarget`/`CustomEvent`,
  `AbortController`/`AbortSignal`, `Uint8Array` built-ins. Use Deno-specific APIs (`Deno.env`,
  `Deno.createHttpClient`) only where the standard has no equivalent, and node-compat
  (`node:module`, etc.) only as a last-resort fallback.
- Named exports are the public contract because scripts interact through the bound namespace; keep
  them stable and documented. `connect()` returns the same client shape for standalone use.
- The bus protocol is versioned with `correlationId`; unknown envelope fields are tolerated;
  failures are typed. Comm payloads use the channel's own vocabulary — Jupyter wire mapping is a
  kernel-side frontend-bridge concern, not a bus property.
- Keep modules self-contained and offline: no runtime package downloads, no external dependencies
  without review. The module is embedded in the kernel, so versions are lockstep and no capability
  negotiation exists.
- Any change that alters environment, network, or filesystem behavior must update the affected
  module README's Deno permissions section in the same change, so the future permission system can
  derive the required grant set instead of guessing.

## Kernel contract

The kernel injects `MAIEUTICS_REPL_IPC` (socket address), `MAIEUTICS_REPL_CLIENT` (module URL), and
`MAIEUTICS_REPL_SESSION` (session id). The bootstrap binds `globalThis.maieutics` to the module
namespace; explicit `import` via `MAIEUTICS_REPL_CLIENT` must keep working.

## Embedding in the .NET host

The `Maieutics` executable embeds the Deno module graph as resources and materializes it into a
per-process temporary directory before launching the REPL child
(`Maieutics/DenoRepl/DenoReplModule.cs` writes each embedded resource to the materialized root). A
Deno-side file that is imported by another embedded file but is not itself embedded fails at child
startup with `Module not found`, breaking every real-Deno integration test.

When adding, renaming, or removing a `.ts` file under this workspace:

1. Update `Maieutics/Maieutics.csproj` `EmbeddedResource` entries (one
   `<EmbeddedResource Include=... LogicalName="Maieutics.Deno....">` per file).
2. Update the `DenoReplModule.Entries` table in `Maieutics/DenoRepl/DenoReplModule.cs` (the logical
   name maps to the materialized relative path).
3. Keep the two lists in sync with each other and with the module's own `deno.json` imports.

## Validation

Each submodule runs `deno task check`, `deno fmt`, and `deno task test`. Run `deno fmt` before
submitting: the workspace enforces formatting (CI runs `deno fmt --check`), so an unformatted file
fails review. A change that adds a Deno-side file also requires the embedding steps above before the
.NET integration tests can pass.
