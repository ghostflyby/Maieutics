// Shared Worker wrapper entry for the Node adapter.
//
// NESTED Workers (created through the recursive node:worker_threads patch)
// enter through this module. The preload script patches node:worker_threads
// so this wrapper is the controlled entry for every supported Worker. It:
//
//   1. reads the bootstrap metadata (target file URL + non-sensitive
//      version/profile markers) from workerData;
//   2. installs the recursive Worker patch BEFORE the target module is
//      evaluated (the wrapper also makes sure the preload patch is in place in
//      case the worker was started without the preload in its execArgv);
//   3. installs the versioned bootstrap marker;
//   4. imports the target module.
//
// A bootstrap import or target top-level failure rejects this module's
// top-level evaluation, which Node surfaces as a Worker 'error' event and a
// non-zero exit on the parent Worker handle (startup failure).
//
// The target descriptor travels as workerData: Node strips the query string
// from import.meta.url inside a worker thread, so the wrapper URL query cannot
// carry the target. The wrapper URL therefore carries only the non-sensitive
// profile/version markers.
//
// This module must never be imported directly: only the patch (or a test)
// builds wrapper URLs that carry the contract query keys, and the wrapper
// requires the target in workerData.

import { createRequire } from "node:module";
import { isMainThread, workerData } from "node:worker_threads";

const require = createRequire(import.meta.url);
const contract = require("./node_bootstrap_contract.cjs");
const patch = require("./node_worker_patch.cjs");

if (isMainThread) {
  throw new Error(
    "The Maieutics node worker wrapper must not run on the main thread.",
  );
}

const metadata = readNodeBootstrapMetadata(workerData);
if (metadata === null) {
  throw new Error(
    "The Maieutics node worker bootstrap must run through the shared wrapper " +
      "(build the entry with buildWrapperUrl and pass the target as workerData).",
  );
}

// Restore the user's original workerData view BEFORE the target is imported:
// the routing patch preserved it under a reserved key. The builtin
// node:worker_threads.workerData export is redefinable in a worker realm
// (verified on Node 26.7.0), so the target observes exactly what the caller
// passed.
restoreUserWorkerData();

// A nested worker that entered through the wrapper is patched before its
// target module imports node:worker_threads. The preload normally already
// patched this realm (inherited execArgv); installNodeWorkerPatch is
// idempotent and covers direct-entry workers too.
patch.installNodeWorkerPatch(metadata.profile);
contract.installBootstrapMarker({
  version: metadata.version,
  profile: metadata.profile,
});

await import(metadata.targetUrl);

/**
 * Reads the bootstrap metadata carried in workerData: the target file URL plus
 * non-sensitive version/profile markers. Returns null when the workerData is
 * not a Maieutics bootstrap descriptor.
 */
function readNodeBootstrapMetadata(data) {
  if (typeof data !== "object" || data === null) return null;
  const descriptor = data[patch.BOOTSTRAP_WORKER_DATA_KEY];
  if (typeof descriptor !== "object" || descriptor === null) return null;
  const targetUrl = descriptor.targetUrl;
  if (typeof targetUrl !== "string" || targetUrl.length === 0) return null;
  const version = descriptor.version;
  const profile = descriptor.profile;
  if (
    !Number.isInteger(version) || (profile !== "repl" && profile !== "node")
  ) {
    return null;
  }
  return { targetUrl, version, profile };
}

/**
 * Restores the caller's original workerData view on the
 * node:worker_threads.workerData export.
 */
function restoreUserWorkerData() {
  const data = workerData;
  if (typeof data !== "object" || data === null) return;
  const original = data[patch.USER_WORKER_DATA_KEY];
  try {
    Object.defineProperty(
      workerThreadsNamespace(),
      "workerData",
      { value: original, enumerable: true, writable: true, configurable: true },
    );
    // Keep static ESM imports of the builtin in sync with the redefined CJS export.
    require("node:module").syncBuiltinESMExports();
  } catch {
    // If the export cannot be redefined, target imports may observe the
    // internal envelope rather than the caller's original workerData.
  }
}

function workerThreadsNamespace() {
  return require("node:worker_threads");
}
