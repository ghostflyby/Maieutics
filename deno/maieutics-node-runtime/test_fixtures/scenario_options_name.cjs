"use strict";

// Scenario: supported Worker options survive routing. The `name` option is
// observed as the worker thread's threadName (Node sets the native thread name
// from `options.name`; it does not change process.title). workerData must
// arrive unchanged.

const { makeAssert, spawnFixtureWorker, report } = require(
  "./test_helpers.cjs",
);

const assert = makeAssert();

spawnFixtureWorker(
  "target_simple.cjs",
  { name: "custom-name", workerData: { id: 42 } },
  (result) => {
    const { phase, threadName, workerData } = result;
    try {
      assert(
        phase === "target-top-level",
        `phase is target-top-level (got ${phase})`,
      );
      assert(
        threadName === "custom-name",
        `name option propagates (got ${threadName})`,
      );
      assert(
        workerData !== undefined && workerData.id === 42 &&
          Object.keys(workerData).length === 1,
        `workerData survives routing without the bootstrap envelope (got ${
          JSON.stringify(workerData)
        })`,
      );
      report(true, {});
    } catch (error) {
      report(false, { message: error.message });
    }
  },
);
