/// <reference lib="deno.ns" />
/// <reference lib="es2024.promise" />
import { assertEquals } from "@std/assert";
import type { CommFrame, CommMessage } from "./protocol.ts";
import { UiBridge, type UiCommSocket } from "./uiBridge.ts";

function fixture() {
  const sent: CommMessage[] = [], posted: unknown[] = [];
  let done = false, closeCount = 0;
  let wake = Promise.withResolvers<void>();
  const queue: { frame: CommFrame; consumed: ReturnType<typeof Promise.withResolvers<void>> }[] =
    [];
  const socket: UiCommSocket = {
    send(message) {
      sent.push(message);
    },
    messages: (async function* () {
      while (!done) {
        const item = queue.shift();
        if (item) {
          try {
            yield item.frame;
          } finally {
            item.consumed.resolve();
          }
          continue;
        }
        const pending = wake;
        await pending.promise;
        if (wake === pending) wake = Promise.withResolvers<void>();
      }
    })(),
    close() {
      closeCount++;
      done = true;
      wake.resolve();
    },
  };
  const bridge = new UiBridge({
    connect: () => Promise.resolve(socket),
    post: (message) => posted.push(message),
    log() {},
  });
  return {
    bridge,
    sent,
    posted,
    socket,
    get closeCount() {
      return closeCount;
    },
    async emit(message: CommMessage) {
      const consumed = Promise.withResolvers<void>();
      queue.push({ frame: { kind: "comm", sequence: 1, message }, consumed });
      wake.resolve();
      await consumed.promise;
    },
  };
}
const mount = (bridge: UiBridge, modelId = "f1", viewId = "v1") =>
  bridge.handleRendererMessage({ source: "maieutics-form", type: "mount", modelId, viewId });

Deno.test("form admission overflow rejects only the new model and keeps the shared transport usable", async () => {
  const f = fixture();
  await mount(f.bridge, "kept", "kept-view");
  await mount(f.bridge, "overflow", "overflow-view");
  try {
    for (let index = 0; index < 1024; index++) {
      await f.emit({
        kind: 0,
        commId: index === 0 ? "kept" : `cached-${index}`,
        targetName: "maieutics.form",
        data: { state: { title: "Initial" } },
        buffers: [],
      });
    }
    await f.emit({
      kind: 0,
      commId: "overflow",
      targetName: "maieutics.form",
      data: { state: { title: "Excess" } },
      buffers: [],
    });
    assertEquals(f.posted.at(-1), {
      source: "maieutics-form",
      type: "closed",
      modelId: "overflow",
      code: "form_state_limit",
    }, "Capacity rejection must be scoped to the new form, without unwinding the shared pump");
    assertEquals(f.closeCount, 0, "One excess form must not close the shared socket");
    assertEquals(f.sent, [], "Local admission must not close a producer shared by other views");

    await f.emit({
      kind: 1,
      commId: "kept",
      data: { method: "update", state: { title: "Updated" } },
      buffers: [],
    });
    assertEquals(f.posted.at(-1), {
      source: "maieutics-form",
      type: "state",
      modelId: "kept",
      state: { title: "Updated" },
    });
    for (const [modelId, viewId] of [["kept", "kept-view"], ["overflow", "overflow-view"]]) {
      await f.bridge.handleRendererMessage({
        source: "maieutics-form",
        type: "event",
        modelId,
        viewId,
        name: "cancel",
      });
    }
    assertEquals(
      f.sent.map((message) => message.commId),
      ["kept"],
      "Existing forms must remain interactive while rejected forms stay readonly",
    );

    await f.emit({
      kind: 0,
      commId: "kept",
      targetName: "maieutics.form",
      data: { state: { title: "Refreshed" } },
      buffers: [],
    });
    assertEquals(f.posted.at(-1), {
      source: "maieutics-form",
      type: "state",
      modelId: "kept",
      state: { title: "Refreshed" },
    }, "Refreshing an admitted form must remain possible at capacity");
    await f.emit({ kind: 2, commId: "cached-1", buffers: [] });
    await f.emit({
      kind: 0,
      commId: "overflow",
      targetName: "maieutics.form",
      data: { state: { title: "Admitted" } },
      buffers: [],
    });
    assertEquals(f.posted.at(-1), {
      source: "maieutics-form",
      type: "state",
      modelId: "overflow",
      state: { title: "Admitted" },
    }, "A fresh open must be admitted after another form releases capacity");
    assertEquals(f.closeCount, 0);
    assertEquals(
      f.posted.filter((message) => (message as { type: string }).type === "closed").length,
      1,
    );
  } finally {
    await f.bridge.dispose();
  }
  assertEquals(f.closeCount, 1);
});

