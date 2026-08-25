# Maieutics Deno REPL

Production Deno execution process for Maieutics. The process connects to the owning kernel over the
process-verified IPC socket at `/v1/repl/eval/ws`, then runs the Aves REPL kernel in a supervised
worker-actor. All wire messages use the versioned `repl.eval.*` protocol from `protocol.ts`.

There are two process entries that boot the same WebSocket REPL client:

- `main.ts` — the kernel-derived entry: the kernel spawns the child directly and the entry parses
  the `MAIEUTICS_REPL_*` environment, bootstraps the Windows credential, and runs the `ReplClient`.
- `process_main.ts` — the host-derived entry (ADR 0020): the plugin host derives the child via
  worker-actor `spawnProcess`. It serves the host actor surface (`process_rpc.ts`) and, once the
  host calls `startRepl()` (after the pid report registered the broker policy), runs the SAME
  `ReplClient` against the kernel eval/comm channels. Real execution flows over the eval WebSocket
  in both paths; the actor surface is control/query only.

The shared `repl_process_env.ts` parses the env contract and bootstraps the Windows credential for
both entries. In the host-derived path the kernel ships the complete child environment through the
`host.repl.derive` payload, so `process_main.ts` reads the exact same `MAIEUTICS_REPL_*` variables
and, on Windows, `MAIEUTICS_REPL_PIPE` that the kernel-derived entry reads.

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
- `DENO_PERMISSION_BROKER_PATH`: broker address when the kernel runs with a permission broker. The
  kernel-derived path injects it at launch; the host-derived path does NOT carry it in the derive
  payload — the plugin host appends its own forwarded `MAIEUTICS_PERMISSION_BROKER` address to the
  child env as `DENO_PERMISSION_BROKER_PATH` when deriving the REPL (the host itself never consults
  the broker). With the broker set it is the single authority for every explicit permission check
  the child makes; without it the child runs against its static permission shell only.

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
