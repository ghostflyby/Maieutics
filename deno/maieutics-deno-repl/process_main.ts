/**
 * REPL process entry: the child module the plugin host derives with
 * `spawnProcess` (ADR 0020). C1 makes this process a REAL REPL: it serves the
 * host actor surface (`process_rpc.ts`) AND runs the same WebSocket REPL client
 * as the kernel-derived path (`main.ts` + `repl_client.ts` + `repl_worker.ts`)
 * once the host starts it.
 *
 * Lifecycle: `serveProcess(rpc)` runs at module top level and opens the actor
 * channel; the process does NOT auto-start the REPL client. The host reports
 * this pid (`host.repl.spawned`) so the kernel registers the broker policy and
 * session identity (B3), then calls `initialize()` and `startRepl()`, which
 * boots the eval/comm WebSockets against the kernel. When the eval channel
 * completes (kernel dispose / failure / host shutdown) the process exits
 * itself and the host's spawnProcess death detection emits `host.repl.exited`.
 *
 * The existing kernel-derived WebSocket REPL path is untouched and keeps
 * working during the dual-track transition (ADR 0020 out of scope).
 */

import { rpc } from "./process_rpc.ts";

if (import.meta.main) {
  await import("./process_rpc.ts");
}

// `rpc` is imported for its side effect (serveProcess(rpc) runs at module top
// level in process_rpc.ts); keep the reference so `deno check` verifies the
// surface contract compiles.
void rpc;
