/**
 * Shared Maieutics bootstrap contract for Deno Workers.
 *
 * This module is the product-owned runtime composition seam described in
 * docs/runtime-bootstrapping-design.md. It is deliberately independent of both
 * the REPL runtime (maieutics-deno-repl) and the plugin host runtime
 * (maieutics-plugin-host): each profile composes its own capabilities AFTER
 * the shared bootstrap, and the shared bootstrap never injects profile
 * globals.
 *
 * The contract has one idea: a Worker that must participate in Maieutics
 * runtime initialization enters through the shared wrapper entry instead of
 * the target module directly. The wrapper reads the bootstrap metadata (the
 * target module URL and non-sensitive version/profile markers) from its own
 * module URL query string — a controlled internal descriptor that never
 * carries credentials, connection-file contents, or any other sensitive
 * value.
 *
 * This module is not part of the reusable Jupyter assemblies and not part of
 * the public plugin-author SDK.
 *
 * Profile space: this contract runs only inside Deno execution contexts, which
 * produce exactly two profiles ("repl" and "plugin"). The standalone Node-side
 * mirror (deno/maieutics-node-runtime/node_bootstrap_contract.cjs) keeps its
 * own independent "node" profile and is deliberately not part of
 * `BootstrapProfile`. Both runtimes share the marker symbol
 * (Symbol.for("maieutics/bootstrap/v1")) and the wrapper-URL query keys for
 * parity, but a marker is never read across runtimes: each runtime validates
 * markers against its own profile whitelist.
 */

/** Version of the shared bootstrap contract. Bumped when the wrapper or the
 * factory semantics change; never a process/worker secret. */
export const BOOTSTRAP_VERSION = 1;

/** Query-string key that carries the target module URL on the wrapper URL. */
export const TARGET_QUERY_KEY = "maieuticsTarget";

/** Query-string key that carries the bootstrap version marker. */
export const VERSION_QUERY_KEY = "maieuticsVersion";

/** Query-string key that carries the runtime profile marker. */
export const PROFILE_QUERY_KEY = "maieuticsProfile";

/** A supported Deno runtime profile. "repl" marks the REPL user worker;
 * "plugin" marks a plugin worker. Deno execution contexts only ever produce
 * these two profiles; the Node adapter (deno/maieutics-node-runtime/) uses its
 * own independent "node" profile, which is deliberately NOT part of this
 * union. Both runtimes share the marker symbol and the wrapper-URL query keys,
 * but a marker is never read across runtimes: each runtime validates markers
 * against its own profile whitelist. The marker is non-sensitive and only used
 * for diagnostics and tests. */
export type BootstrapProfile = "repl" | "plugin";

/**
 * Bootstrap metadata parsed by the shared wrapper from its own module URL.
 * The target is a file/HTTP(S) URL; every other field is a non-sensitive
 * version/profile marker.
 */
export interface BootstrapMetadata {
  readonly targetUrl: string;
  readonly version: number;
  readonly profile: BootstrapProfile;
}

/**
 * Reads the bootstrap metadata from a wrapper module URL. Returns null when
 * the URL does not carry the contract query keys (the module is being used
 * outside the shared wrapper, e.g. imported directly by a test).
 */
export function readBootstrapMetadata(url: URL | string): BootstrapMetadata | null {
  const parsed = new URL(String(url));
  const targetUrl = parsed.searchParams.get(TARGET_QUERY_KEY);
  if (targetUrl === null || targetUrl.length === 0) return null;
  const version = Number(parsed.searchParams.get(VERSION_QUERY_KEY));
  const profile = parsed.searchParams.get(PROFILE_QUERY_KEY);
  const validProfile = profile === "repl" || profile === "plugin";
  if (!Number.isInteger(version) || !validProfile) return null;
  return { targetUrl, version, profile };
}

/**
 * Builds the shared wrapper module URL for a target. Only the target URL and
 * non-sensitive markers are encoded; the caller must never place credentials
 * in `targetUrl` or in the query.
 */
export function buildWrapperUrl(
  wrapperModuleUrl: URL | string,
  targetUrl: URL | string,
  profile: BootstrapProfile,
  version: number = BOOTSTRAP_VERSION,
): URL {
  const wrapper = new URL(String(wrapperModuleUrl));
  wrapper.searchParams.set(TARGET_QUERY_KEY, String(targetUrl));
  wrapper.searchParams.set(VERSION_QUERY_KEY, String(version));
  wrapper.searchParams.set(PROFILE_QUERY_KEY, profile);
  return wrapper;
}

/**
 * Global-symbol marker that identifies a realm in which the shared bootstrap
 * has run. The marker is the only non-profile global the shared bootstrap
 * installs; it is non-enumerable through normal property access and carries
 * only non-sensitive version/profile data (design: "non-sensitive runtime
 * profile/version markers").
 */
export const BOOTSTRAP_MARKER = Symbol.for("maieutics/bootstrap/v1");

/** The non-sensitive marker value installed by the shared wrapper. */
export interface BootstrapMarkerValue {
  readonly version: number;
  readonly profile: BootstrapProfile;
}

/**
 * Installs the bootstrap marker on the current realm's global scope. Called by
 * the shared wrapper after installing the Worker patch and before importing the
 * target module; idempotent.
 */
export function installBootstrapMarker(marker: BootstrapMarkerValue): void {
  const globals = globalThis as unknown as Record<PropertyKey, unknown>;
  if (globals[BOOTSTRAP_MARKER] !== undefined) return;
  globals[BOOTSTRAP_MARKER] = Object.freeze({ ...marker });
}

/**
 * Reads the bootstrap marker from the current realm's global scope. Returns
 * null when the shared bootstrap has not run in this realm (the module was not
 * entered through the shared wrapper).
 *
 * Validation matches the Node mirror (node_bootstrap_contract.cjs): the value
 * must be a non-null object, the version must be an integer (Number.isInteger;
 * the only writer emits the positive BOOTSTRAP_VERSION constant), and the
 * profile must be in this runtime's own whitelist. The whitelists differ by
 * design ("repl" | "plugin" here, "repl" | "node" on the Node side); markers
 * never cross runtimes.
 */
export function readBootstrapMarker(): BootstrapMarkerValue | null {
  const value = (globalThis as unknown as Record<PropertyKey, unknown>)[BOOTSTRAP_MARKER];
  if (typeof value !== "object" || value === null) return null;
  const candidate = value as Partial<BootstrapMarkerValue>;
  const profile = candidate.profile;
  if (!Number.isInteger(candidate.version) || (profile !== "repl" && profile !== "plugin")) {
    return null;
  }
  return { version: candidate.version as number, profile };
}
