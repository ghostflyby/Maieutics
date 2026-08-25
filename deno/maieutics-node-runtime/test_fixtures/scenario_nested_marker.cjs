"use strict";

// Scenario: a nested Worker created inside a target (through the wrapper-
// installed patch) is routed through the wrapper recursively. The grandchild
// observes the bootstrap marker before its own top-level code runs, proving
// the marker was installed before the target import.

const { makeAssert, spawnFixtureWorker, report } = require(
  "./test_helpers.cjs",
);

const assert = makeAssert();

spawnFixtureWorker("nested_target.cjs", { name: "nested-marker" }, (result) => {
  const { phase, nested } = result;
  try {
    assert(phase === "nested-reply", `phase is nested-reply (got ${phase})`);
    assert(nested !== undefined, "nested report present");
    assert(
      nested.marker !== null &&
        nested.marker.version === 1 &&
        nested.marker.profile === "node",
      "grandchild sees the bootstrap marker before its top-level code",
    );
    assert(
      nested.threadName === "grandchild",
      `grandchild threadName propagates (got ${nested.threadName})`,
    );
    report(true, {});
  } catch (error) {
    report(false, { message: error.message });
  }
});
