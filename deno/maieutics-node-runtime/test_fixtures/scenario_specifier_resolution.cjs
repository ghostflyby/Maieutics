"use strict";

// Scenario: a relative file-URL target inside a nested worker resolves against
// the CALLER module (the target that created the worker), not against the
// wrapper entry. nested_target.cjs builds its grandchild target from its own
// __dirname; a wrapper-relative resolution would fail with MODULE_NOT_FOUND.

const { makeAssert, spawnFixtureWorker, report } = require(
  "./test_helpers.cjs",
);

const assert = makeAssert();

spawnFixtureWorker(
  "nested_target.cjs",
  { name: "specifier-resolution" },
  (result) => {
    const { phase, nested } = result;
    try {
      assert(
        phase === "nested-reply",
        `relative file URL targets resolve against the caller (got ${phase})`,
      );
      assert(
        nested !== undefined && nested.threadName === "grandchild",
        "grandchild worker reached target top-level",
      );
      report(true, {});
    } catch (error) {
      report(false, { message: error.message });
    }
  },
);
