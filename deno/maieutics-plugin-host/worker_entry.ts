/**
 * Plugin worker entry, materialized by the kernel and passed to each worker as
 * its module URL. The SDK registers the worker-actor runtime and handles the
 * host's `init`/`dispose` control frames; this entry is a thin shim. The
 * handshake (and thus `spawn()`'s readiness) is delayed until the host's
 * `maieutics-config` frame arrives, which the host posts before waiting.
 */

import { initPluginWorker } from "../maieutics-plugin-sdk/runtime.ts";

void initPluginWorker();
