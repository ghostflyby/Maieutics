/**
 * Versioned wire contract for the REPL output endpoint.
 *
 * The output endpoint (`/v1/repl/output/ws`) carries every non-comm output
 * event the REPL actor produces — console, display/updateDisplay, and
 * clearOutput. Unlike the eval endpoint's JSON envelopes, frames are binary:
 * string MIME data rides in a JSON bundle while binary MIME values (image
 * bytes) travel as native byte buffers, so binary data is never text/base64
 * encoded on the wire (AGENTS.md invariant 26). The endpoint is half-duplex
 * (process -> host only).
 *
 * Frame layout (all integers big-endian):
 *
 * ```text
 * [type:1] [seq:8 uint64] [executionIdLen:2 uint16] [executionId UTF-8] [payload...]
 * ```
 *
 * type 0 (stdout):       payload = UTF-8 text
 * type 1 (stderr):       payload = UTF-8 text
 * type 2 (display):      payload =
 *     [bundleJsonLen:4 uint32] [bundleJson UTF-8]
 *     [metadataLen:4 uint32] [metadata UTF-8]
 *     [displayIdLen:2 uint16] [displayId UTF-8]
 *     [isUpdate:1] [bufferCount:2 uint16]
 *     [bufLen:4 uint32] [buf] ...
 *   bundleJson values are strings or JSON objects verbatim; a binary value is
 *   replaced by the placeholder `{"$buffer": <index>}` where index is the
 *   buffer's position in the trailing buffers section.
 * type 3 (clearOutput):  payload = [wait:1]
 *
 * The 64 MiB frame ceiling is a safety guard for the binary buffers, not a
 * functional limit; the eval control endpoint keeps its own 1 MiB ceiling.
 */

import type { ReplMediaBundle } from "./protocol.ts";

export const REPL_OUTPUT_WEBSOCKET_PATH = "/v1/repl/output/ws";
export const REPL_OUTPUT_PROTOCOL_VERSION = 1;
export const REPL_OUTPUT_MAX_MESSAGE_BYTES = 64 * 1024 * 1024;

const HEADER_BYTES = 1 + 8 + 2;

/** Typed protocol error for output frame encode/decode failures. */
export class OutputProtocolError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly correlationId: string = crypto.randomUUID(),
  ) {
    super(message);
    this.name = "OutputProtocolError";
  }
}

export interface StdoutFrame {
  type: 0;
  seq: number;
  executionId: string;
  text: string;
}

export interface StderrFrame {
  type: 1;
  seq: number;
  executionId: string;
  text: string;
}

export interface DisplayFrame {
  type: 2;
  seq: number;
  executionId: string;
  data: ReplMediaBundle;
  metadata?: Record<string, unknown>;
  displayId?: string;
  /** false = display, true = updateDisplay. */
  isUpdate: boolean;
}

export interface ClearOutputFrame {
  type: 3;
  seq: number;
  executionId: string;
  wait: boolean;
}

export type OutputFrame =
  | StdoutFrame
  | StderrFrame
  | DisplayFrame
  | ClearOutputFrame;

