/**
 * Test fixture: a root worker that installs the shared bootstrap and then
 * spawns `nested/nested_probe.ts` as a NESTED worker. The wrapper installs the
 * patch before the nested target's top-level code runs, so the probe observes
 * the routed `globalThis.Worker`.
 */

import { installBootstrapMarker } from "../bootstrap_contract.ts";
import { installWorkerPatch } from "../worker_patch.ts";

installWorkerPatch("repl");
installBootstrapMarker({ version: 1, profile: "repl" });

const nested = new Worker(new URL("./nested/nested_probe.ts", import.meta.url), {
  type: "module",
});
nested.onmessage = (event: MessageEvent) => {
  postToParent(event.data);
  nested.terminate();
};
nested.onerror = (event: ErrorEvent) => {
  postToParent({ phase: "nested-probe-error", message: event.message });
  nested.terminate();
};

function postToParent(message: unknown): void {
  (self as unknown as { postMessage(value: unknown): void }).postMessage(message);
}
