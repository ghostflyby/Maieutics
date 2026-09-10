/**
 * Plugin worker entry, materialized by the kernel and passed to each worker as
 * its module URL. The plugin worker is a ROOT worker entered directly by the
 * host factory; this entry installs the shared Maieutics bootstrap (recursive
 * Worker patch + versioned marker) BEFORE the SDK registers the worker-actor
 * runtime and handles the host's `init`/`dispose` control frames. The
 * handshake (and thus `spawn()`'s readiness) is delayed until the host's
 * `maieutics-config` frame arrives, which the host posts before waiting.
 */

import { installBootstrapMarker } from "../maieutics-runtime/bootstrap_contract.ts";
import { installPluginStorage } from "../maieutics-runtime/storage_channel.ts";
import {
  installWorkerPatch,
  onControlledWorkerCreated,
} from "../maieutics-runtime/worker_patch.ts";
import { initPluginWorker } from "../maieutics-plugin-sdk/runtime.ts";

installWorkerPatch("plugin");
installPluginStorage();
installBootstrapMarker({ version: 1, profile: "plugin" });
installCapabilityRelay();

void initPluginWorker();

/**
 * Relays nested (dependency) workers' `capability.request` frames upward to the
 * host, attributed to THIS plugin (the relay runs in the plugin's root realm, so
 * the host's worker→plugin mapping resolves the sender correctly), and routes
 * the correlated `capability.response` frames back down into the requesting
 * nested worker. Ids are rewritten with a relay prefix, so the parent's own
 * capability pending map and a nested worker's can never collide.
 */
function installCapabilityRelay(): void {
  const routes = new Map<string, { worker: Worker; nestedId: string }>();
  let relaySequence = 0;

  onControlledWorkerCreated((worker) => {
    worker.addEventListener("message", (event: MessageEvent) => {
      const frame = event.data as {
        type?: string;
        id?: string;
        capability?: string;
        payload?: unknown;
      };
      if (frame?.type !== "capability.request" || typeof frame.id !== "string") return;

      const globalId = `relay-${++relaySequence}-${frame.id}`;
      routes.set(globalId, { worker, nestedId: frame.id });
      scope.postMessage({
        type: "capability.request",
        id: globalId,
        capability: frame.capability,
        payload: frame.payload,
      });
    });
  });

  const scope = self as unknown as {
    addEventListener(type: string, listener: (event: MessageEvent) => void): void;
    postMessage(message: unknown): void;
  };
  scope.addEventListener("message", (event: MessageEvent) => {
    const frame = event.data as {
      type?: string;
      id?: string;
      ok?: boolean;
      result?: unknown;
      code?: string;
      message?: string;
    };
    if (frame?.type !== "capability.response" || typeof frame.id !== "string") return;
    const route = routes.get(frame.id);
    if (route === undefined) return; // The root realm's own pending map handles these.
    routes.delete(frame.id);
    route.worker.postMessage({
      type: "capability.response",
      id: route.nestedId,
      ok: frame.ok === true,
      result: frame.result,
      code: frame.code,
      message: frame.message,
    });
  });
}
