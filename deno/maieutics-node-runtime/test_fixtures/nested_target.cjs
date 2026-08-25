"use strict";

// Nested target: creates a further Worker through the patched
// node:worker_threads.Worker (the wrapper installed the patch before importing
// this module). The grandchild worker is itself routed through the wrapper, so
// it reports the bootstrap marker from its top-level code. No preload is
// required inside this realm: the wrapper's own patch routes the creation.

const { Worker, parentPort } = require("node:worker_threads");
const { pathToFileURL } = require("node:url");
const path = require("node:path");

const grandchild = new Worker(
  pathToFileURL(path.join(__dirname, "grandchild_target.cjs")),
  { name: "grandchild" },
);
grandchild.on("message", (message) => {
  parentPort.postMessage({ phase: "nested-reply", nested: message });
  grandchild.terminate();
});
grandchild.on("error", (error) => {
  parentPort.postMessage({ phase: "nested-error", message: error.message });
});
