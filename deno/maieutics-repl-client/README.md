# Maieutics REPL client

Deno-side client for the Maieutics REPL control channel. The kernel spawns each `deno jupyter` REPL
with `MAIEUTICS_REPL_IPC` set to the control channel's unix domain socket and serves HTTP
(`/health`) and WebSocket (`/ws`) endpoints on it.

The module is the single source of truth for the client; the kernel materializes it per process. At
REPL session start the kernel runs a verified bootstrap cell that binds `globalThis.maieutics`, so
scripts can call `maieutics.health()` with no import; explicit `import` via `MAIEUTICS_REPL_CLIENT`
remains available. Tool and widget APIs are pending design; today the module exposes connection,
health, and the transport primitives.

```ts
const maieutics = await import(Deno.env.get("MAIEUTICS_REPL_CLIENT")!);
console.log(await maieutics.health());
```

Validation: `deno task check` and `deno task test`.
