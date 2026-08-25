"use strict";

// Scenario: a named import from node:worker_threads resolves to the PATCHED
// Worker after the preload ran, and a worker spawned through it reaches the
// wrapper and exits cleanly.

const { makeAssert, spawnFixtureWorker, report } = require(
  "./test_helpers.cjs",
);

const assert = makeAssert();
const wt = require("node:worker_threads");
const { Worker, threadName } = wt;

assert(typeof Worker === "function", "named import Worker is a function");
assert(typeof threadName === "string", "named import threadName is a string");

spawnFixtureWorker("target_simple.cjs", { name: "named-import" }, (result) => {
  const { phase, exit } = result;
  try {
    assert(
      phase === "target-top-level",
      `worker reached target top-level (got ${phase})`,
    );
    assert(exit === 0, `worker exited 0 (got ${exit})`);
    report(true, {});
  } catch (error) {
    report(false, { message: error.message });
  }
});
