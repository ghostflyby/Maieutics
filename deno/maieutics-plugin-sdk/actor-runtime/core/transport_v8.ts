/** Host-only adapters using node:v8; browser transports supply their own wire codec. */
import { ActorTransportError, positiveLimit } from "./lifetime.ts";
import { createDecoder, createEncoder, deserialize, serialize } from "./frame.ts";
import {
  createMux,
  type MessageTransport,
  messageTransport,
  type Transport,
  type TransportOptions,
} from "./transport.ts";

/**
 * Create a `kind: "framed"` Transport over a WHATWG duplex byte stream
 * (ReadableStream + WritableStream<Uint8Array>, e.g. Deno.connect or process
 * stdio). The frame layer (createEncoder/createDecoder) is wired in, and
 * logical channels are multiplexed over the same connection via Mux control
 * frames.
 */
export function framedTransport(
  readable: ReadableStream<Uint8Array>,
  writable: WritableStream<Uint8Array>,
  options: TransportOptions = {},
): Transport {
  const encoder = createEncoder();
  const decoder = createDecoder();
  const writer = encoder.writable.getWriter();
  const controller = new AbortController();
  const writes = new Set<Promise<void>>();
  const maxPending = positiveLimit(options.maxBufferedMessages, 128);
  const mux = createMux((frame) => {
    if (writes.size >= maxPending) {
      throw new ActorTransportError("capacity", "Actor frame write budget exceeded");
    }
    const task = writer.write(frame).catch((error) => {
      if (!mux.closed) mux.close(error);
    }).finally(() => writes.delete(task));
    writes.add(task);
  }, options);
  mux.onClose((reason) => controller.abort(reason));
  const fail = (error: unknown) => {
    if (!mux.closed) mux.close(error);
  };
  const incoming = readable.pipeThrough(decoder, { signal: controller.signal }).pipeTo(
    new WritableStream({
      write(frame) {
        mux.deliver(frame);
      },
    }),
    { signal: controller.signal },
  ).then(() => mux.close(), fail);
  const outgoing = encoder.readable.pipeTo(writable, { signal: controller.signal }).then(
    () => mux.close(),
    fail,
  );
  const completion = Promise.all([incoming, outgoing]).then(async () => {
    await Promise.all([...writes]);
    writer.releaseLock();
  });
  void completion.catch(fail);
  return {
    get closed() {
      return mux.closed;
    },
    get reason() {
      return mux.reason;
    },
    kind: "framed",
    completion,
    onClose: mux.onClose,
    close: mux.close,
    send: mux.send,
    onMessage: mux.onMessage,
    onChannel: mux.onChannel,
    openChannel: mux.openChannel,
    claimOrphan: mux.claimOrphan,
  };
}

/**
 * WebSocket Transport (message kind). Each binary message is one value: the
 * frame layer is not needed (WS message boundaries are frame boundaries), and
 * Mux multiplexes logical channels over the single WS connection.
 *
 * `deliver` must be wired to the socket's message handler:
 *   ws.onmessage = (ev) => transport.deliver(ev.data);
 */
export function fromWebSocket(
  socket: {
    send(data: string | ArrayBuffer | ArrayBufferView): void;
  },
  options: TransportOptions = {},
): MessageTransport {
  return messageTransport({
    ...options,
    send: (message) => {
      // v8-serialize the value into a binary message (the WS message is the frame).
      socket.send(serialize(message));
    },
    // Inbound WS messages are binary (v8 bytes) or text. Deno delivers binary
    // as Blob by default; convert to bytes then deserialize.
    decode: async (message) => {
      if (typeof message === "string") return message;
      let bytes: Uint8Array;
      if (message instanceof Blob) {
        bytes = new Uint8Array(await message.arrayBuffer());
      } else if (message instanceof ArrayBuffer) {
        bytes = new Uint8Array(message);
      } else {
        bytes = message as Uint8Array;
      }
      return deserialize(bytes);
    },
    onClosed: options.onClosed,
  });
}
