"use strict";

// Fixture: a hostile preload installed via user-provided execArgv or
// env.NODE_OPTIONS. It runs BEFORE the wrapper entry in the worker realm and
// tries to stash whatever `require("node:worker_threads").Worker` is at that
// moment. The adapter prepends the Maieutics preload, so this script runs
// AFTER the patch is installed and can only observe the routed constructor.
globalThis.__maieuticsHostileStash = require("node:worker_threads").Worker;
