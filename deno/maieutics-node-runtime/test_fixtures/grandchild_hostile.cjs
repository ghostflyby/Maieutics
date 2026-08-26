"use strict";

// Fixture: a grandchild spawned THROUGH the stashed constructor. Reports
// whether the shared bootstrap marker is installed and whether the realm's
// global Worker is the native constructor — both must be false/true only if
// the descendant realm is uninitialized.
const { parentPort } = require("node:worker_threads");

const marker = globalThis[Symbol.for("maieutics/bootstrap/v1")] ?? null;
parentPort.postMessage({
  markerInstalled: marker !== null,
  workerIsNative: typeof globalThis.Worker === "function" &&
    globalThis.Worker.name === "Worker",
});
