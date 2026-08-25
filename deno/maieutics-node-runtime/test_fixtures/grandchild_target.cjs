"use strict";

const { parentPort, threadName } = require("node:worker_threads");

parentPort.postMessage({
  threadName,
  marker: readMarker(),
});

function readMarker() {
  const value = globalThis[Symbol.for("maieutics/bootstrap/v1")];
  if (typeof value !== "object" || value === null) return null;
  return { version: value.version, profile: value.profile };
}
