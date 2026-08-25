"use strict";

// Scenario: a dynamic `import { Worker } from "node:worker_threads"` inside a
// worker resolves to the PATCHED constructor. The wrapper installs the patch
// and calls syncBuiltinESMExports() before importing this target, so the ESM
// namespace must expose the patched Worker. The child spawned through the
// imported constructor must reach the wrapper and report the marker.

const { makeAssert, report } = require("./test_helpers.cjs");

const assert = makeAssert();

(async () => {
  const { Worker, threadName } = await import("node:worker_threads");
  const { pathToFileURL } = await import("node:url");
  const path = await import("node:path");

  assert(typeof Worker === "function", "dynamic import Worker is a function");
  assert(
    typeof threadName === "string",
    "dynamic import threadName is a string",
  );

  const nested = new Worker(
    pathToFileURL(path.join(__dirname, "import_child.cjs")),
    { name: "import-child" },
  );
  nested.on("message", (message) => {
    try {
      assert(message.marker !== null, "import child sees the bootstrap marker");
      assert(
        message.threadName === "import-child",
        "import child threadName propagates",
      );
      report(true, {});
    } catch (error) {
      report(false, { message: error.message });
    }
    nested.terminate();
  });
  nested.on("error", (error) => {
    report(false, { message: `import child error: ${error.message}` });
  });
})();