Deno.test("form bridge caches typed form state and routes actions for an attached view", async () => {
  const f = fixture();
  await mount(f.bridge);
  try {
    await f.emit({
      kind: 0,
      commId: "f1",
      targetName: "maieutics.form",
      data: { state: { fields: [], title: "Ready" } },
      buffers: [],
    });
    assertEquals(f.posted.at(-1), {
      source: "maieutics-form",
      type: "state",
      modelId: "f1",
      state: { fields: [], title: "Ready" },
    });
    await f.bridge.handleRendererMessage({
      source: "maieutics-form",
      type: "event",
      modelId: "f1",
      viewId: "v1",
      name: "submit",
      payload: { values: { q: "typed" } },
    });
    assertEquals(f.sent[0].data, {
      method: "event",
      name: "submit",
      payload: { values: { q: "typed" } },
    });
    await f.emit({
      kind: 1,
      commId: "f1",
      data: { method: "update", state: { title: "Updated" } },
      buffers: [],
    });
    assertEquals((f.posted.at(-1) as { state: unknown }).state, { fields: [], title: "Updated" });
    await f.emit({ kind: 2, commId: "f1", buffers: [] });
    assertEquals(f.posted.at(-1), { source: "maieutics-form", type: "closed", modelId: "f1" });
    const count = f.sent.length;
    await f.bridge.handleRendererMessage({
      source: "maieutics-form",
      type: "event",
      modelId: "f1",
      viewId: "v1",
      name: "cancel",
    });
    assertEquals(f.sent.length, count);
  } finally {
    await f.bridge.dispose();
  }
});
Deno.test("retired component targets and unknown views cannot obtain a live form", async () => {
  const f = fixture();
  await mount(f.bridge);
  try {
    await f.emit({
      kind: 0,
      commId: "f1",
      targetName: "maieutics.view/old",
      data: { state: { secret: true } },
      buffers: [],
    });
    await f.bridge.handleRendererMessage({
      source: "maieutics-form",
      type: "event",
      modelId: "f1",
      viewId: "spoof",
      name: "cancel",
    });
    assertEquals(f.posted, []);
    assertEquals(f.sent, []);
  } finally {
    await f.bridge.dispose();
  }
});
Deno.test("view disposal removes only that attachment while another view continues", async () => {
  const f = fixture();
  await mount(f.bridge);
  await mount(f.bridge, "f1", "v2");
  try {
    await f.emit({
      kind: 0,
      commId: "f1",
      targetName: "maieutics.form",
      data: { state: {} },
      buffers: [],
    });
    await f.bridge.handleRendererMessage({
      source: "maieutics-form",
      type: "unmount",
      modelId: "f1",
      viewId: "v1",
    });
    for (const viewId of ["v1", "v2"]) {
      await f.bridge.handleRendererMessage({
        source: "maieutics-form",
        type: "event",
        modelId: "f1",
        viewId,
        name: "cancel",
      });
    }
    assertEquals(f.sent.length, 1);
  } finally {
    await f.bridge.dispose();
  }
  await f.bridge.dispose();
  assertEquals(f.closeCount, 1);
});
Deno.test("disposal joins a pending connection and closes the late socket once", async () => {
  const f = fixture(), connected = Promise.withResolvers<UiCommSocket>();
  const bridge = new UiBridge({ connect: () => connected.promise, post() {}, log() {} });
  const mounting = mount(bridge), disposing = bridge.dispose();
  connected.resolve(f.socket);
  await mounting;
  await disposing;
  assertEquals(f.closeCount, 1);
});

Deno.test("form bridge disposal cancels its owned connection before awaiting it", async () => {
  const started = Promise.withResolvers<void>();
  let aborted = false;
  const bridge = new UiBridge({
    connect: (signal) =>
      new Promise<UiCommSocket>((_resolve, reject) => {
        signal.addEventListener("abort", () => {
          aborted = true;
          reject(signal.reason);
        }, { once: true });
        started.resolve();
      }),
    post() {},
    log() {},
  });
  const mounting = mount(bridge);
  await started.promise;
  await bridge.dispose();
  await mounting;
  assertEquals(aborted, true);
});
