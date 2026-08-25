/**
 * Shared Worker factory: the controlled creation seam for Workers that
 * participate in the Maieutics bootstrap contract.
 *
 * Root Workers enter DIRECTLY at their Maieutics-controlled entry module with
 * the caller's construction options preserved:
 *
 *   - `type` is pinned to "module" (Deno's supported Worker kind; classic is a
 *     typed unsupported operation);
 *   - `name` is forwarded unchanged;
 *   - the complete `deno` permission option is forwarded unchanged — the
 *     bootstrap never widens permissions and never encodes secrets in URLs or
 *     messages.
 *
 * The entry module itself installs the shared bootstrap (recursive Worker
 * patch + versioned marker) before its profile initialization — see
 * repl_worker.ts and worker_entry.ts. Routing root workers through a query
 * wrapper would force the worker to dynamically import the entry, which makes
 * Deno re-resolve the entry's bare-specifier graph (jsr:/npm:) inside the
 * worker and therefore requires `import` access for the worker. The direct
 * entry keeps the graph statically analyzable at spawn, so no permission
 * change is introduced.
 *
 * NESTED workers are routed through the shared wrapper by the patch installed
 * in each root entry (see worker_patch.ts).
 */

import { type BootstrapProfile } from "./bootstrap_contract.ts";

/** File URL of the shared wrapper module (worker_bootstrap.ts). The plugin host
 * extends worker read grants to its directory so the wrapper and its imports
 * can load. Exported for grant computation; not part of the public contract. */
export const BOOTSTRAP_WRAPPER_URL = new URL("./worker_bootstrap.ts", import.meta.url).href;

/** Supported Worker construction options that survive creation. */
export interface BootstrapWorkerOptions {
  type?: "module" | "classic";
  name?: string;
  deno?: { permissions?: Deno.PermissionOptions };
}

export interface SpawnBootstrapWorkerOptions extends BootstrapWorkerOptions {
  /**
   * Runtime profile marker. For a direct root entry this is informational:
   * the entry self-identifies its profile when it installs the bootstrap
   * marker. The patch propagates the same profile to nested workers.
   */
  profile: BootstrapProfile;
}

/**
 * Spawns a root Worker for `targetUrl` (a Maieutics-controlled entry module
 * that installs the shared bootstrap itself). Returns the Worker; messages,
 * transfers, `terminate()`, and error observation behave exactly as on a
 * directly-spawned Worker. Throws synchronously for a classic Worker request.
 */
export function spawnBootstrapWorker(
  targetUrl: string | URL,
  options: SpawnBootstrapWorkerOptions,
): Worker {
  if (options.type === "classic") {
    throw new DOMException(
      "Classic workers are not supported by the Maieutics runtime; use a module worker.",
      "NotSupportedError",
    );
  }
  const { profile: _profile, ...workerOptions } = options;
  return new Worker(targetUrl, { ...workerOptions, type: "module" });
}
