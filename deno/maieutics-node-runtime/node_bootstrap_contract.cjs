// Node-side mirror of the shared Maieutics bootstrap contract
// (deno/maieutics-runtime/bootstrap_contract.ts).
//
// The Deno module lives under deno/maieutics-runtime/ and is typed/checked by
// the Deno workspace; this Node adapter is deliberately STANDALONE CommonJS so
// a plain `node --require` preload can install it. It shares the exact marker
// symbol and the exact wrapper-URL query keys with the Deno module so both
// runtimes' descriptors stay mutually parseable. A marker is never read across
// runtimes: each runtime recognizes only its own profile space. It must never
// import a Deno module and must never be imported by Deno code.
//
// Node strips the query string from import.meta.url inside a worker thread, so
// the wrapper must receive the target descriptor through `workerData` instead
// of a wrapper URL query. The wrapper URL therefore carries only the
// non-sensitive version/profile markers (kept for parity with the Deno
// descriptor and for diagnostics); the target URL travels as workerData.
//
// The adapter-added descriptor contains only the target and non-sensitive
// bootstrap markers. Caller-provided workerData is forwarded as caller-owned
// data and may itself contain arbitrary values.
//
// Profile space: this Node adapter has its own independent "node" profile,
// which exists only here. The Deno contract's BootstrapProfile union
// (deno/maieutics-runtime/bootstrap_contract.ts) covers exactly the two Deno
// execution contexts ("repl" and "plugin") and intentionally does not include
// "node". The shared marker symbol and query keys give the two runtimes
// parity, not cross-runtime reads: readBootstrapMarker accepts only this
// runtime's whitelist ("repl" | "node" here, "repl" | "plugin" in Deno).

"use strict";

/** Mirrors bootstrap_contract.ts: version of the shared bootstrap contract. */
const BOOTSTRAP_VERSION = 1;

/** Mirrors bootstrap_contract.ts: wrapper-URL query key for the profile. */
const PROFILE_QUERY_KEY = "maieuticsProfile";

/** Mirrors bootstrap_contract.ts: global-symbol bootstrap marker. */
const BOOTSTRAP_MARKER = Symbol.for("maieutics/bootstrap/v1");

/**
 * Reads the bootstrap marker from the current realm's global scope. Returns
 * null when the shared bootstrap has not run in this realm. The marker value
 * is an object with a non-sensitive version and profile; installed by the
 * wrapper before the target module is imported.
 *
 * Validation is consistent with the Deno mirror (bootstrap_contract.ts): the
 * value must be a non-null object, the version must be an integer
 * (Number.isInteger), and the profile must be in this runtime's own whitelist
 * ("repl" | "node" here, "repl" | "plugin" in Deno). Markers never cross
 * runtimes, so the whitelists differing is by design.
 */
function readBootstrapMarker() {
  const globals = globalThis;
  const value = globals[BOOTSTRAP_MARKER];
  if (typeof value !== "object" || value === null) return null;
  const version = value.version;
  const profile = value.profile;
  if (
    !Number.isInteger(version) || (profile !== "repl" && profile !== "node")
  ) {
    return null;
  }
  return { version, profile };
}

/**
 * Installs the bootstrap marker on the current realm's global scope.
 * Idempotent. The marker is the only non-profile global the bootstrap
 * installs.
 */
function installBootstrapMarker(marker) {
  const globals = globalThis;
  if (globals[BOOTSTRAP_MARKER] !== undefined) return;
  globals[BOOTSTRAP_MARKER] = Object.freeze({ ...marker });
}

module.exports = {
  BOOTSTRAP_VERSION,
  PROFILE_QUERY_KEY,
  BOOTSTRAP_MARKER,
  readBootstrapMarker,
  installBootstrapMarker,
};
