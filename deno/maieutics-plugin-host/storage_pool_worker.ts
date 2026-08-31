/**
 * Storage pool worker entry (ADR 0022): the thread that owns per-plugin
 * SQLite databases and executes storage ops synchronously, off the host main
 * isolate.
 *
 * Frame protocol with the router (storage_pool.ts):
 *   - `maieutics-storage-op {sab, pluginId, dataDir}`: forwarded plugin op.
 *     The worker ACKS ON RECEIPT (before executing) — the router's pending
 *     registry only covers the route→ack window, and a crash after the ack
 *     leaves the parked realm on its own bounded `Atomics.wait` timeout
 *     instead of racing a late failure reply.
 *   - `maieutics-storage-close {}`: close all databases (WAL checkpoint) and
 *     confirm with `maieutics-storage-closed`.
 *
 * The worker replies to the plugin realm by writing the mailbox directly —
 * the router is never in the reply path.
 */

import {
  STORAGE_ACK_FRAME_TYPE,
  STORAGE_CLOSE_FRAME_TYPE,
  STORAGE_CLOSED_FRAME_TYPE,
  STORAGE_OP_FRAME_TYPE,
} from "./storage_pool.ts";
import { StorageEngine } from "./storage_engine.ts";

const engine = new StorageEngine();

const scope = self as unknown as {
  onmessage: ((event: MessageEvent) => void) | null;
  postMessage(message: unknown): void;
};

scope.onmessage = (event: MessageEvent) => {
  const frame = event.data as {
    type?: string;
    sab?: unknown;
    pluginId?: unknown;
    dataDir?: unknown;
  };
  if (frame?.type === STORAGE_OP_FRAME_TYPE) {
    if (
      !(frame.sab instanceof SharedArrayBuffer) ||
      typeof frame.pluginId !== "string" ||
      typeof frame.dataDir !== "string"
    ) {
      // Malformed routing frame: error replies are the router's job.
      return;
    }
    const sab: SharedArrayBuffer = frame.sab;
    scope.postMessage({ type: STORAGE_ACK_FRAME_TYPE, sab });
    engine.handle(frame.pluginId, frame.dataDir, sab);
    return;
  }
  if (frame?.type === STORAGE_CLOSE_FRAME_TYPE) {
    engine.shutdown();
    scope.postMessage({ type: STORAGE_CLOSED_FRAME_TYPE });
    return;
  }
};
