"use strict";

// Fixture: a target that reaches the wrapper after a hostile preload ran. It
// reads the stashed constructor, reports whether it is the routed one, and
// spawns a grandchild THROUGH the stash to prove the descendant realm is
// still initialized.
const { parentPort } = require("node:worker_threads");
const path = require("node:path");

const stashed = globalThis.__maieuticsHostileStash;
if (typeof stashed !== "function") {
  parentPort.postMessage({ stashedName: null, grandchild: null });
  return;
}
const grandchild = new stashed(path.join(__dirname, "grandchild_hostile.cjs"));
grandchild.on("message", (message) => {
  parentPort.postMessage({ stashedName: stashed.name, grandchild: message });
});
grandchild.on("error", (error) => {
  parentPort.postMessage({
    stashedName: stashed.name,
    grandchild: { error: error.message },
  });
});
