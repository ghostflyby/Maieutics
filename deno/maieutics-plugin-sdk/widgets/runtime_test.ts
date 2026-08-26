import { assertEquals, assertRejects, assertThrows } from "@std/assert";
import { WidgetRuntime } from "./runtime.ts";

/** A fake broadcast that records comm_open / comm_msg payloads. */
function fakeBroadcast() {
  const calls: Array<{ type: string; content: Record<string, unknown> }> = [];
  return {
    calls,
    broadcast: async (
      messageType: string,
      content: Record<string, unknown>,
    ): Promise<void> => {
      calls.push({ type: messageType, content });
    },
  };
}

Deno.test("init broadcasts comm_open with jupyter.widget target and identity state", () => {
  const { broadcast, calls } = fakeBroadcast();
  const runtime = new WidgetRuntime(broadcast);

  runtime.init("comm-1", {
    _model_module: "@jupyter-widgets/controls",
    _model_name: "IntSliderModel",
    value: 5,
    min: 0,
    max: 10,
  });

  assertEquals(calls.length, 1);
  const open = calls[0];
  assertEquals(open.type, "comm_open");
  const content = open.content as Record<string, unknown>;
  assertEquals(content.comm_id, "comm-1");
  assertEquals(content.target_name, "jupyter.widget");
  const data = content.data as { state: Record<string, unknown> };
  assertEquals(data.state._model_name, "IntSliderModel");
  assertEquals(data.state.value, 5);
});

Deno.test("sync broadcasts comm_msg update with method and delta state", async () => {
  const { broadcast, calls } = fakeBroadcast();
  const runtime = new WidgetRuntime(broadcast);
  const model = runtime.init("comm-1", { value: 5 });

  await model.sync("value", 7);

  assertEquals(calls.length, 2);
  const update = calls[1];
  assertEquals(update.type, "comm_msg");
  const data = update.content as { data: { method: string; state: Record<string, unknown> } };
  assertEquals(data.data.method, "update");
  assertEquals(data.data.state.value, 7);
});

Deno.test("handleIncoming applies frontend update and invokes onChange", () => {
  const { broadcast } = fakeBroadcast();
  const runtime = new WidgetRuntime(broadcast);
  const changes: Array<[string, unknown]> = [];
  const model = runtime.init("comm-1", { value: 5 }, (key, value) => {
    changes.push([key, value]);
  });

  runtime.handleIncoming({
    kind: 1,
    commId: "comm-1",
    data: { method: "update", state: { value: 9 } },
    buffers: [],
  });

  assertEquals(model.get("value"), 9);
  assertEquals(changes, [["value", 9]]);
});

Deno.test("handleIncoming ignores unknown comm ids and non-update methods", () => {
  const { broadcast } = fakeBroadcast();
  const runtime = new WidgetRuntime(broadcast);
  const changes: unknown[] = [];
  runtime.init("comm-1", { value: 5 }, (key, value) => {
    changes.push([key, value]);
  });

  // Unknown comm id: must not throw and must not change anything.
  runtime.handleIncoming({
    kind: 1,
    commId: "nope",
    data: { method: "update", state: { value: 1 } },
    buffers: [],
  });
  // Non-update method: ignored.
  runtime.handleIncoming({
    kind: 1,
    commId: "comm-1",
    data: { method: "backbone", sync_data: { value: 2 } },
    buffers: [],
  });

  assertEquals(changes, []);
});

Deno.test("remove deletes a model and subsequent sync throws", async () => {
  const { broadcast } = fakeBroadcast();
  const runtime = new WidgetRuntime(broadcast);
  const model = runtime.init("comm-1", { value: 5 });

  runtime.remove("comm-1");
  await assertRejects(() => model.sync("value", 6));
});

Deno.test("init with a later onChange does not clobber earlier registration", () => {
  const { broadcast } = fakeBroadcast();
  const runtime = new WidgetRuntime(broadcast);
  const seen: string[] = [];
  runtime.init("comm-1", { value: 5 }, () => seen.push("first"));

  // Same comm id re-registered: the later handler wins (like any Map overwrite).
  runtime.init("comm-1", { value: 6 }, () => seen.push("second"));
  runtime.handleIncoming({
    kind: 1,
    commId: "comm-1",
    data: { method: "update", state: { value: 7 } },
    buffers: [],
  });

  assertEquals(seen, ["second"]);
});
