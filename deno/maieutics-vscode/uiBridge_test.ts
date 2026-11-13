/// <reference lib="deno.ns" />
/// <reference lib="es2024.promise" />
import { assertEquals } from "@std/assert";
import type { CommFrame, CommMessage } from "./protocol.ts";
import { UiBridge, UiBridgeRegistry, type UiCommSocket } from "./uiBridge.ts";

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

const formMessage = (type = "mount") => ({
  source: "maieutics-form",
  type,
  modelId: "shared-model",
  viewId: "shared-output",
  name: "cancel",
});
const formOpen = (title: string): CommMessage => ({
  kind: 0,
  commId: "shared-model",
  targetName: "maieutics.form",
  data: { state: { title } },
  buffers: [],
});

Deno.test("editor scopes route overlapping form identities to their addressed sessions", async () => {
  const a = fixture(), b = fixture();
  const sessions: string[] = [];
  const posted: { owner: string; message: unknown }[] = [];
  const registry = new UiBridgeRegistry<string>({
    connect: (sessionId) => {
      sessions.push(sessionId);
      return Promise.resolve(sessionId === "session-a" ? a.socket : b.socket);
    },
    post: (owner, message) => posted.push({ owner, message }),
    log() {},
  });
  try {
    await registry.handleRendererMessage("editor-a", "session-a", formMessage());
    await registry.handleRendererMessage("editor-b", "session-b", formMessage());
    await a.emit(formOpen("A"));
    await b.emit(formOpen("B"));
    assertEquals(sessions, ["session-a", "session-b"]);
    assertEquals(posted, [
      {
        owner: "editor-a",
        message: {
          source: "maieutics-form",
          type: "state",
          modelId: "shared-model",
          state: { title: "A" },
        },
      },
      {
        owner: "editor-b",
        message: {
          source: "maieutics-form",
          type: "state",
          modelId: "shared-model",
          state: { title: "B" },
        },
      },
    ], "Downlink state must target its owning editor, even when model and view IDs overlap");
    for (const owner of ["editor-a", "editor-b"]) {
      await registry.handleRendererMessage(
        owner,
        owner === "editor-a" ? "session-a" : "session-b",
        formMessage("event"),
      );
    }
    assertEquals(a.sent.length, 1);
    assertEquals(b.sent.length, 1);
    await registry.handleRendererMessage("editor-a", "session-a", formMessage("unmount"));
    assertEquals(a.closeCount, 1, "The last view releases its editor's socket");
    assertEquals(b.closeCount, 0, "Another editor's connection must remain owned and usable");
    assertEquals(
      a.sent.map((message) => message.kind),
      [1],
      "Detaching a view never closes the upstream model",
    );
    await registry.handleRendererMessage("editor-b", "session-b", formMessage("event"));
    assertEquals(b.sent.length, 2);
  } finally {
    await registry.dispose();
  }
  assertEquals([a.closeCount, b.closeCount], [1, 1]);
});

Deno.test("closing one editor preserves a second view of the same session", async () => {
  const a = fixture(), b = fixture();
  const sockets = [a.socket, b.socket];
  const posted: string[] = [];
  const registry = new UiBridgeRegistry<string>({
    connect: (sessionId) => {
      assertEquals(sessionId, "same-session");
      const socket = sockets.shift();
      if (!socket) throw new Error("Unexpected connection");
      return Promise.resolve(socket);
    },
    post: (owner) => posted.push(owner),
    log() {},
  });
  try {
    for (const owner of ["one", "two"]) {
      await registry.handleRendererMessage(owner, "same-session", formMessage());
    }
    await a.emit(formOpen("Shared"));
    await b.emit(formOpen("Shared"));
    await registry.releaseWhere((owner) => owner === "one");
    await registry.handleRendererMessage("two", "same-session", formMessage("event"));
    assertEquals([a.closeCount, b.closeCount], [1, 0]);
    assertEquals([a.sent.length, b.sent.length], [0, 1]);
    await b.emit({
      kind: 1,
      commId: "shared-model",
      data: { method: "update", state: { title: "Still live" } },
      buffers: [],
    });
    assertEquals(posted.at(-1), "two");
  } finally {
    await registry.dispose();
  }
});

Deno.test("an unbound editor or a stale action cannot establish a form connection", async () => {
  const f = fixture();
  const sessions: string[] = [];
  const posted: unknown[] = [];
  const registry = new UiBridgeRegistry<string>({
    connect: (sessionId) => {
      sessions.push(sessionId);
      return Promise.resolve(f.socket);
    },
    post: (_owner, message) => posted.push(message),
    log() {},
  });
  try {
    await registry.handleRendererMessage("editor", undefined, formMessage());
    await registry.handleRendererMessage("editor", "session", formMessage("event"));
    assertEquals(sessions, [], "A mount needs an explicit host-owned session binding");
    await registry.handleRendererMessage("editor", "session", formMessage());
    await f.emit(formOpen("Original session"));
    await registry.handleRendererMessage("editor", "replacement", formMessage("event"));
    assertEquals(
      sessions,
      ["session"],
      "Repinning freezes old views without attaching their actions elsewhere",
    );
    assertEquals(f.sent, []);
    assertEquals(f.closeCount, 1);
    assertEquals(posted.at(-1), {
      source: "maieutics-form",
      type: "closed",
      modelId: "shared-model",
    });
  } finally {
    await registry.dispose();
  }
});

