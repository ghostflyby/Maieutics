/**
 * Test fixture: nested Worker routing from a root worker. The root worker
 * installs the shared bootstrap (as the real entries do) and then creates a
 * nested module Worker with a RELATIVE specifier, which only resolves
 * correctly if the patch routed the nested Worker through the wrapper and
 * resolved the relative specifier against this caller module (not against the
 * wrapper entry in maieutics-runtime/).
 */

import { installBootstrapMarker } from "../bootstrap_contract.ts";
import { installWorkerPatch } from "../worker_patch.ts";

installWorkerPatch("repl");
installBootstrapMarker({ version: 1, profile: "repl" });

const nested = new Worker(new URL("./nested/nested_target.ts", import.meta.url), {
  type: "module",
});
nested.onmessage = (event: MessageEvent) => {
  postToParent({ phase: "nested-reply", nested: event.data });
  nested.terminate();
};
nested.onerror = (event: ErrorEvent) => {
  postToParent({
    phase: "nested-error",
    message: event.message,
  });
  nested.terminate();
};
postToParent({ phase: "nested-created" });

function postToParent(message: unknown): void {
  (self as unknown as { postMessage(value: unknown): void }).postMessage(message);
}