const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** Encodes one output frame into its native binary representation. */
export function encodeOutputFrame(frame: OutputFrame): Uint8Array {
  validateCommon(frame.seq, frame.executionId);
  const executionId = encoder.encode(frame.executionId);

  switch (frame.type) {
    case 0:
    case 1: {
      const text = encoder.encode(frame.text);
      const total = HEADER_BYTES + executionId.length + text.length;
      requireWithinLimit(total);
      const result = new Uint8Array(total);
      writeHeader(result, frame.type, frame.seq, executionId);
      result.set(text, HEADER_BYTES + executionId.length);
      return result;
    }
    case 2: {
      const buffers: Uint8Array[] = [];
      const bundle: Record<string, unknown> = {};
      for (const [mime, content] of Object.entries(frame.data)) {
        if (content instanceof Uint8Array) {
          const index = buffers.length;
          buffers.push(content);
          bundle[mime] = { $buffer: index };
        } else if (typeof content === "string" || isObject(content)) {
          bundle[mime] = content;
        } else {
          throw new OutputProtocolError(
            "invalid_bundle",
            `MIME bundle value for '${mime}' is neither a string, an object, nor a byte buffer.`,
          );
        }
      }
      let bundleJson: string;
      let metadataJson: string;
      try {
        bundleJson = JSON.stringify(bundle);
        metadataJson = JSON.stringify(frame.metadata ?? {});
      } catch (error) {
        throw new OutputProtocolError(
          "invalid_bundle",
          `The display bundle is not JSON serializable: ${errorMessage(error)}`,
        );
      }
      const bundleBytes = encoder.encode(bundleJson);
      const metadataBytes = encoder.encode(metadataJson);
      const displayId = encoder.encode(frame.displayId ?? "");
      let total = HEADER_BYTES + executionId.length +
        4 + bundleBytes.length +
        4 + metadataBytes.length +
        2 + displayId.length +
        1 + 2;
      for (const buffer of buffers) total += 4 + buffer.length;
      requireWithinLimit(total);
      const result = new Uint8Array(total);
      const view = new DataView(result.buffer);
      let offset = writeHeader(result, 2, frame.seq, executionId);
      view.setUint32(offset, bundleBytes.length, false);
      offset += 4;
      result.set(bundleBytes, offset);
      offset += bundleBytes.length;
      view.setUint32(offset, metadataBytes.length, false);
      offset += 4;
      result.set(metadataBytes, offset);
      offset += metadataBytes.length;
      view.setUint16(offset, displayId.length, false);
      offset += 2;
      result.set(displayId, offset);
      offset += displayId.length;
      result[offset++] = frame.isUpdate ? 1 : 0;
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
    case 3: {
      const total = HEADER_BYTES + executionId.length + 1;
      requireWithinLimit(total);
      const result = new Uint8Array(total);
      writeHeader(result, 3, frame.seq, executionId);
      result[HEADER_BYTES + executionId.length] = frame.wait ? 1 : 0;
      return result;
    }
  }
}

/** Decodes one native binary output frame back into its structured form. */
export function decodeOutputFrame(data: Uint8Array): OutputFrame {
  if (data.length > REPL_OUTPUT_MAX_MESSAGE_BYTES) {
    throw new OutputProtocolError(
      "message_too_large",
      `REPL output frames must not exceed ${REPL_OUTPUT_MAX_MESSAGE_BYTES} bytes.`,
    );
  }
  if (data.length < HEADER_BYTES) {
    throw new OutputProtocolError(
      "invalid_frame",
      "The REPL output frame is shorter than its header.",
    );
  }
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  let offset = 0;
  const type = data[offset++];
  if (type > 3) {
    throw new OutputProtocolError(
      "unknown_frame_type",
      `Unknown REPL output frame type '${type}'.`,
    );
  }
  const rawSeq = view.getBigUint64(offset, false);
  offset += 8;
  if (rawSeq <= 0n || rawSeq > BigInt(Number.MAX_SAFE_INTEGER)) {
    throw new OutputProtocolError(
      "invalid_sequence",
      "The REPL output frame sequence must be a positive safe integer.",
    );
  }
  const seq = Number(rawSeq);
  const executionIdLength = view.getUint16(offset, false);
  offset += 2;
  const executionId = decoder.decode(
    requireBytes(data, offset, executionIdLength),
  );
  offset += executionIdLength;
  if (executionId.length === 0) {
    throw new OutputProtocolError(
      "invalid_execution_id",
      "The REPL output frame requires a non-empty execution id.",
    );
  }

  switch (type) {
    case 0:
    case 1: {
      const text = decoder.decode(data.subarray(offset));
      return type === 0 ? { type: 0, seq, executionId, text } : { type: 1, seq, executionId, text };
    }
    case 2: {
      const bundleJsonLength = view.getUint32(offset, false);
      offset += 4;
      const bundle = parseJson(
        requireBytes(data, offset, bundleJsonLength),
        "display bundle",
      );
      offset += bundleJsonLength;
      const metadataLength = view.getUint32(offset, false);
      offset += 4;
      const metadata = parseJson(
        requireBytes(data, offset, metadataLength),
        "display metadata",
      );
      offset += metadataLength;
      const displayIdLength = view.getUint16(offset, false);
      offset += 2;
      const displayId = decoder.decode(
        requireBytes(data, offset, displayIdLength),
      );
      offset += displayIdLength;
      const isUpdate = data[offset++] !== 0;
      const bufferCount = view.getUint16(offset, false);
      offset += 2;
      const buffers: Uint8Array[] = [];
      for (let index = 0; index < bufferCount; index++) {
        const bufferLength = view.getUint32(offset, false);
        offset += 4;
        buffers.push(requireBytes(data, offset, bufferLength));
        offset += bufferLength;
      }
      if (offset !== data.length) {
        throw new OutputProtocolError(
          "invalid_frame",
          "The REPL output display frame has trailing bytes.",
        );
      }
      if (!isRecord(bundle) || !isRecord(metadata)) {
        throw new OutputProtocolError(
          "invalid_bundle",
          "The REPL output display bundle and metadata must be JSON objects.",
        );
      }
      return {
        type: 2,
        seq,
        executionId,
        data: rebuildBundle(bundle, buffers),
        ...(Object.keys(metadata).length === 0 ? {} : { metadata }),
        ...(displayId.length === 0 ? {} : { displayId }),
        isUpdate,
      };
    }
    case 3: {
      if (offset + 1 !== data.length) {
        throw new OutputProtocolError(
          "invalid_frame",
          "The REPL output clearOutput frame has an invalid payload length.",
        );
      }
      return { type: 3, seq, executionId, wait: data[offset] !== 0 };
    }
    default:
      throw new OutputProtocolError(
        "unknown_frame_type",
        `Unknown REPL output frame type '${type}'.`,
      );
  }
}

function validateCommon(seq: number, executionId: string): void {
  if (!Number.isSafeInteger(seq) || seq <= 0) {
    throw new OutputProtocolError(
      "invalid_sequence",
      "The REPL output frame sequence must be a positive safe integer.",
    );
  }
  if (typeof executionId !== "string" || executionId.length === 0) {
    throw new OutputProtocolError(
      "invalid_execution_id",
      "The REPL output frame requires a non-empty execution id.",
    );
  }
}

function requireWithinLimit(total: number): void {
  if (total > REPL_OUTPUT_MAX_MESSAGE_BYTES) {
    throw new OutputProtocolError(
      "message_too_large",
      `REPL output frames must not exceed ${REPL_OUTPUT_MAX_MESSAGE_BYTES} bytes.`,
    );
  }
}

function writeHeader(
  target: Uint8Array,
  type: number,
  seq: number,
  executionId: Uint8Array,
): number {
  const view = new DataView(target.buffer);
  target[0] = type;
  view.setBigUint64(1, BigInt(seq), false);
  view.setUint16(9, executionId.length, false);
  target.set(executionId, 11);
  return HEADER_BYTES + executionId.length;
}

function requireBytes(
  data: Uint8Array,
  offset: number,
  length: number,
): Uint8Array {
  if (offset + length > data.length) {
    throw new OutputProtocolError(
      "invalid_frame",
      "The REPL output frame is truncated.",
    );
  }
  return data.subarray(offset, offset + length);
}

function parseJson(bytes: Uint8Array, what: string): unknown {
  try {
    return JSON.parse(decoder.decode(bytes));
  } catch {
    throw new OutputProtocolError(
      "invalid_json",
      `The REPL output ${what} is not valid JSON.`,
    );
  }
}

function rebuildBundle(
  bundle: Record<string, unknown>,
  buffers: Uint8Array[],
): ReplMediaBundle {
  const result: ReplMediaBundle = {};
  for (const [mime, value] of Object.entries(bundle)) {
    if (isBufferPlaceholder(value)) {
      const index = value.$buffer;
      if (!Number.isInteger(index) || index < 0 || index >= buffers.length) {
        throw new OutputProtocolError(
          "invalid_buffer_reference",
          `The REPL output display references buffer '${index}' for mime '${mime}', which is out of range.`,
        );
      }
      result[mime] = buffers[index];
    } else if (typeof value === "string" || isObject(value)) {
      result[mime] = value;
    } else {
      throw new OutputProtocolError(
        "invalid_bundle",
        `The REPL output display value for mime '${mime}' is unsupported.`,
      );
    }
  }
  return result;
}

function isBufferPlaceholder(
  value: unknown,
): value is { $buffer: number } {
  return typeof value === "object" && value !== null && !Array.isArray(value) &&
    Object.keys(value).length === 1 && "$buffer" in value &&
    typeof (value as { $buffer: unknown }).$buffer === "number";
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
