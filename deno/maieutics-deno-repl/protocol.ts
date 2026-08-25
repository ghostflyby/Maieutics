/** Versioned wire contract for one supervised Deno REPL actor. */

export const REPL_EVAL_PROTOCOL_VERSION = 1;
export const REPL_EVAL_WEBSOCKET_PATH = "/v1/repl/eval/ws";
export const REPL_EVAL_MAX_MESSAGE_BYTES = 1024 * 1024;

export const ReplEvalMessageType = {
  hello: "repl.eval.hello",
  ready: "repl.eval.ready",
  execute: "repl.eval.execute",
  cancel: "repl.eval.cancel",
  dispose: "repl.eval.dispose",
  result: "repl.eval.result",
  error: "repl.eval.error",
  cancelled: "repl.eval.cancelled",
  inputRequest: "repl.eval.inputRequest",
  inputReply: "repl.eval.inputReply",
} as const;

export type ReplEvalMessageType = (typeof ReplEvalMessageType)[keyof typeof ReplEvalMessageType];

export interface ReplEvalEnvelope<TPayload = unknown> {
  version: number;
  type: string;
  correlationId: string;
  payload?: TPayload;
}

export interface ReplEvalIdentity {
  sessionId: string;
  generation: number;
  credential?: string;
}

export interface ReplEvalExecutePayload {
  executionId: string;
  code: string;
}

export interface ReplEvalCancelPayload {
  executionId: string;
}

/** MIME bundle of one display/updateDisplay event. Binary MIME values (image
 * bytes) are carried as native `Uint8Array` values; they never enter the eval
 * JSON channel — the output endpoint moves them as byte buffers. */
export type ReplMediaBundle = Record<string, string | object | Uint8Array>;

export interface ReplEvalInputRequestPayload {
  executionId: string;
  sequence: number;
  requestId: string;
  prompt: string;
  password: boolean;
}

export interface ReplEvalInputReplyPayload {
  executionId: string;
  requestId: string;
  value: string;
}

export interface ReplEvalResultPayload {
  executionId?: string;
  value?: unknown;
}

export interface ReplEvalErrorPayload {
  executionId?: string;
  code: string;
  message: string;
  fatal?: boolean;
}

export interface ReplEvalCancelledPayload {
  executionId: string;
}

export class ReplEvalProtocolError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly correlationId: string = crypto.randomUUID(),
  ) {
    super(message);
    this.name = "ReplEvalProtocolError";
  }
}

const knownMessageTypes = new Set<string>(Object.values(ReplEvalMessageType));
const encoder = new TextEncoder();

export function decodeReplEvalEnvelope(data: string): ReplEvalEnvelope {
  if (encoder.encode(data).byteLength > REPL_EVAL_MAX_MESSAGE_BYTES) {
    throw new ReplEvalProtocolError(
      "message_too_large",
      `REPL eval messages must not exceed ${REPL_EVAL_MAX_MESSAGE_BYTES} bytes.`,
    );
  }

  let value: unknown;
  try {
    value = JSON.parse(data);
  } catch {
    throw new ReplEvalProtocolError("invalid_json", "The REPL eval message is not valid JSON.");
  }

  if (!isRecord(value)) {
    throw new ReplEvalProtocolError(
      "invalid_envelope",
      "The REPL eval envelope must be an object.",
    );
  }

  const correlationId = typeof value.correlationId === "string"
    ? value.correlationId
    : crypto.randomUUID();
  if (value.version !== REPL_EVAL_PROTOCOL_VERSION) {
    throw new ReplEvalProtocolError(
      "unsupported_version",
      `Unsupported REPL eval protocol version '${String(value.version)}'.`,
      correlationId,
    );
  }
  if (typeof value.type !== "string") {
    throw new ReplEvalProtocolError(
      "invalid_envelope",
      "The REPL eval envelope requires a message type.",
      correlationId,
    );
  }
  if (!knownMessageTypes.has(value.type)) {
    throw new ReplEvalProtocolError(
      "unknown_message_type",
      `Unknown REPL eval message type '${value.type}'.`,
      correlationId,
    );
  }
  if (typeof value.correlationId !== "string" || value.correlationId.length === 0) {
    throw new ReplEvalProtocolError(
      "invalid_envelope",
      "The REPL eval envelope requires a correlation id.",
      correlationId,
    );
  }

  return {
    version: value.version,
    type: value.type,
    correlationId: value.correlationId,
    ...(Object.hasOwn(value, "payload") ? { payload: value.payload } : {}),
  };
}

export function encodeReplEvalEnvelope(
  envelope: Omit<ReplEvalEnvelope, "version">,
): string {
  let data: string;
  try {
    data = JSON.stringify({ version: REPL_EVAL_PROTOCOL_VERSION, ...envelope });
  } catch (error) {
    throw new ReplEvalProtocolError(
      "invalid_payload",
      `The REPL eval payload is not JSON serializable: ${errorMessage(error)}`,
      envelope.correlationId,
    );
  }
  if (encoder.encode(data).byteLength > REPL_EVAL_MAX_MESSAGE_BYTES) {
    throw new ReplEvalProtocolError(
      "message_too_large",
      `REPL eval messages must not exceed ${REPL_EVAL_MAX_MESSAGE_BYTES} bytes.`,
      envelope.correlationId,
    );
  }
  return data;
}

export function requirePayloadRecord(envelope: ReplEvalEnvelope): Record<string, unknown> {
  if (!isRecord(envelope.payload)) {
    throw new ReplEvalProtocolError(
      "invalid_payload",
      `Message '${envelope.type}' requires an object payload.`,
      envelope.correlationId,
    );
  }
  return envelope.payload;
}

export function requireString(
  payload: Record<string, unknown>,
  field: string,
  envelope: ReplEvalEnvelope,
): string {
  const value = payload[field];
  if (typeof value !== "string" || value.length === 0) {
    throw new ReplEvalProtocolError(
      "invalid_payload",
      `Message '${envelope.type}' requires a non-empty '${field}'.`,
      envelope.correlationId,
    );
  }
  return value;
}

export function requireInteger(
  payload: Record<string, unknown>,
  field: string,
  envelope: ReplEvalEnvelope,
): number {
  const value = payload[field];
  if (!Number.isSafeInteger(value) || (value as number) < 0) {
    throw new ReplEvalProtocolError(
      "invalid_payload",
      `Message '${envelope.type}' requires a non-negative integer '${field}'.`,
      envelope.correlationId,
    );
  }
  return value as number;
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
