/**
 * Test fixture: nested target module evaluated inside the wrapped nested
 * Worker. Confirms the recursive wrapper installed the patch (self.name) and
 * that REPL/plugin profile globals are NOT ambiently present.
 */

import { readBootstrapMarker } from "../../bootstrap_contract.ts";

const marker = readBootstrapMarker();
postToParent({
  name: self.name,
  version: marker?.version ?? null,
  profile: marker?.profile ?? null,
  hasReplGlobals: "maieutics" in globalThis ||
    typeof (globalThis as { prompt?: unknown }).prompt === "function",
});

function postToParent(message: unknown): void {
  (self as unknown as { postMessage(value: unknown): void }).postMessage(message);
}
