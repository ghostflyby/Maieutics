/** Browser-standard WebSocket ownership; wire encoding is selected by the host. */
import { ActorTransportError, positiveLimit } from "./lifetime.ts";
import {
  type MessageTransport,
  messageTransport,
  type MessageTransportOptions,
} from "./transport.ts";

export interface WireCodec {
  encode(value: unknown): string | Uint8Array | ArrayBuffer;
  decode(
    message: string | Uint8Array | ArrayBuffer | Blob,
    signal?: AbortSignal,
  ): unknown | Promise<unknown>;
}
export interface WebSocketTransportOptions
  extends Omit<MessageTransportOptions, "send" | "decode" | "messageSize"> {
  codec: WireCodec;
  maxMessageBytes?: number;
  maxBufferedBytes?: number;
}
const utf8 = new TextEncoder();
function byteLength(value: string | Uint8Array | ArrayBuffer | Blob): number {
  return typeof value === "string"
    ? utf8.encode(value).byteLength
    : value instanceof Blob
    ? value.size
    : value.byteLength;
}

/** Adopt an OPEN socket. Closing the transport releases its listeners and the socket. */
export function webSocketTransport(
  socket: WebSocket,
  options: WebSocketTransportOptions,
): MessageTransport {
  if (socket.readyState !== 1) {
    throw new ActorTransportError("closed", "Actor WebSocket must be open before adoption");
  }
  const maxMessage = positiveLimit(options.maxMessageBytes, 1024 * 1024);
  const maxBuffered = positiveLimit(options.maxBufferedBytes, 4 * 1024 * 1024);
  const transport = messageTransport({
    ...options,
    maxPendingBytes: options.maxPendingBytes ?? maxBuffered,
    messageSize: (message) => byteLength(message as string | Uint8Array | ArrayBuffer | Blob),
    send(value) {
      try {
        const encoded = options.codec.encode(value);
        const size = byteLength(encoded);
        if (size > maxMessage || socket.bufferedAmount + size > maxBuffered) {
          throw new ActorTransportError("capacity", "Actor WebSocket send budget exceeded");
        }
        const body = encoded instanceof Uint8Array
          ? encoded.buffer instanceof ArrayBuffer
            ? new Uint8Array(encoded.buffer, encoded.byteOffset, encoded.byteLength)
            : new Uint8Array(encoded)
          : encoded;
        socket.send(body);
      } catch (error) {
        throw error instanceof ActorTransportError
          ? error
          : new ActorTransportError("send", "Actor WebSocket send failed", { cause: error });
      }
    },
    decode(message, signal) {
      if (
        !(typeof message === "string" || message instanceof ArrayBuffer ||
          message instanceof Uint8Array || message instanceof Blob)
      ) {
        throw new ActorTransportError("protocol", "Unsupported Actor WebSocket message");
      }
      return options.codec.decode(message, signal);
    },
  });
  const onMessage = (event: MessageEvent) => {
    const value: unknown = event.data;
    if (
      !(typeof value === "string" || value instanceof ArrayBuffer || value instanceof Uint8Array ||
        value instanceof Blob)
    ) {
      transport.close(new ActorTransportError("protocol", "Unsupported Actor WebSocket message"));
      return;
    }
    if (byteLength(value) > maxMessage) {
      transport.close(
        new ActorTransportError("capacity", "Actor WebSocket receive budget exceeded"),
      );
    } else transport.deliver(event.data);
  };
  const onClose = () =>
    transport.close(new ActorTransportError("closed", "Actor WebSocket peer disconnected"));
  const onError = () =>
    transport.close(new ActorTransportError("closed", "Actor WebSocket failed"));
  socket.addEventListener("message", onMessage);
  socket.addEventListener("close", onClose);
  socket.addEventListener("error", onError);
  transport.onClose(() => {
    socket.removeEventListener("message", onMessage);
    socket.removeEventListener("close", onClose);
    socket.removeEventListener("error", onError);
    if (socket.readyState < 2) socket.close();
  });
  return transport;
}
