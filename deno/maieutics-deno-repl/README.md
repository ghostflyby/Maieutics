# Maieutics Deno REPL

Production Deno execution process for Maieutics. The process connects to the owning kernel over the
process-verified IPC socket at `/v1/repl/eval/ws`, then runs the Aves REPL kernel in a supervised
worker-actor. All wire messages use the versioned `repl.eval.*` protocol from `protocol.ts`.

The main thread owns the WebSocket, bounded inbound/event/outbound queues, pending input requests,
the active execution, and actor shutdown. Worker output is a worker-actor `AsyncIterable`, so main
pulls ordered events with transport-level backpressure and receives the terminal as its last item.
Cancellation is carried as an `AbortSignal` into the worker and Aves. Cooperative Aves disposal
always precedes the worker's hard termination.

The worker imports `MAIEUTICS_REPL_CLIENT`, binds its namespace as `globalThis.maieutics`, and
completes a health probe before accepting execution. This preserves tools, events, comms, and plugin
hooks inside evaluated cells.

## Environment

- `MAIEUTICS_REPL_IPC`: process-owned Unix-domain socket path on Unix, or the existing Kestrel
  `host:port` on Windows.
- `MAIEUTICS_REPL_SESSION`: owning kernel session id.
- `MAIEUTICS_REPL_GENERATION`: non-negative actor generation.
- `MAIEUTICS_REPL_CLIENT`: module URL for the process-bound Maieutics client namespace.
- `MAIEUTICS_REPL_PIPE`: Windows-only named pipe used once to obtain the process-verified session
  credential.
- `SystemRoot`: Windows-only system directory used to resolve the exact `kernel32.dll` FFI target.

## Permissions

The kernel lazily installs the module graph (`deno cache` with the pinned lockfile, no `--allow-*`
flags needed: Deno loads and downloads the initial module graph without consulting the permission
system) on the first REPL start that finds it missing, then locates `<esbuild-wasm-file>` with
`deno eval "import.meta.resolve('npm:esbuild-wasm/esbuild.wasm')"` — the same resolution Aves uses
at runtime. A warm cache needs no runtime downloads; the child does not run with `--cached-only`
because Aves resolves its `esbuild-wasm` npm subpath from registry metadata that the offline cache
does not retain.

Each read entry has one purpose:

- `<module-root>` contains `maieutics-deno-repl` and the materialized `MAIEUTICS_REPL_CLIENT`.
- `<esbuild-wasm-file>` is the single cached `esbuild-wasm@0.25.12/esbuild.wasm` file that Aves
  reads with `Deno.readFile` during REPL initialization; the rest of the cache is not granted.
- `<workspace>` is the only user-file root available to evaluated cells and
  `Deno.jupyter.image(path)`.
- `<absolute-socket-path>` is Unix-only and is granted because `Deno.createHttpClient` checks both
  filesystem read and write permissions for the UDS proxy.

Unix launch permissions:

```text
deno run --no-prompt \
  --config=<module-root>/maieutics-deno-repl/deno.json \
  --allow-env=MAIEUTICS_REPL_IPC,MAIEUTICS_REPL_SESSION,MAIEUTICS_REPL_GENERATION,MAIEUTICS_REPL_CLIENT \
  --allow-net=unix:<absolute-socket-path>,localhost:80 \
  --allow-read=<module-root>,<esbuild-wasm-file>,<workspace>,<absolute-socket-path> \
  --allow-write=<absolute-socket-path> \
  <module-root>/maieutics-deno-repl/main.ts
```

`Deno.createHttpClient` requires the socket path in both read and write allowlists. The native
`WebSocket` connects through the UDS proxy but still resolves its synthetic `ws://localhost` URL
authority, so `localhost:80` must be granted alongside the socket. No other TCP endpoint is granted
on Unix. The same socket and allowlist serve the dedicated comm WebSocket (`/comm`, see
`docs/deno-jupyter-compat.md`); no additional permission is needed.

Windows launch permissions:

```text
deno run --no-prompt \
  --config=<module-root>\\maieutics-deno-repl\\deno.json \
  --allow-env=MAIEUTICS_REPL_IPC,MAIEUTICS_REPL_SESSION,MAIEUTICS_REPL_GENERATION,MAIEUTICS_REPL_CLIENT,MAIEUTICS_REPL_PIPE,SystemRoot \
  --allow-net=127.0.0.1:<kestrel-port> \
  --allow-read=<module-root>,<esbuild-wasm-file>,<workspace> \
  --allow-ffi=<SystemRoot>\\System32\\kernel32.dll \
  <module-root>\\maieutics-deno-repl\\main.ts
```

Windows reuses the existing Kestrel listener and grants only its concrete loopback port. FFI is
limited to the absolute `kernel32.dll` path used for process-verified named-pipe bootstrap.
