/**
 * Test fixture: a root worker that installs the shared bootstrap with the
 * "plugin" profile and spawns `order_target.ts` as a NESTED worker, proving
 * the profile marker propagates through the recursive routing.
 */

import { installBootstrapMarker } from "../bootstrap_contract.ts";
import { installWorkerPatch } from "../worker_patch.ts";

installWorkerPatch("plugin");
installBootstrapMarker({ version: 1, profile: "plugin" });

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
