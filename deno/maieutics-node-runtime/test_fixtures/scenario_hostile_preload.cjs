"use strict";

// Scenario: user-provided execArgv / env.NODE_OPTIONS that install a hostile
// preload must not let that preload capture the native constructor or create
// an uninitialized descendant realm. The adapter prepends the Maieutics
// preload, so the hostile script runs AFTER the patch is installed.

const { makeAssert, report } = require("./test_helpers.cjs");
const path = require("node:path");

const assert = makeAssert();
const { Worker } = require("node:worker_threads");

const hostile = path.join(__dirname, "hostile_preload.cjs");
const target = path.join(__dirname, "target_hostile.cjs");

let execArgvResult = null;
let nodeOptionsResult = null;

spawnVia({ execArgv: ["--require", hostile] }, (result) => {
  execArgvResult = result;
  maybeDone();
});
spawnVia(
  { env: { ...process.env, NODE_OPTIONS: `--require ${hostile}` } },
  (result) => {
    nodeOptionsResult = result;
    maybeDone();
  },
);

function spawnVia(options, onResult) {
  const worker = new Worker(target, options);
  const finish = (message) => {
    if (!message) return;
    worker.terminate();
    onResult(message);
  };
  worker.on("message", finish);
  worker.on("error", (error) => {
    finish({ stashedName: null, grandchild: { error: error.message } });
  });
}

function maybeDone() {
  if (execArgvResult !== null && nodeOptionsResult !== null) {
    try {
      for (
        const [label, result] of [
          ["execArgv", execArgvResult],
          ["NODE_OPTIONS", nodeOptionsResult],
        ]
      ) {
        assert(
          result.stashedName !== null &&
            result.stashedName !== "Worker",
          `${label}: the hostile preload must not see the native constructor (got '${result.stashedName}')`,
        );
        assert(
          result.grandchild !== null &&
            result.grandchild.markerInstalled === true &&
            result.grandchild.workerIsNative === false,
          `${label}: a grandchild through the stash stays initialized`,
        );
      }
      report(true, {});
    } catch (error) {
      report(false, { message: error.message });
    }
  }
}
