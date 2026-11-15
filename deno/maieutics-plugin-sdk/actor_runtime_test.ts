import { assertStrictEquals } from "@std/assert";
import * as canonical from "@maieutics/plugin-sdk/actor";
import * as legacy from "@ghostflyby/worker-actor";
import * as canonicalCodec from "@maieutics/plugin-sdk/actor/codec";
import * as legacyCodec from "@ghostflyby/worker-actor/codec";
import * as canonicalCodecs from "@maieutics/plugin-sdk/actor/codecs";
import * as legacyCodecs from "@ghostflyby/worker-actor/codecs";
import * as local from "./actor-runtime/mod.ts";

Deno.test("canonical, SDK-local and legacy actor imports share one runtime and codec state", () => {
  assertStrictEquals(canonical.spawn, local.spawn);
  assertStrictEquals(legacy.spawn, local.spawn);
  assertStrictEquals(canonicalCodec.PayloadCodecRegistry, legacyCodec.PayloadCodecRegistry);
  assertStrictEquals(canonicalCodec.setActiveRegistry, legacyCodec.setActiveRegistry);
  assertStrictEquals(canonicalCodecs.iterableCodec, legacyCodecs.iterableCodec);
});
