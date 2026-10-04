/// <reference lib="deno.ns" />
/**
 * UI bridge tests: state caching, family-dispatched envelopes, and
 * renderer↔comm routing against a scripted socket, with a captured post
 * sink (ports widgets_test.ts and adds the native-family paths).
 */

import { assertEquals } from "@std/assert";
import type { CommMessage } from "../shared/comm_codec.ts";
import type { CommFrame } from "./protocol.ts";
import { UiBridge, type UiCommSocket } from "./uiBridge.ts";

interface ScriptedFrame {
  sequence: number;
  kind: 0 | 1 | 2;
  commId: string;
  targetName?: string;
  data?: unknown;
}

function scriptedSocket() {
  const sent: CommMessage[] = [];
  let closeCount = 0;
  let closed = false;
  const queue: ScriptedFrame[] = [];
  let wake: () => void = () => {};
  const messages = (async function* (): AsyncGenerator<CommFrame> {
    while (!closed) {
      while (queue.length > 0) {
        const frame = queue.shift()!;
        yield {
          kind: "comm",
          sequence: frame.sequence,
          message: {
            kind: frame.kind,
            commId: frame.commId,
            targetName: frame.targetName,
            data: frame.data,
            buffers: [],
          },
        };
      }
      if (closed) return;
      await new Promise<void>((resolve) => {
        wake = resolve;
      });
    }
  })();
  const emit = (frame: ScriptedFrame) => {
    queue.push(frame);
    wake();
  };
  const socket: UiCommSocket = {
    send: (message) => sent.push(message),
    messages,
    close: () => {
      closed = true;
      closeCount += 1;
      wake();
    },
  };
  return {
    socket,
    sent,
    emit,
    get closeCount() {
      return closeCount;
    },
  };
}

function bridgeOver(scripted: ReturnType<typeof scriptedSocket>) {
  const posted: unknown[] = [];
  const bridge = new UiBridge({
    connect: () => Promise.resolve(scripted.socket),
    post: (message) => posted.push(message),
    log: () => {},
  });
  return { bridge, posted };
}

function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 5));
}

async function mounted(bridge: UiBridge, modelId: string): Promise<void> {
  // Mount first (connects the socket; nothing is posted while no state is
  // known), then the test emits downlink frames and flushes — the pump's
  // postState is then the single reply.
  await bridge.handleRendererMessage({ source: "maieutics-widget", type: "mount", modelId });
  await flush();
}

Deno.test("mount replies with the widget-family state from comm_open", async () => {
  const scripted = scriptedSocket();
  const { bridge, posted } = bridgeOver(scripted);
  await mounted(bridge, "w1");
  scripted.emit({
    sequence: 1,
    kind: 0,
    commId: "w1",
    targetName: "jupyter.widget",
    data: { state: { value: 5 } },
  });
  await flush();
  assertEquals(posted, [
    { source: "maieutics-widget", type: "state", modelId: "w1", state: { value: 5 } },
  ]);
  await bridge.dispose();
});

Deno.test("native-target opens speak the maieutics-ui source", async () => {
  const scripted = scriptedSocket();
  const { bridge, posted } = bridgeOver(scripted);
  await bridge.handleRendererMessage({ source: "maieutics-ui", type: "mount", modelId: "u1" });
  await flush();
  scripted.emit({
    sequence: 1,
    kind: 0,
    commId: "u1",
    targetName: "maieutics.view/maieutics.form",
    data: { state: { fields: [] } },
  });
  await flush();
  assertEquals(posted, [
    { source: "maieutics-ui", type: "state", modelId: "u1", state: { fields: [] } },
  ]);
  await bridge.dispose();
});

Deno.test("renderer updates merge locally and relay upstream", async () => {
  const scripted = scriptedSocket();
  const { bridge, posted } = bridgeOver(scripted);
  await mounted(bridge, "w1");
  scripted.emit({
    sequence: 1,
    kind: 0,
    commId: "w1",
    targetName: "jupyter.widget",
    data: { state: { value: 1 } },
  });
  await flush();
  await bridge.handleRendererMessage({
    source: "maieutics-widget",
    type: "update",
    modelId: "w1",
    state: { value: 2 },
  });
  assertEquals(scripted.sent, [{
    kind: 1,
    commId: "w1",
    data: { method: "update", state: { value: 2 }, buffer_paths: [] },
    buffers: [],
  }]);
  scripted.emit({
    sequence: 2,
    kind: 1,
    commId: "w1",
    data: { method: "update", state: { other: "x" } },
  });
  await flush();
  assertEquals(posted.at(-1), {
    source: "maieutics-widget",
    type: "state",
    modelId: "w1",
    state: { value: 2, other: "x" },
  });
  await bridge.dispose();
});

