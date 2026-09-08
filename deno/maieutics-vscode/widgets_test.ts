/// <reference lib="deno.ns" />
/**
 * Widget bridge tests: state caching and renderer↔comm routing against a
 * scripted socket, with a captured post sink.
 */

import { assertEquals } from "@std/assert";
import { WidgetBridge, type WidgetCommSocket } from "./widgets.ts";
import { CommKind, type CommMessage } from "../shared/comm_codec.ts";
import type { CommFrame } from "./protocol.ts";

/** A scripted socket whose uplink sends are recorded and whose downlink
 * frames are delivered by calling `emit`. */
function scriptedSocket() {
  const sent: CommMessage[] = [];
  const queue: CommFrame[] = [];
  let wake: (() => void) | undefined;
  let closed = false;
  const messages: AsyncGenerator<CommFrame> = async function* () {
    while (true) {
      if (queue.length > 0) {
        yield queue.shift()!;
        continue;
      }
      if (closed) return;
      await new Promise<void>((resolve) => {
        wake = resolve;
      });
    }
  }();

  return {
    sent,
    emit: (frame: CommFrame) => {
      queue.push(frame);
      wake?.();
      wake = undefined;
    },
    socket: {
      send: (message: CommMessage) => sent.push(message),
      messages,
      close: () => {
        closed = true;
        wake?.();
        wake = undefined;
      },
    } satisfies WidgetCommSocket,
  };
}

/** Lets the bridge's background pump drain the queued frame. */
function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 5));
}

function bridgeOver(socket: ReturnType<typeof scriptedSocket>) {
  const posted: unknown[] = [];
  const bridge = new WidgetBridge({
    connect: () => Promise.resolve(socket.socket),
    post: (message) => posted.push(message),
    log: () => {},
  });
  return { bridge, posted };
}

Deno.test("bridge caches comm_open state and answers renderer mounts", async () => {
  const scripted = scriptedSocket();
  const { bridge, posted } = bridgeOver(scripted);
  await bridge.handleRendererMessage({ type: "mount", modelId: "w1" });

  scripted.emit?.({
    kind: "comm",
    sequence: 1,
    message: {
      kind: CommKind.Open,
      commId: "w1",
      targetName: "jupyter.widget",
      data: { state: { value: 3 }, buffer_paths: [] },
      buffers: [],
    },
  });
  await flush();
  assertEquals(posted.length, 1);
  assertEquals(
    posted[0],
    { source: "maieutics-widget", type: "state", modelId: "w1", state: { value: 3 } },
  );
  await bridge.dispose();
});

Deno.test("bridge merges updates, relays renderer edits upstream, and drops unknown models", async () => {
  const scripted = scriptedSocket();
  const { bridge, posted } = bridgeOver(scripted);
  await bridge.handleRendererMessage({ type: "mount", modelId: "w1" });
  scripted.emit?.({
    kind: "comm",
    sequence: 1,
    message: {
      kind: CommKind.Open,
      commId: "w1",
      targetName: "jupyter.widget",
      data: { state: { value: 3, description: "n" }, buffer_paths: [] },
      buffers: [],
    },
  });
  await flush();

  // Renderer edit: merged locally and forwarded as an ipywidgets update.
  await bridge.handleRendererMessage({ type: "update", modelId: "w1", state: { value: 4 } });
  assertEquals(scripted.sent.length, 1);
  assertEquals(scripted.sent[0].kind, CommKind.Message);
  assertEquals(scripted.sent[0].commId, "w1");
  assertEquals(scripted.sent[0].data, { method: "update", state: { value: 4 }, buffer_paths: [] });

  // Kernel-side update merges over the cached state.
  scripted.emit?.({
    kind: "comm",
    sequence: 2,
    message: {
      kind: CommKind.Message,
      commId: "w1",
      data: { method: "update", state: { value: 5 } },
      buffers: [],
    },
  });
  await flush();
  const last = posted.at(-1) as { state: Record<string, unknown> };
  assertEquals(last.state, { value: 5, description: "n" });

  // Unknown model: the renderer gets nothing and nothing is forwarded.
  const before = posted.length;
  await bridge.handleRendererMessage({ type: "update", modelId: "ghost", state: { value: 0 } });
  assertEquals(posted.length, before);
  await bridge.dispose();
});

Deno.test("bridge answers a late mount with the cached state", async () => {
  const scripted = scriptedSocket();
  const { bridge, posted } = bridgeOver(scripted);
  await bridge.handleRendererMessage({ type: "mount", modelId: "w1" });
  scripted.emit?.({
    kind: "comm",
    sequence: 1,
    message: {
      kind: CommKind.Open,
      commId: "w1",
      targetName: "jupyter.widget",
      data: { state: { value: 7 }, buffer_paths: [] },
      buffers: [],
    },
  });
  await flush();

  // A second output mounts after the open arrived.
  posted.length = 0;
  await bridge.handleRendererMessage({ type: "mount", modelId: "w1" });
  assertEquals(
    posted[0],
    { source: "maieutics-widget", type: "state", modelId: "w1", state: { value: 7 } },
  );
  await bridge.dispose();
});
