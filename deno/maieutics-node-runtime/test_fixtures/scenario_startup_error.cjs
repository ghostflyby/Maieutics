"use strict";

// Scenario: a target top-level failure surfaces as Worker startup failure. The
// wrapper's `await import(targetUrl)` rejects; the target never sent a message
// (so the worker was never marked online), and Node reports that as an 'error'
// event on the Worker handle plus a non-zero exit. This is the bootstrap
// failure the design requires the actor owner to observe.

const { makeAssert, spawnFixtureWorker, report } = require(
  "./test_helpers.cjs",
);

const assert = makeAssert();

spawnFixtureWorker("failing_target.cjs", {}, (result) => {
  const { phase, failed, error, exit } = result;
  try {
    assert(
      phase === "error" && failed === true,
      `startup failure is reported through the error surface (got phase '${phase}', failed ${failed})`,
    );
    assert(
      error !== undefined &&
        error.message.includes("fixture target top-level failure"),
      `error message is the target failure (got ${error && error.message})`,
    );
    assert(
      exit !== 0,
      `worker exits non-zero on startup failure (got ${exit})`,
    );
    report(true, {});
  } catch (error) {
    report(false, { message: error.message });
  }
});
