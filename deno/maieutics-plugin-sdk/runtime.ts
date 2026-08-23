/**
 * Worker runtime bootstrap (`@maieutics/plugin-sdk/runtime`).
 *
 * Plugin authors never import this path: it is the host-side entry used by the
 * materialized worker entry module to start the SDK's worker runtime (config
 * handshake, load hook, actor registry, dispose handling). Everything the
 * runtime wires internally stays out of the plugin author entry.
 */

export { initPluginWorker } from "./mod.ts";
