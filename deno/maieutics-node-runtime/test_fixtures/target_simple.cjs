"use strict";

const { parentPort, threadName, workerData } = require("node:worker_threads");

parentPort.postMessage({
  phase: "target-top-level",
  marker: readMarker(),
  threadName,
  name: threadName,
  hasReplGlobals: false,
  hasBootstrapGlobal: typeof globalThis["maieutics.bootstrap"] !== "undefined",
  workerData,
});

function readMarker() {
  const value = globalThis[Symbol.for("maieutics/bootstrap/v1")];
  if (typeof value !== "object" || value === null) return null;
  return { version: value.version, profile: value.profile };
}
