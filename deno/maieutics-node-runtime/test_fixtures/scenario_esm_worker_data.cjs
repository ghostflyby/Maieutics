"use strict";

const { makeAssert, spawnFixtureWorker, report } = require(
  "./test_helpers.cjs",
);

const assert = makeAssert();

spawnFixtureWorker(
  "target_esm_worker_data.mjs",
  { name: "esm-worker-data", workerData: { id: 42 } },
  (result) => {
    const { phase, workerData, exit } = result;
    try {
      assert(
        phase === "target-top-level",
        `ESM target reached top-level (got ${phase})`,
      );
      assert(
        workerData !== undefined && workerData.id === 42 &&
          Object.keys(workerData).length === 1,
        `static ESM workerData sees the original value (got ${JSON.stringify(workerData)})`,
      );
      assert(exit === 0, `worker exited 0 (got ${exit})`);
      report(true, {});
    } catch (error) {
      report(false, { message: error.message });
    }
  },
);
