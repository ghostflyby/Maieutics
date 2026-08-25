/**
 * Plugin worker entry, materialized by the kernel and passed to each worker as
 * its module URL. The plugin worker is a ROOT worker entered directly by the
 * host factory; this entry installs the shared Maieutics bootstrap (recursive
 * Worker patch + versioned marker) BEFORE the SDK registers the worker-actor
 * runtime and handles the host's `init`/`dispose` control frames. The
 * handshake (and thus `spawn()`'s readiness) is delayed until the host's
 * `maieutics-config` frame arrives, which the host posts before waiting.
 */

import { installBootstrapMarker } from "../maieutics-runtime/bootstrap_contract.ts";
import { installWorkerPatch } from "../maieutics-runtime/worker_patch.ts";
import { initPluginWorker } from "../maieutics-plugin-sdk/runtime.ts";

installWorkerPatch("plugin");
installBootstrapMarker({ version: 1, profile: "plugin" });

void initPluginWorker();
