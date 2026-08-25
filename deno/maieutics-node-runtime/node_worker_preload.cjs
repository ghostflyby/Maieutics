// Preload-installable entry for the Maieutics Node bootstrap.
//
// Install with `node --require <this file>` (CJS preload) or
// `node --import <this file>` (ESM preload; the file is still CommonJS and
// runs at require time). Both forms run before the main module's first
// require/import, so the patched node:worker_threads.Worker is in place before
// user code observes the builtin.
//
// What the preload does:
//
//   - patches node:worker_threads.Worker (recursive routing through the shared
//     wrapper) and re-synchronizes the builtin ESM namespace with
//     syncBuiltinESMExports();
//   - marks the realm with `globalThis.maieutics.bootstrap` (non-sensitive
//     version marker, diagnostics/tests only);
//   - leaves the patched Worker in process.execArgv so nested Workers inherit
//     this preload and their node:worker_threads.Worker is already patched
//     when their wrapper entry runs.
//
// No secrets or target descriptors are ever carried by this file: it is
// static bootstrap code. All targets flow through the recursive routing at
// construction time.

"use strict";

const patch = require("./node_worker_patch.cjs");
const contract = require("./node_bootstrap_contract.cjs");

patch.installNodeWorkerPatch("node");
globalThis["maieutics.bootstrap"] = Object.freeze({
  bootstrapVersion: contract.BOOTSTRAP_VERSION,
});
