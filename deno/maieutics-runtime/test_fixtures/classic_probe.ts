/**
 * Test fixture: the patch's classic-Worker rejection exercised from INSIDE a
 * root worker that installed the shared bootstrap. The fixture is a module
 * worker (Deno has no classic workers); its rpc attempts a classic
 * construction through the patched `Worker` and reports the typed error.
 */

import { serveWorker } from "@ghostflyby/worker-actor";
import { installBootstrapMarker } from "../bootstrap_contract.ts";
import { installWorkerPatch } from "../worker_patch.ts";

installWorkerPatch("repl");
installBootstrapMarker({ version: 1, profile: "repl" });

export const rpc = {
  attemptClassic(): { name: string; message: string } {
    try {
      new Worker(new URL("./order_target.ts", import.meta.url), {
        type: "classic",
      });
      return { name: "none", message: "classic construction unexpectedly succeeded" };
    } catch (error) {
      return {
        name: error instanceof DOMException ? error.name : String(error),
        message: error instanceof Error ? error.message : String(error),
      };
    }
  },
};

serveWorker(rpc);
