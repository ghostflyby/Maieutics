/** Test-only node fixture: named actors served with a custom codec, exercising spawnNode's `codecs` option. */
import { serveNode } from "@maieutics/plugin-sdk/actor";
import type { Codec } from "@maieutics/plugin-sdk/actor/codec";

export interface Capsule {
  tag: string;
}

// Same tag/logic as codec_worker.ts's capsule codec (no shared code across
// processes; the tag must agree). Registers before the built-ins.
const capsuleCodec: Codec<Capsule> = {
  tag: "capsule",
  matches(v: unknown): v is Capsule {
    return typeof v === "object" && v !== null &&
      typeof (v as { tag?: unknown }).tag === "string";
  },
  encode(v: Capsule): unknown {
    return { __wCodec: "capsule", payload: v };
  },
  decode(placeholder: { payload: Capsule }): Capsule {
    return placeholder.payload;
  },
};

export const actors = {
  capsule: {
    echo(v: Capsule): Capsule {
      return v;
    },
    /** Sync return: with the codecs tuple, CodecValueTypes keeps Capsule's identity on the client. */
    get(): Capsule {
      return { tag: "fresh" };
    },
  },
};

serveNode(actors, { codecs: [capsuleCodec] });
