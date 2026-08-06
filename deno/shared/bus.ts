/**
 * Minimal WebSocket control bus over the unix socket control channel. Both the
 * REPL client (request side) and the plugin host (response side) connect the
 * same `/ws` endpoint; the hello payload tells the kernel which identity the
 * peer claims.
 */

import {ENVELOPE_VERSION, type HttpClient, type ReplEnvelope,} from "./protocol.ts";

export interface BusConnection {
  /** Sends an envelope; the current version is stamped automatically. */
  send(envelope: Omit<ReplEnvelope, "version">): void;
  close(): void;
}

export interface ConnectBusOptions {
  /** HttpClient for the unix socket proxy; omit on Windows loopback TCP. */
  http?: HttpClient;
  /** WebSocket endpoint; defaults to the unix-socket control channel. */
  url?: string;
  hello: Omit<ReplEnvelope, "version">;
  onMessage: (envelope: ReplEnvelope) => void;
  onClose?: () => void;
}

/**
 * Opens the control bus, sends the hello handshake, and dispatches every parsed
 * envelope to `onMessage`. The returned connection is valid once the promise
 * resolves; no server readiness handshake is assumed.
 */
export function connectBus(options: ConnectBusOptions): Promise<BusConnection> {
  const {http, url = "ws://localhost/ws", hello, onMessage, onClose} =
      options;
  const socket = http === undefined
      ? new WebSocket(url)
      : new WebSocket(url, {client: http});
  socket.onclose = () => onClose?.();
  socket.onmessage = (event) => {
    let envelope: ReplEnvelope;
    try {
      envelope = JSON.parse(String(event.data)) as ReplEnvelope;
    } catch {
      return;
    }
    onMessage(envelope);
  };
  const connection: BusConnection = {
    send: (envelope) =>
        socket.send(
            JSON.stringify({version: ENVELOPE_VERSION, ...envelope}),
        ),
    close: () => socket.close(),
  };
  return new Promise<BusConnection>((resolve, reject) => {
    socket.onopen = () => {
      socket.send(
          JSON.stringify({version: ENVELOPE_VERSION, ...hello}),
      );
      resolve(connection);
    };
    socket.onerror = () => reject(new Error("the control bus failed to open"));
  });
}
