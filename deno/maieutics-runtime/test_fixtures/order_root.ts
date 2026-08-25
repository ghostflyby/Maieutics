/**
 * Test fixture: a root worker that installs the shared bootstrap and then
 * spawns `order_target.ts` as a NESTED worker. The nested target reports
 * whether the wrapper installed the marker before its top-level code ran.
 */

import { installBootstrapMarker } from "../bootstrap_contract.ts";
import { installWorkerPatch } from "../worker_patch.ts";

installWorkerPatch("repl");
installBootstrapMarker({ version: 1, profile: "repl" });

const nested = new Worker(new URL("./order_target.ts", import.meta.url), { type: "module" });
nested.onmessage = (event: MessageEvent) => {
  postToParent(event.data);
  nested.terminate();
};
nested.onerror = (event: ErrorEvent) => {
  postToParent({ phase: "nested-error", message: event.message });
  nested.terminate();
};

function postToParent(message: unknown): void {
  (self as unknown as { postMessage(value: unknown): void }).postMessage(message);
}
