import type { Codec } from "../../core/codec.ts";

export const throwingCleanupCodec: Codec = {
  tag: "throwing-cleanup",
  matches: (_value): _value is never => false,
  encode() {
    throw new Error("unused encoder");
  },
  decode() {
    throw new Error("unused decoder");
  },
  onRegistryFail() {
    throw new Error("cleanup sentinel");
  },
};
