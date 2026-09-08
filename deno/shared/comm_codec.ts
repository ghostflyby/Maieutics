/**
 * The shared comm wire codec (ADR 0024). Every hop a comm message takes — REPL
 * child ↔ host, host ↔ frontend — encodes with this fixed binary layout, all
 * lengths big-endian:
 *
 * ```text
 * [kind:1][commIdLen:2][commId][targetNameLen:2][targetName][dataLen:4][data][metadataLen:4][metadata][bufferCount:2][bufLen:4][buf]...
 * ```
 *
 * `data` and `metadata` are UTF-8 JSON (zero length = absent); buffers are
 * native bytes, never base64. The frontend comms endpoint additionally wraps
 * each codec frame in an 8-byte big-endian envelope sequence so clients can
 * resume with `sinceSeq` — see `encodeCommEnvelope` / `decodeCommEnvelope`.
 */

export const MAX_COMM_MESSAGE_BYTES = 16 * 1024 * 1024;

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
  /** Jupyter message metadata (comm_open carries the protocol version). */
  metadata?: Record<string, unknown>;
}

export function encodeCommMessage(message: CommMessage): Uint8Array {
  const commId = new TextEncoder().encode(message.commId);
  const targetName = new TextEncoder().encode(message.targetName ?? "");
  const data = message.data === undefined ? new Uint8Array() : encodeData(message.data);
  const metadata = message.metadata === undefined ? new Uint8Array() : encodeData(message.metadata);
  const buffers = message.buffers;

  let total = 1 + 2 + commId.length + 2 + targetName.length + 4 + data.length +
    4 + metadata.length + 2;
  for (const buffer of buffers) total += 4 + buffer.length;
  if (total > MAX_COMM_MESSAGE_BYTES) {
    throw new RangeError(`The comm message exceeds ${MAX_COMM_MESSAGE_BYTES} bytes.`);
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
  view.setUint32(offset, metadata.length, false);
  offset += 4;
  result.set(metadata, offset);
  offset += metadata.length;
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

export function decodeCommMessage(frames: Uint8Array): CommMessage {
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
  const metadataLength = view.getUint32(offset, false);
  offset += 4;
  let metadata: Record<string, unknown> | undefined;
  if (metadataLength > 0) {
    metadata = JSON.parse(
      new TextDecoder().decode(frames.subarray(offset, offset + metadataLength)),
    );
    offset += metadataLength;
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
  return { kind, commId, targetName, data, buffers, metadata };
}

/** Sequence envelope size for the frontend comms hop. */
export const COMM_ENVELOPE_SEQUENCE_BYTES = 8;

/** Wraps a codec frame with the frontend hop's big-endian envelope sequence. */
export function encodeCommEnvelope(sequence: number, message: CommMessage): Uint8Array {
  const frame = encodeCommMessage(message);
  const payload = new Uint8Array(COMM_ENVELOPE_SEQUENCE_BYTES + frame.length);
  new DataView(payload.buffer).setBigUint64(0, BigInt(sequence), false);
  payload.set(frame, COMM_ENVELOPE_SEQUENCE_BYTES);
  return payload;
}

/** Splits an uplink envelope: the client does not assign ordering, so the
 * sequence it sends is zero and the server renumbers. */
export function decodeCommEnvelope(payload: Uint8Array): {
  sequence: number;
  message: CommMessage;
} {
  if (payload.length < COMM_ENVELOPE_SEQUENCE_BYTES) {
    throw new RangeError("The comm frame is missing its envelope sequence.");
  }

  const sequence = Number(
    new DataView(payload.buffer, payload.byteOffset, payload.byteLength).getBigUint64(0, false),
  );
  return {
    sequence,
    message: decodeCommMessage(payload.subarray(COMM_ENVELOPE_SEQUENCE_BYTES)),
  };
}

function encodeData(data: unknown): Uint8Array {
  const text = JSON.stringify(data);
  if (text === undefined) throw new TypeError("The comm data is not JSON serializable.");
  return new TextEncoder().encode(text);
}
