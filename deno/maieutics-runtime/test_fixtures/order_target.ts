/**
 * Test fixture: reports the bootstrap marker from the target's top-level code.
 * Used as a NESTED target: the root worker's module creates it through the
 * patched Worker, the wrapper installs patch+marker, then this module runs.
 * If the report arrives, the wrapper installed the patch + marker before this
 * module was evaluated.
 */

import { readBootstrapMarker } from "../bootstrap_contract.ts";

const marker = readBootstrapMarker();
(self as unknown as { postMessage(value: unknown): void }).postMessage({
  phase: "target-top-level",
  version: marker?.version ?? null,
  profile: marker?.profile ?? null,
});
