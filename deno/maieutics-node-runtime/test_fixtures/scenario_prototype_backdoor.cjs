"use strict";

// Scenario: the patched Worker must redirect every user-visible `constructor`
// reference to the routed constructor, while keeping the native instance
// methods and truthful instanceof.

const { makeAssert, report } = require("./test_helpers.cjs");
const path = require("node:path");

const assert = makeAssert();
const { Worker } = require("node:worker_threads");

let worker = null;
try {
  worker = new Worker(path.join(__dirname, "target_simple.cjs"));
} catch {
  worker = null;
}

try {
  assert(
    typeof Worker.prototype.constructor === "function" &&
      Worker.prototype.constructor === Worker,
    "Worker.prototype.constructor is the routed constructor",
  );
  assert(
    typeof Worker.prototype.postMessage === "function" &&
      typeof Worker.prototype.terminate === "function",
    "Worker.prototype keeps the native instance methods",
  );
  assert(
    worker !== null && worker instanceof Worker,
    "a real Worker instance still passes instanceof Worker",
  );
  assert(
    worker !== null && worker.constructor === Worker,
    "a real instance's constructor is the routed constructor",
  );
  const parent = Object.getPrototypeOf(Worker.prototype);
  assert(
    parent === null || parent.constructor === undefined ||
      parent.constructor.name !== "Worker",
    "the prototype parent does not carry the native Worker constructor",
  );
  report(true, {});
} catch (error) {
  report(false, { message: error.message });
} finally {
  if (worker !== null) worker.terminate();
}