Deno.test("event envelopes relay as native event comm messages", async () => {
  const scripted = scriptedSocket();
  const { bridge } = bridgeOver(scripted);
  await bridge.handleRendererMessage({ source: "maieutics-ui", type: "mount", modelId: "u1" });
  await flush();
  await bridge.handleRendererMessage({
    source: "maieutics-ui",
    type: "event",
    modelId: "u1",
    name: "submit",
    payload: { values: { q: "typed" } },
  });
  assertEquals(scripted.sent, [{
    kind: 1,
    commId: "u1",
    data: { method: "event", name: "submit", payload: { values: { q: "typed" } } },
    buffers: [],
  }]);
  await bridge.dispose();
});

Deno.test("comm_close drops the cached state; a later mount stays silent", async () => {
  const scripted = scriptedSocket();
  const { bridge, posted } = bridgeOver(scripted);
  await mounted(bridge, "w1");
  scripted.emit({
    sequence: 1,
    kind: 0,
    commId: "w1",
    targetName: "jupyter.widget",
    data: { state: { a: 1 } },
  });
  await flush();
  scripted.emit({ sequence: 2, kind: 2, commId: "w1" });
  await flush();
  const postedCount = posted.length;
  await mounted(bridge, "w1");
  assertEquals(posted.length, postedCount);
  await bridge.dispose();
});

Deno.test("frames for never-mounted models are cached; unknown-model updates relay for a typed kernel error", async () => {
  const scripted = scriptedSocket();
  const { bridge, posted } = bridgeOver(scripted);
  // Mount another model to open the socket; w1's open is cached silently.
  await mounted(bridge, "other");
  scripted.emit({
    sequence: 1,
    kind: 0,
    commId: "w1",
    targetName: "jupyter.widget",
    data: { state: { a: 1 } },
  });
  await flush();
  // Updates for unknown models relay upstream (the kernel answers a typed
  // comm_not_found error in production); nothing is posted back for them.
  await bridge.handleRendererMessage({
    source: "maieutics-widget",
    type: "update",
    modelId: "unknown",
    state: { a: 2 },
  });
  assertEquals(scripted.sent.length, 1);
  assertEquals(
    posted.some((entry) => (entry as { modelId?: string }).modelId === "unknown"),
    false,
  );
  // The late mount for w1 answers from the cache.
  await mounted(bridge, "w1");
  assertEquals(posted.at(-1), {
    source: "maieutics-widget",
    type: "state",
    modelId: "w1",
    state: { a: 1 },
  });
  await bridge.dispose();
});

Deno.test("dispose closes the socket exactly once and settles the pump", async () => {
  const scripted = scriptedSocket();
  const { bridge } = bridgeOver(scripted);
  await mounted(bridge, "w1");
  await bridge.dispose();
  await bridge.dispose();
  assertEquals(scripted.closeCount, 1);
});

Deno.test("state for a model without a cached open posts under both sources", async () => {
  const scripted = scriptedSocket();
  const { bridge, posted } = bridgeOver(scripted);
  // An update frame for a model whose comm_open was lost to replay
  // truncation: the state must still reach either renderer.
  await bridge.handleRendererMessage({ source: "maieutics-ui", type: "mount", modelId: "u1" });
  scripted.emit({
    sequence: 1,
    kind: 1,
    commId: "u1",
    data: { method: "update", state: { a: 1 } },
  });
  await flush();
  assertEquals(
    posted.filter((entry) => (entry as { modelId?: string }).modelId === "u1")
      .map((entry) => (entry as { source: string }).source)
      .sort(),
    ["maieutics-ui", "maieutics-widget"],
  );
  await bridge.dispose();
});
