"use strict";

// Scenario: unsupported Worker forms are rejected BEFORE any worker is
// created. The adapter must not silently claim to cover semantics it cannot
// route: classic, explicit commonjs, eval, and data: entries are all refused.
// The rejection is a DOMException with name `NotSupportedError`, matching the
// Deno side (deno/maieutics-runtime). Node's DOMException inherits from Error,
// so `error instanceof Error` still holds and both `error.name` and
// `error.message` are readable.

const { makeAssert, report } = require("./test_helpers.cjs");
const path = require("node:path");

const assert = makeAssert();
const wt = require("node:worker_threads");
const target = path.join(__dirname, "target_simple.cjs");

let classicName = null;
let classicMessage = null;
let commonjsName = null;
let commonjsMessage = null;
let evalName = null;
let evalMessage = null;
let dataName = null;
let dataMessage = null;

try {
  new wt.Worker(target, { type: "classic" });
} catch (error) {
  classicName = error.name;
  classicMessage = error instanceof Error ? error.message : String(error);
}
try {
  new wt.Worker(target, { type: "commonjs" });
} catch (error) {
  commonjsName = error.name;
  commonjsMessage = error instanceof Error ? error.message : String(error);
}
try {
  new wt.Worker("1+1", { eval: true });
} catch (error) {
  evalName = error.name;
  evalMessage = error instanceof Error ? error.message : String(error);
}
try {
  new wt.Worker(new URL("data:text/javascript,1"));
} catch (error) {
  dataName = error.name;
  dataMessage = error instanceof Error ? error.message : String(error);
}

try {
  assert(
    classicName === "NotSupportedError" &&
      classicMessage !== null &&
      classicMessage.includes("Classic workers are not supported"),
    "classic Worker is a DOMException with name NotSupportedError",
  );
  assert(
    commonjsName === "NotSupportedError" &&
      commonjsMessage !== null &&
      commonjsMessage.includes("CommonJS workers are not supported"),
    "commonjs Worker is a DOMException with name NotSupportedError",
  );
  assert(
    evalName === "NotSupportedError" &&
      evalMessage !== null &&
      evalMessage.includes("eval workers are not supported"),
    "eval Worker is a DOMException with name NotSupportedError",
  );
  assert(
    dataName === "NotSupportedError" &&
      dataMessage !== null &&
      dataMessage.includes("data: worker entries are not supported"),
    "data: Worker is a DOMException with name NotSupportedError",
  );
  report(true, {});
} catch (error) {
  report(false, { message: error.message });
}
