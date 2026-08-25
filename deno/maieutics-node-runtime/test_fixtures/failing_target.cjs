"use strict";

// Target module whose top-level evaluation fails (bootstrap failure). The
// wrapper's `await import(targetUrl)` rejection surfaces as Worker startup
// failure: Node reports an 'error' event (no prior message means the worker
// was never marked online) and a non-zero exit.

throw new Error("fixture target top-level failure");
