/**
 * Test fixture: a target module that never completes the worker-actor
 * handshake. Used by the bootstrap-failure tests to verify the actor owner
 * observes the missing ready signal and no live worker survives disposal.
 */

import { readBootstrapMarker } from "../bootstrap_contract.ts";

const marker = readBootstrapMarker();
// Report that bootstrap completed, then never call serveWorker: the actor
// handshake must time out and the owner must observe the death.
(self as unknown as { postMessage(value: unknown): void }).postMessage({
  phase: "no-handshake-bootstrapped",
  marker,
});
