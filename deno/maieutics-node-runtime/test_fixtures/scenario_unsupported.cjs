"use strict";

// Scenario: unsupported Worker forms are rejected with a typed error BEFORE
// any worker is created. The adapter must not silently claim to cover
// semantics it cannot route: classic, explicit commonjs, eval, and data:
// entries are all refused.

const { makeAssert, report } = require("./test_helpers.cjs");
const path = require("node:path");

const assert = makeAssert();
const wt = require("node:worker_threads");
const target = path.join(__dirname, "target_simple.cjs");

let classicMessage = null;
let commonjsMessage = null;
let evalMessage = null;
let dataMessage = null;

try {
  new wt.Worker(target, { type: "classic" });
} catch (error) {
  classicMessage = error instanceof Error ? error.message : String(error);
}
try {
  new wt.Worker(target, { type: "commonjs" });
} catch (error) {
  commonjsMessage = error instanceof Error ? error.message : String(error);
}
try {
  new wt.Worker("1+1", { eval: true });
} catch (error) {
  evalMessage = error instanceof Error ? error.message : String(error);
}
try {
  new wt.Worker(new URL("data:text/javascript,1"));
} catch (error) {
  dataMessage = error instanceof Error ? error.message : String(error);
}

try {
  assert(
    classicMessage !== null &&
      classicMessage.includes("Classic workers are not supported"),
    "classic Worker is a typed unsupported operation",
  );
  assert(
    commonjsMessage !== null &&
      commonjsMessage.includes("CommonJS workers are not supported"),
    "commonjs Worker is a typed unsupported operation",
  );
  assert(
    evalMessage !== null &&
      evalMessage.includes("eval workers are not supported"),
    "eval Worker is a typed unsupported operation",
  );
  assert(
    dataMessage !== null &&
      dataMessage.includes("data: worker entries are not supported"),
    "data: Worker is a typed unsupported operation",
  );
  report(true, {});
} catch (error) {
  report(false, { message: error.message });
}
