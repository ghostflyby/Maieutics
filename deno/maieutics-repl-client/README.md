# Maieutics REPL client

Deno-side client for the Maieutics REPL control channel. The kernel spawns each
`deno jupyter` REPL with `MAIEUTICS_REPL_IPC` set to the control channel's unix domain socket and serves HTTP
(`/health`) and WebSocket (`/ws`) endpoints on it.

The module is the single source of truth for the client; the kernel materializes it per process. At REPL session start
the kernel runs a verified bootstrap cell that binds `globalThis.maieutics`, so scripts can call `maieutics.health()`
with no import; explicit `import` via `MAIEUTICS_REPL_CLIENT` remains available. The module namespace is the default
client: `health`, `tools`, `events` (an
`EventTarget` for bus messages), and `comm`. `connect()` returns an independent client with the same shape.

```ts
const maieutics = await import(Deno.env.get("MAIEUTICS_REPL_CLIENT")!);
console.log(await maieutics.health());
maieutics.events.addEventListener("comm.msg", (event) => {
  console.log(event.detail);
});
```

Validation: `deno task check` and `deno task test`.

## Deno permissions

Inside the `deno jupyter` kernel every permission is granted. When the module is used standalone or in tests, it
requires:

- `--allow-env` — reads `MAIEUTICS_REPL_IPC`, `MAIEUTICS_REPL_CLIENT`, and
  `MAIEUTICS_REPL_SESSION`.
- `--allow-net` — HTTP (`fetch`) and WebSocket connections through the unix socket proxy.
- `--allow-read` and `--allow-write` — `Deno.createHttpClient` checks both read and write access to the unix socket path
  (verified empirically; without
  `--allow-write` the proxy fails at creation).

Any change that alters environment, network, or filesystem behavior must update this list in the same change, so the
future permission system can derive the required grant set instead of guessing.
