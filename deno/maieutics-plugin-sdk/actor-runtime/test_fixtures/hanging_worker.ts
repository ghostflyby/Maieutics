/** Test fixture: a worker module that never calls serveWorker() — no handshake ever arrives. */
export const rpc = {
  ping(): string {
    return "pong";
  },
};

declare const self: { postMessage(message: unknown): void };
self.postMessage({ type: "fixture:loaded" });
