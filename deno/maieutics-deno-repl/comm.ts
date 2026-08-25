/**
 * Dedicated Jupyter comm client over the process-owned IPC channel.
 *
 * Comm traffic between the kernel and this REPL child travels on its own WebSocket
 * (`/comm`), separate from the control bus. Messages are a fixed binary encoding:
 *
 * ```text
 * [kind:1][commIdLen:2][commId][targetNameLen:2][targetName][dataLen:4][data][bufferCount:2][bufLen:4][buf]...
 * ```
 *
 * Buffers are native bytes (no base64). The first frame after connect is a JSON
 * hello declaring the session id, verified by the host against the peer process.
 */

import { connectIpcWebSocket } from "../shared/ipc_websocket.ts";

const MAX_MESSAGE_BYTES = 16 * 1024 * 1024;

export enum CommKind {
  Open = 0,
  Message = 1,
  Close = 2,
}

export interface CommMessage {
  kind: CommKind;
  commId: string;
  targetName?: string;
  data?: unknown;
  buffers: Uint8Array[];
}

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
    { maxMessageBytes: MAX_MESSAGE_BYTES },
  );
  const client: CommClient = {
    send: (message) => {
      socket.send(encode(message));
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
    client.onMessage?.(decode(data));
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

function encode(message: CommMessage): Uint8Array {
  const commId = new TextEncoder().encode(message.commId);
  const targetName = new TextEncoder().encode(message.targetName ?? "");
  const data = message.data === undefined ? new Uint8Array() : encodeData(message.data);
  const buffers = message.buffers;

  let total = 1 + 2 + commId.length + 2 + targetName.length + 4 + data.length + 2;
  for (const buffer of buffers) total += 4 + buffer.length;
  if (total > MAX_MESSAGE_BYTES) {
    throw new RangeError(`The comm message exceeds ${MAX_MESSAGE_BYTES} bytes.`);
  }

  const result = new Uint8Array(total);
  const view = new DataView(result.buffer);
  let offset = 0;
  result[offset++] = message.kind;
  view.setUint16(offset, commId.length, false);
  offset += 2;
  result.set(commId, offset);
  offset += commId.length;
  view.setUint16(offset, targetName.length, false);
  offset += 2;
  result.set(targetName, offset);
  offset += targetName.length;
  view.setUint32(offset, data.length, false);
  offset += 4;
  result.set(data, offset);
  offset += data.length;
  view.setUint16(offset, buffers.length, false);
  offset += 2;
  for (const buffer of buffers) {
    view.setUint32(offset, buffer.length, false);
    offset += 4;
    result.set(buffer, offset);
    offset += buffer.length;
  }
  return result;
}

function decode(frames: Uint8Array): CommMessage {
  const view = new DataView(frames.buffer, frames.byteOffset, frames.byteLength);
  let offset = 0;
  const kind = frames[offset++] as CommKind;
  const commIdLength = view.getUint16(offset, false);
  offset += 2;
  const commId = new TextDecoder().decode(frames.subarray(offset, offset + commIdLength));
  offset += commIdLength;
  const targetNameLength = view.getUint16(offset, false);
  offset += 2;
  const targetName = targetNameLength === 0
    ? undefined
    : new TextDecoder().decode(frames.subarray(offset, offset + targetNameLength));
  offset += targetNameLength;
  const dataLength = view.getUint32(offset, false);
  offset += 4;
  let data: unknown;
  if (dataLength > 0) {
    data = JSON.parse(new TextDecoder().decode(frames.subarray(offset, offset + dataLength)));
    offset += dataLength;
  }
  const bufferCount = view.getUint16(offset, false);
  offset += 2;
  const buffers: Uint8Array[] = [];
  for (let index = 0; index < bufferCount; index++) {
    const bufferLength = view.getUint32(offset, false);
    offset += 4;
    buffers.push(frames.slice(offset, offset + bufferLength));
    offset += bufferLength;
  }
  return { kind, commId, targetName, data, buffers };
}

function encodeData(data: unknown): Uint8Array {
  const text = JSON.stringify(data);
  if (text === undefined) throw new TypeError("The comm data is not JSON serializable.");
  return new TextEncoder().encode(text);
}
