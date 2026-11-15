import { type Frame, PROTOCOL_VERSION } from "./protocol.ts";
import type { PayloadCodecRegistry } from "./codec.ts";
import type { TransportKind } from "./transport.ts";
import { ActorTransportError } from "./lifetime.ts";

export function handshakeError(
  frame: Extract<Frame, { type: "handshake" }>,
  registry: PayloadCodecRegistry,
  transportKind: TransportKind,
): Error | undefined {
  if (
    !Array.isArray(frame.codecs) || !frame.codecs.every((tag) => typeof tag === "string") ||
    frame.actors !== undefined &&
      (!Array.isArray(frame.actors) || !frame.actors.every((name) => typeof name === "string"))
  ) {
    return new ActorTransportError("protocol", "Invalid Actor handshake frame");
  }
  if (frame.version !== PROTOCOL_VERSION) {
    return new Error(
      `Protocol version mismatch: peer speaks ${frame.version}, host expects ${PROTOCOL_VERSION}`,
    );
  }
  // Codec registration mismatch: a startup failure instead of silently wrong values
  const local = registry.tags;
  const remote = frame.codecs;
  const missing = remote.filter((t) => !local.includes(t));
  const extra = local.filter((t) => !remote.includes(t));
  if (missing.length > 0 || extra.length > 0) {
    return new Error(
      `Codec mismatch: peer has extra codecs [${missing.join(", ")}], ` +
        `host has extra codecs [${extra.join(", ")}]`,
    );
  }
  // Transport-kind mismatch: a Mux transport must not be treated as a
  // messageport one (no MessagePort transfer across Mux). A peer that
  // doesn't send kind (older protocol) is accepted as messageport.
  if (frame.kind !== undefined && frame.kind !== transportKind) {
    return new Error(
      `Transport kind mismatch: peer speaks ${frame.kind}, host transport is ${transportKind}`,
    );
  }
  return undefined;
}