Deno.test("a late connection cannot publish into the editor's replacement scope", async () => {
  const old = fixture(), current = fixture();
  const connecting = Promise.withResolvers<UiCommSocket>();
  const started = Promise.withResolvers<void>();
  const posted: unknown[] = [];
  const registry = new UiBridgeRegistry<string>({
    connect: (sessionId) => {
      if (sessionId === "old") {
        started.resolve();
        return connecting.promise;
      }
      return Promise.resolve(current.socket);
    },
    post: (_owner, message) => posted.push(message),
    log() {},
  });
  const first = registry.handleRendererMessage("editor", "old", formMessage());
  await started.promise;
  const replacement = registry.handleRendererMessage("editor", "current", formMessage());
  try {
    await registry.handleRendererMessage("editor", "current", formMessage());
    await current.emit(formOpen("Replacement"));
    const before = posted.length;
    connecting.resolve(old.socket);
    await Promise.all([first, replacement]);
    assertEquals(old.closeCount, 1);
    assertEquals(
      posted.slice(before).every((message) =>
        (message as { state?: { title?: string } }).state?.title === "Replacement"
      ),
      true,
      "Retired startup must not overwrite or close the current model mirror",
    );
    await registry.handleRendererMessage("editor", "current", formMessage("event"));
    assertEquals(current.sent.length, 1);
  } finally {
    connecting.resolve(old.socket);
    await registry.dispose();
  }
});

Deno.test("registry disposal cancels and joins every pending editor connection", async () => {
  const aborted: string[] = [];
  const started = Promise.withResolvers<void>();
  let count = 0;
  const registry = new UiBridgeRegistry<string>({
    connect: (sessionId, signal) =>
      new Promise<UiCommSocket>((_resolve, reject) => {
        signal.addEventListener("abort", () => {
          aborted.push(sessionId);
          reject(signal.reason);
        }, { once: true });
        if (++count === 2) started.resolve();
      }),
    post() {},
    log() {},
  });
  const mounts = ["one", "two"].map((owner) =>
    registry.handleRendererMessage(owner, owner, formMessage())
  );
  await started.promise;
  await Promise.all([registry.dispose(), registry.dispose()]);
  await Promise.all(mounts);
  assertEquals(aborted.sort(), ["one", "two"]);
  await registry.handleRendererMessage("late", "late", formMessage());
  assertEquals(count, 2, "Terminal disposal rejects late attachment");
});

Deno.test("editor admission stays bounded without disrupting established form scopes", async () => {
  const fixtures = new Map<string, ReturnType<typeof fixture>>();
  const rejected: unknown[] = [];
  const registry = new UiBridgeRegistry<string>({
    connect: (sessionId) => {
      const value = fixture();
      fixtures.set(sessionId, value);
      return Promise.resolve(value.socket);
    },
    post: (_owner, message) => {
      if ((message as { code?: string }).code === "form_scope_limit") rejected.push(message);
    },
    log() {},
  });
  try {
    for (let index = 0; index < 128; index++) {
      await registry.handleRendererMessage(String(index), String(index), formMessage());
    }
    await registry.handleRendererMessage("excess", "excess", formMessage());
    assertEquals(fixtures.size, 128);
    assertEquals(rejected.length, 1);
    const kept = fixtures.get("1");
    if (!kept) throw new Error("Missing established form scope");
    await kept.emit(formOpen("Still live"));
    await registry.handleRendererMessage("1", "1", formMessage("event"));
    assertEquals(kept.sent.length, 1);
    await registry.releaseWhere((owner) => owner === "0");
    await registry.handleRendererMessage("excess", "excess", formMessage());
    assertEquals(fixtures.has("excess"), true, "Released capacity must admit a later mount");
  } finally {
    await registry.dispose();
  }
  assertEquals([...fixtures.values()].every((value) => value.closeCount === 1), true);
});

Deno.test("host closure delivery failure does not abandon another editor's connection", async () => {
  const a = fixture(), b = fixture();
  const logs: string[] = [];
  const registry = new UiBridgeRegistry<string>({
    connect: (sessionId) => Promise.resolve(sessionId === "a" ? a.socket : b.socket),
    post: (_owner, message) => {
      if ((message as { type?: string }).type === "closed") throw new Error("Editor gone");
    },
    log: (message) => logs.push(message),
  });
  for (const owner of ["a", "b"]) await registry.handleRendererMessage(owner, owner, formMessage());
  await registry.dispose();
  assertEquals([a.closeCount, b.closeCount], [1, 1]);
  assertEquals(logs, ["Form closure delivery failed.", "Form closure delivery failed."]);
});
