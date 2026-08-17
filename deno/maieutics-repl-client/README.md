# Maieutics REPL client

Deno-side client for the Maieutics REPL control channel. The kernel spawns each `deno jupyter` REPL
with `MAIEUTICS_REPL_IPC` set to the control channel's unix domain socket and serves HTTP
(`/health`) and WebSocket (`/ws`) endpoints on it.

The module is the single source of truth for the client; the kernel materializes it per process. At
REPL session start the kernel runs a verified bootstrap cell that binds `globalThis.maieutics`, so
scripts can call `maieutics.health()` with no import; explicit `import` via `MAIEUTICS_REPL_CLIENT`
remains available. The module namespace is the default client: `health`, `tools`, `events` (an
`EventTarget` for bus messages), and `comm`. `connect()` returns an independent client with the same
shape.

```ts
const maieutics = await import(Deno.env.get("MAIEUTICS_REPL_CLIENT")!);
console.log(await maieutics.health());
maieutics.events.addEventListener("comm.msg", (event) => {
  console.log(event.detail);
});
```

Validation: `deno task check` and `deno task test`.

## Deno permissions

Use explicit allowlists when the module runs standalone:

- Unix: `--allow-env=MAIEUTICS_REPL_IPC,MAIEUTICS_REPL_CLIENT,MAIEUTICS_REPL_SESSION`,
  `--allow-net=unix:<absolute-socket-path>,localhost:80`, and the same socket path in both
  `--allow-read` and `--allow-write`. The read/write pair is required by `Deno.createHttpClient`
  (verified empirically); `localhost:80` is the synthetic URL authority the native `WebSocket`
  resolves even though the connection travels over the UDS proxy. No other TCP host is granted.
- Windows:
  `--allow-env=MAIEUTICS_REPL_IPC,MAIEUTICS_REPL_CLIENT,MAIEUTICS_REPL_SESSION,MAIEUTICS_REPL_PIPE,SystemRoot`,
  `--allow-net=127.0.0.1:<kestrel-port>`, and `--allow-ffi=<SystemRoot>\\System32\\kernel32.dll`.
  The bootstrap does not load any other native library and reuses the existing Kestrel listener.

Any change that alters environment, network, or filesystem behavior must update this list in the
same change, so the future permission system can derive the required grant set instead of guessing.
