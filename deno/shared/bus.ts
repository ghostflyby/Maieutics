/**
 * Minimal WebSocket control bus over the process-owned IPC channel. Both the
 * REPL client (request side) and the plugin host (response side) connect the
 * same `/ws` endpoint; the hello payload tells the kernel which identity the
 * peer claims.
 */

import { connectIpcWebSocket } from "./ipc_websocket.ts";
import { ENVELOPE_VERSION, type ReplEnvelope } from "./protocol.ts";

export interface BusConnection {
  /** Sends an envelope; the current version is stamped automatically. */
  send(envelope: Omit<ReplEnvelope, "version">): void;
  close(): void;
}

export interface ConnectBusOptions {
  /** Unix-domain socket path or Windows loopback host:port. */
  address: string;
  /** Windows bootstrap credential; never included in the URL. */
  credential?: string;
  /** WebSocket endpoint path on the shared application host. */
  path?: string;
  hello: Omit<ReplEnvelope, "version">;
  onMessage: (envelope: ReplEnvelope) => void;
  onClose?: () => void;
  onError?: (error: Error) => void;
}

/**
 * Opens the control bus, sends the hello handshake, and dispatches every parsed
 * envelope to `onMessage`. The returned connection is valid once the promise
 * resolves; no server readiness handshake is assumed.
 */
export async function connectBus(options: ConnectBusOptions): Promise<BusConnection> {
  const {
    address,
    credential,
    path = "/ws",
    hello,
    onMessage,
    onClose,
    onError,
  } = options;
  const socket = await connectIpcWebSocket(address, path, credential);
  socket.onError = (error) => onError?.(error);
  socket.onClose = () => onClose?.();
  socket.onMessage = (text) => {
    let envelope: ReplEnvelope;
    try {
      envelope = JSON.parse(text) as ReplEnvelope;
    } catch {
      onError?.(new Error("The control WebSocket received invalid JSON."));
      return;
    }
    onMessage(envelope);
  };
  const connection: BusConnection = {
    send: (envelope) =>
      socket.send(
        JSON.stringify({ version: ENVELOPE_VERSION, ...envelope }),
      ),
    close: () => socket.close(),
  };
  socket.send(JSON.stringify({ version: ENVELOPE_VERSION, ...hello }));
  return connection;
}
