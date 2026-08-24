/**
 * REPL process entry: the child module the plugin host derives with
 * `spawnProcess` (ADR 0020 migration skeleton).
 *
 * This is the process-side counterpart of the host's `ReplManager` in
 * `maieutics-plugin-host/`. The process calls `serveProcess` at top level, then
 * handles the host's RPC calls from `process_rpc.ts`. The existing WebSocket
 * REPL path (`main.ts` + `repl_client.ts` + `repl_worker.ts`) is untouched and
 * keeps working during the dual-track transition (ADR 0020 out of scope).
 *
 * The process runs the actor surface with whatever `permissions` shell the host
 * passed at spawn. In the full migration the kernel attaches the permission
 * broker to this pid; the static shell is only the fallback baseline (ADR 0020
 * decision 1), never the security boundary.
 */

import { rpc } from "./process_rpc.ts";

if (import.meta.main) {
  await import("./process_rpc.ts");
}

// `rpc` is imported for its side effect (serveProcess(rpc) runs at module top
// level in process_rpc.ts); keep the reference so `deno check` verifies the
// surface contract compiles.
void rpc;
