/**
 * Recursive Deno Worker routing patch.
 *
 * Installed inside a Worker (root or nested) BEFORE the target module is
 * imported:
 *
 *   - the shared wrapper (worker_bootstrap.ts) installs it for NESTED workers;
 *   - the Maieutics-controlled root entries (repl_worker.ts, worker_entry.ts)
 *     install it themselves before their profile initialization.
 *
 * The patch captures the realm's native `Worker` constructor and replaces the
 * product-visible construction path with a factory that routes every module
 * Worker through the shared wrapper:
 *
 * ```text
 * shared Worker wrapper
 *     -> install shared Worker patch (this module)
 *     -> resolve target module
 *     -> import target module
 * ```
 *
 * The patch:
 *   - preserves the caller's `type` (pinned to "module" — Deno is module-only),
 *     `name`, and the complete `deno` permission option unchanged;
 *   - never widens permissions, never changes a module Worker into a classic
 *     one, and never encodes secrets in URLs or messages;
 *   - resolves relative Worker specifiers against the CALLER's module file
 *     (V8 stack-scan) instead of the wrapper entry, so nested workers created
 *     by the target module resolve exactly as they would without the wrapper;
 *   - uses the native constructor exactly once per controlled creation and
 *     never recursively wraps itself.
 *
 * A request for a classic Worker is a typed unsupported operation, even though
 * current Deno rejects that form itself.
 *
 * Node `node:worker_threads` support (design decision 3) lives in
 * deno/maieutics-node-runtime/ (standalone CommonJS/ESM files that share the
 * marker and query-key constants from bootstrap_contract.ts). This module
 * never imports Node modules; the Node adapter never imports this module.
 * Root workers enter DIRECTLY at their Maieutics-controlled entry (not through
 * this wrapper): a wrapper entry would import the target dynamically, which
 * forces Deno to re-resolve the target's bare-specifier graph (jsr:/npm:)
 * inside the worker and therefore requires `import` access for the worker — a
 * permission the plugin worker's narrowed scope does not have. A direct entry
 * keeps the target graph statically analyzable at spawn, matching the current
 * plugin/REPL worker behavior exactly. Nested workers (arbitrary user code)
 * are routed through the wrapper; a nested bare-specifier graph already
 * required `import` access before this patch, so routing does not add a
 * permission requirement.
 */

import { BOOTSTRAP_VERSION, type BootstrapProfile, buildWrapperUrl } from "./bootstrap_contract.ts";

/** The shared wrapper module is always this module's sibling. */
const WRAPPER_URL = new URL("./worker_bootstrap.ts", import.meta.url);

/** Shared patch state for the current realm. */
interface PatchContext {
  readonly profile: BootstrapProfile;
  readonly wrapperUrl: URL;
}

let context: PatchContext | null = null;
let nativeWorker: typeof Worker | null = null;
let installed = false;

/** Path of THIS module (no query), used to skip patch frames in the stack scan. */
const patchPath = new URL(import.meta.url).pathname;

/**
 * Installs the recursive Worker patch on the current realm's `globalThis`.
 * Idempotent: only the first call in a realm installs. Called by the shared
 * wrapper (nested workers) and by the Maieutics-controlled root entries before
 * their profile initialization.
 */
export function installWorkerPatch(profile: BootstrapProfile): void {
  if (installed) return;
  installed = true;
  context = { profile, wrapperUrl: WRAPPER_URL };
  const native = globalThis.Worker;
  if (typeof native !== "function") return;
  nativeWorker = native;
  const routed = new Proxy(native, {
    construct(_target, args: unknown[]): Worker {
      return constructWorker(args[0] as string | URL, args[1] as WorkerOptions | undefined);
    },
    get(target, prop, receiver): unknown {
      // Keep `worker instanceof Worker` truthful after the replacement.
      if (prop === Symbol.hasInstance) {
        return (value: unknown): boolean => value instanceof native;
      }
      return Reflect.get(target, prop, receiver);
    },
  });
  (globalThis as { Worker: typeof Worker }).Worker = routed;
}

/**
 * Resolves a Worker specifier against a caller module base. Exported for unit
 * tests; not part of the public contract.
 */
export function resolveWorkerSpecifier(
  specifier: string | URL,
  base: string | undefined,
): string {
  if (specifier instanceof URL) return specifier.href;
  const raw = String(specifier);
  if (base !== undefined) {
    try {
      return new URL(raw, base).href;
    } catch {
      // Fall through to the wrapper-relative fallback.
    }
  }
  try {
    return new URL(raw, import.meta.url).href;
  } catch {
    throw new TypeError(`Cannot resolve the Worker specifier '${raw}'.`);
  }
}

function constructWorker(specifier: string | URL, options: WorkerOptions | undefined): Worker {
  if (options?.type === "classic") {
    throw new DOMException(
      "Classic workers are not supported by the Maieutics runtime; use a module worker.",
      "NotSupportedError",
    );
  }
  const current = requireContext();
  const resolved = resolveWorkerSpecifier(specifier, callerModuleBase());
  const wrapperUrl = buildWrapperUrl(
    current.wrapperUrl,
    resolved,
    current.profile,
    BOOTSTRAP_VERSION,
  );
  const forwarded = { ...(options ?? {}), type: "module" as const };
  const ctor = nativeWorker ?? globalThis.Worker;
  return new (ctor as new (url: URL, options?: WorkerOptions) => Worker)(wrapperUrl, forwarded);
}

function requireContext(): PatchContext {
  if (context === null) {
    throw new Error(
      "The Maieutics Worker patch is not installed in this realm; " +
        "enter the worker through the shared bootstrap wrapper.",
    );
  }
  return context;
}

/**
 * Extracts the caller module's file URL from a single V8 stack frame line,
 * stripping the trailing `:line:column` V8 appends to file URLs. A Windows
 * drive letter contains a colon too, so the numeric suffix is removed after
 * the match instead of excluding colons from the matched characters (which
 * would truncate `file:///C:/...` to `file:///C`). Returns undefined for
 * frames without a file URL. Exported for unit tests; not part of the public
 * contract.
 */
export function fileUrlFromStackLine(line: string): string | undefined {
  const match = line.match(/\(?(file:\/\/[^)\s]+)/);
  const file = match?.[1];
  if (file === undefined) return undefined;
  return file.replace(/:\d+(?::\d+)?$/, "");
}

/**
 * Best-effort discovery of the caller's module file URL from the V8 stack.
 * Returns the first stack frame that names a `file:` URL and is not part of
 * this patch module. REPL user cells evaluate through an AsyncFunction whose
 * frames carry no `file:` module URL (probed with Aves `new AF(...)`), so such
 * calls fall back to the wrapper-relative resolution instead.
 */
function callerModuleBase(): string | undefined {
  const stack = new Error().stack ?? "";
  for (const line of stack.split("\n")) {
    if (line.includes(" at eval ")) return undefined;
    const file = fileUrlFromStackLine(line);
    if (file === undefined) continue;
    if (!isPatchFrame(file)) return file;
  }
  return undefined;
}

function isPatchFrame(fileUrl: string): boolean {
  try {
    return new URL(fileUrl).pathname === patchPath;
  } catch {
    return false;
  }
}
