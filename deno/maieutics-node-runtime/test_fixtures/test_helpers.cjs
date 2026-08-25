// Shared helpers for the Node adapter scenarios.
//
// The scenario files in test_fixtures/ run inside a real `node` process that
// was started with the product preload (`node --require
// node_worker_preload.cjs`), so the patched node:worker_threads.Worker is in
// place before the scenario code observes the builtin. Each scenario reports a
// single JSON line on stdout; the test runner parses it and aggregates.

"use strict";

const { pathToFileURL } = require("node:url");
const path = require("node:path");

const FIXTURES_DIR = __dirname;

/** Creates an assert helper that throws with a descriptive message. */
function makeAssert() {
  return (condition, message) => {
    if (!condition) throw new Error(`assertion failed: ${message}`);
  };
}

/**
 * Spawns a worker for a fixture through the PATCHED
 * node:worker_threads.Worker. The worker reports once via parentPort; the
 * report is forwarded to the callback with the worker's final exit code.
 * Startup failures (target top-level throw) arrive through the worker 'error'
 * event and are reported as `{ phase: "error", failed: true, error, exit }`.
 */
function spawnFixtureWorker(fixtureName, options, onReport) {
  const { Worker } = require("node:worker_threads");
  const target = pathToFileURL(path.join(FIXTURES_DIR, fixtureName)).href;
  const worker = new Worker(target, {
    ...options,
    execArgv: ["--no-warnings"],
  });
  let reported = false;
  const finish = (message) => {
    if (reported) return;
    reported = true;
    worker.on("exit", (code) => {
      onReport({ ...message, exit: code });
    });
  };
  worker.on("message", (message) => finish(message));
  worker.on("error", (error) => {
    finish({ phase: "error", failed: true, error: { message: error.message } });
  });
  worker.on("exit", (code) => {
    if (!reported) {
      finish({
        phase: "exit",
        failed: true,
        error: { message: `worker exited ${code} without a report` },
      });
    }
  });
}

/** Prints a scenario result as a single JSON line. */
function report(ok, payload) {
  process.stdout.write(JSON.stringify({ ok, payload }) + "\n");
}

module.exports = {
  FIXTURES_DIR,
  makeAssert,
  spawnFixtureWorker,
  report,
};
