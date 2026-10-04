/**
 * UI model runtime tests against a recording fake broadcast: family dialect
 * bytes, uplink dispatch, and lifecycle — mirroring widgets/runtime_test.ts.
 */

import { assertEquals, assertRejects, assertThrows } from "@std/assert";
import { FORM_FAMILY, FormFamilyContract } from "./form.ts";
import { nativeTarget } from "./family.ts";
import { UiModelRuntime } from "./runtime.ts";

interface BroadcastCall {
  messageType: string;
  content: Record<string, unknown>;
}

function recordingRuntime() {
  const calls: BroadcastCall[] = [];
  const runtime = new UiModelRuntime((messageType, content) => {
    calls.push({ messageType, content });
    return Promise.resolve();
  });
  runtime.registerFamily(new FormFamilyContract());
  return { calls, runtime };
}

Deno.test("create broadcasts the family's comm_open dialect", () => {
  const { calls, runtime } = recordingRuntime();
  const model = runtime.create(FORM_FAMILY, { fields: [], values: {} });
  assertEquals(calls.length, 1);
  const open = calls[0];
  assertEquals(open.messageType, "comm_open");
  assertEquals(open.content.comm_id, model.commId);
  assertEquals(open.content.target_name, nativeTarget(FORM_FAMILY));
  assertEquals(open.content.data, { state: { fields: [], values: {} } });
});

Deno.test("unknown families fail loudly", () => {
  const { runtime } = recordingRuntime();
  assertThrows(() => runtime.create("nope/missing", { a: 1 }), Error, "Unknown view family");
});

Deno.test("sync broadcasts the family's update dialect", async () => {
  const { calls, runtime } = recordingRuntime();
  const model = runtime.create<{ values: Record<string, unknown>; fields: unknown[] }>(
    FORM_FAMILY,
    { fields: [], values: {} },
  );
  await model.sync("values", { q: "typed" });
  const msg = calls[calls.length - 1];
  assertEquals(msg.messageType, "comm_msg");
  assertEquals(msg.content.comm_id, model.commId);
  assertEquals(msg.content.data, {
    method: "update",
    state: { values: { q: "typed" } },
    buffer_paths: [],
  });
  assertEquals(model.get("values"), { q: "typed" });
});

Deno.test("uplink updates fire onChange per known key only", () => {
  const { runtime } = recordingRuntime();
  const seen: [string, unknown][] = [];
  const model = runtime.create<{ values?: unknown; known: string }>(
    FORM_FAMILY,
    { known: "a", values: {} },
    { onChange: (key, value) => seen.push([key, value]) },
  );
  runtime.handleIncoming({
    kind: 1,
    commId: model.commId,
    data: { method: "update", state: { known: "b", unknown: 1 } },
    buffers: [],
  });
  assertEquals(seen, [["known", "b"]]);
  assertEquals(model.get("known"), "b");
});

Deno.test("uplink events fire onEvent with the form's payload shape", () => {
  const { runtime } = recordingRuntime();
  const events: [string, unknown][] = [];
  const model = runtime.create(FORM_FAMILY, { fields: [], values: {} }, {
    onEvent: (name, payload) => events.push([name, payload]),
  });
  runtime.handleIncoming({
    kind: 1,
    commId: model.commId,
    data: { method: "event", name: "submit", payload: { values: { q: "hi" } } },
    buffers: [],
  });
  assertEquals(events, [["submit", { q: "hi" }]]);
});

Deno.test("unknown comm ids and malformed payloads are ignored", () => {
  const { runtime } = recordingRuntime();
  const model = runtime.create(FORM_FAMILY, { fields: [], values: {} });
  runtime.handleIncoming({ kind: 0, commId: model.commId, buffers: [] });
  runtime.handleIncoming({ kind: 1, commId: "missing", data: { method: "update", state: {} }, buffers: [] });
  runtime.handleIncoming({ kind: 1, commId: model.commId, data: { method: "other" }, buffers: [] });
  runtime.handleIncoming({ kind: 1, commId: model.commId, data: 7, buffers: [] });
  assertEquals(model.get("values"), {});
});

Deno.test("remove drops the model; later sync rejects", async () => {
  const { runtime } = recordingRuntime();
  const model = runtime.create(FORM_FAMILY, { fields: [], values: {} });
  assertEquals(runtime.has(model.commId), true);
  runtime.remove(model.commId);
  assertEquals(runtime.has(model.commId), false);
  await assertRejects(() => model.sync("values", {}));
});
