/** Shared shape checks at Actor protocol boundaries; unknown frame types remain extensible. */
import { ActorTransportError } from "./lifetime.ts";
import type { SerializedError } from "./protocol.ts";
import type { RpcRequest, RpcResponse } from "./rpc.ts";

export function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

export function isSerializedError(value: unknown): value is SerializedError {
  return record(value) && typeof value.name === "string" && typeof value.message === "string" &&
    (value.stack === undefined || typeof value.stack === "string");
}

export function isRequest(value: unknown): value is RpcRequest {
  return record(value) && Number.isSafeInteger(value.id) && Number(value.id) > 0 &&
    typeof value.method === "string" && value.method.length <= 256 && Array.isArray(value.args);
}

export function isResponse(value: unknown): value is RpcResponse {
  return record(value) && Number.isSafeInteger(value.id) && Number(value.id) > 0 &&
    (value.ok === true ||
      value.ok === false && isSerializedError(value.error));
}

export function frameRecord(value: unknown): Record<string, unknown> {
  if (!record(value) || typeof value.type !== "string") {
    throw new ActorTransportError("protocol", "Invalid Actor channel frame");
  }
  return value;
}

export function protocolFailure(error: unknown): ActorTransportError {
  return error instanceof ActorTransportError
    ? error
    : new ActorTransportError("protocol", "Actor channel dispatch failed", { cause: error });
}
