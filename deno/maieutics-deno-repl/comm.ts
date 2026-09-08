/**
 * Dedicated Jupyter comm client over the process-owned IPC channel.
 *
 * Comm traffic between the kernel and this REPL child travels on its own WebSocket
 * (`/comm`), separate from the control bus, using the shared binary codec in
 * `../shared/comm_codec.ts`. Buffers are native bytes (no base64). The first frame
 * after connect is a JSON hello declaring the session id, verified by the host
 * against the peer process.
 */

import { connectIpcWebSocket } from "../shared/ipc_websocket.ts";
import {
  type CommMessage,
  decodeCommMessage,
  encodeCommMessage,
  MAX_COMM_MESSAGE_BYTES,
} from "../shared/comm_codec.ts";

export { CommKind, type CommMessage } from "../shared/comm_codec.ts";

export interface CommClient {
  /** Sends a comm message to the kernel (relayed to the frontend). */
  send(message: CommMessage): Promise<void>;
  close(code?: number, reason?: string): void;
  readonly isOpen: boolean;
  onMessage: ((message: CommMessage) => void) | undefined;
  onClose: (() => void) | undefined;
  onError: ((error: Error) => void) | undefined;
}

/**
 * Opens the dedicated comm WebSocket for a session. The first frame is a JSON
 * hello declaring the session id; the host attributes the connection through
 * the peer process identity.
 */
export async function connectComm(
  address: string,
  sessionId: string,
  credential?: string,
): Promise<CommClient> {
  const socket = await connectIpcWebSocket(
    address,
    "/comm",
    credential,
    { maxMessageBytes: MAX_COMM_MESSAGE_BYTES },
  );
  const client: CommClient = {
    send: (message) => {
      socket.send(encodeCommMessage(message));
      return Promise.resolve();
    },
    close: (code, reason) => socket.close(code, reason),
    get isOpen() {
      return socket.isOpen;
    },
    onMessage: undefined,
    onClose: undefined,
    onError: undefined,
  };
  const ready = deferred<void>();
  socket.onError = (error) => client.onError?.(error);
  socket.onClose = () => client.onClose?.();
  socket.onMessage = (data) => {
    if (typeof data === "string") {
      // The first text frame is the host's comm.ready acknowledgment; subsequent
      // text frames are protocol violations.
      ready.resolve(undefined);
      return;
    }
    client.onMessage?.(decodeCommMessage(data));
  };
  socket.send(JSON.stringify({ sessionId }));
  await withTimeout(
    ready.promise,
    10_000,
    "Timed out waiting for comm.ready.",
  );
  return client;
}

function deferred<T>(): { promise: Promise<T>; resolve(value: T | PromiseLike<T>): void } {
  let resolvePromise!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((resolve) => {
    resolvePromise = resolve;
  });
  return { promise, resolve: resolvePromise };
}

async function withTimeout<T>(promise: Promise<T>, ms: number, message: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      promise,
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(message)), ms);
      }),
    ]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}
