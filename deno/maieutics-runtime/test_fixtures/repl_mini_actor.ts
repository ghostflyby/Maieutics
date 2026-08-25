/**
 * Test fixture: a root worker that installs the shared bootstrap and reports
 * its `name` (proving the factory forwarded the name option) and the marker.
 * The `deno` permission option is proven by the test requiring env+read
 * permissions for the worker to start.
 */

import { installBootstrapMarker, readBootstrapMarker } from "../bootstrap_contract.ts";
import { installWorkerPatch } from "../worker_patch.ts";

installWorkerPatch("repl");
installBootstrapMarker({ version: 1, profile: "repl" });

const marker = readBootstrapMarker();
(self as unknown as { postMessage(value: unknown): void }).postMessage({
  phase: "repl-mini-ready",
  name: self.name,
  version: marker?.version ?? null,
  profile: marker?.profile ?? null,
});
