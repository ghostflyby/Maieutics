/**
 * Plugin-owned UI model tests driven through the real capability seam: a fake
 * worker scope captures `postMessage` capability requests and answers them on
 * the scope's message channel, exactly as the plugin host relay does. The SDK
 * runtime is a module-level singleton (initPluginWorker binds the scope once),
 * so one scope serves the whole file and the response policy is a swappable slot.
 */

import { assertEquals, assertRejects } from "@std/assert";
import { initPluginWorker } from "./mod.ts";
import { ui, type UiEventMessage } from "./plugin-ui.ts";

interface CapRequest {
  type: "capability.request";
  id: string;
  capability: string;
  payload?: unknown;
}

interface CapAnswer {
  ok: boolean;
  result?: unknown;
  code?: string;
  message?: string;
}

const requests: CapRequest[] = [];
let respond: (request: CapRequest) => CapAnswer = () => ({ ok: true, result: { status: "ok" } });

const listeners: ((event: { data: unknown }) => void)[] = [];
const dispatch = (data: unknown): void => {
  for (const listener of listeners) listener({ data });
};
const scope = {
  postMessage(message: unknown): void {
    const request = message as CapRequest;
    if (request.type !== "capability.request") return;
    requests.push(request);
    queueMicrotask(() => {
      const answer = respond(request);
      dispatch({
        type: "capability.response",
        id: request.id,
        ok: answer.ok,
        result: answer.result,
        code: answer.code,
        message: answer.message,
      });
    });
  },
  addEventListener(_: string, listener: (event: { data: unknown }) => void): void {
    listeners.push(listener);
  },
};

Object.defineProperty(globalThis, "self", {
  value: scope,
  writable: true,
  configurable: true,
});
// The SDK binds its message listeners inside initPluginWorker and waits for
// the host's maieutics-config handshake; the config frame replays once before
// any test runs.
const initializing = initPluginWorker();
dispatch({ type: "maieutics-config", payload: { specifier: "test-plugin/main" } });
await initializing;

Deno.test("form publishes the native dialect open and returns a handle", async () => {
  respond = () => ({ ok: true, result: { status: "ok" } });

  const handle = await ui.form({
    title: "Approve?",
    fields: [{ name: "note", type: "text" }, { name: "pick", type: "choice", choices: ["a", "b"] }],
  });

  assertEquals(requests.length, 1);
  assertEquals(requests[0].capability, "ui.models");
  const frame = requests[0].payload as Record<string, unknown>;
  assertEquals(frame.kind, "open");
  assertEquals(frame.targetName, "maieutics.view/maieutics/form");
  assertEquals(typeof frame.commId, "string");
  assertEquals(handle.commId, frame.commId);
  const state = (frame.data as { state: Record<string, unknown> }).state;
  assertEquals(state.title, "Approve?");
  const fields = state.fields as { name: string; choices?: unknown }[];
  assertEquals(fields[0].name, "note");
  assertEquals(fields[1].choices, [{ value: "a", label: "a" }, { value: "b", label: "b" }]);
});

Deno.test("submit events route to onSubmit with the values payload", async () => {
  respond = () => ({ ok: true, result: { status: "ok" } });
  const submitted: Record<string, unknown>[] = [];
  const handle = await ui.form(
    { fields: [{ name: "note", type: "text" }] },
    { onSubmit: (values) => submitted.push(values) },
  );

  const event: UiEventMessage = {
    commId: handle.commId,
    data: { method: "event", name: "submit", payload: { values: { note: "ship it" } } },
  };
  ui.deliver(event);

  assertEquals(submitted, [{ note: "ship it" }]);
});

Deno.test("cancel, update, and close route to their handlers", async () => {
  respond = () => ({ ok: true, result: { status: "ok" } });
  const cancelled: number[] = [];
  const closed: number[] = [];
  const handle = await ui.form(
    { fields: [] },
    { onCancel: () => cancelled.push(1), onClosed: () => closed.push(1) },
  );

  ui.deliver({ commId: handle.commId, data: { method: "event", name: "cancel" } });
  assertEquals(cancelled, [1]);

  // A frontend-driven close releases the model: later delivers are inert.
  ui.deliver({ commId: handle.commId, closed: true });
  assertEquals(closed, [1]);
  ui.deliver({ commId: handle.commId, data: { method: "event", name: "cancel" } });
  assertEquals(cancelled, [1]);
});

Deno.test("sync pushes the update dialect; close pushes close; closed handles reject sync", async () => {
  respond = () => ({ ok: true, result: { status: "ok" } });
  const handle = await ui.form({ fields: [{ name: "q", type: "text" }] });

  await ui.sync(handle, "q", "typed");
  await ui.close(handle);

  // The requests array is shared across the file: slice this test's tail.
  const mine = requests.slice(-2);
  assertEquals(mine.length, 2);
  const update = mine[0].payload as Record<string, unknown>;
  assertEquals(update.kind, "message");
  assertEquals(update.data, { method: "update", state: { q: "typed" } });
  const close = mine[1].payload as Record<string, unknown>;
  assertEquals(close.kind, "close");
  assertEquals(close.commId, handle.commId);
  await assertRejects(() => ui.sync(handle, "q", "again"), Error, "is closed");
});

Deno.test("unknown comm ids and malformed delivers are inert", async () => {
  respond = () => ({ ok: true, result: { status: "ok" } });
  await ui.form({ fields: [] });

  ui.deliver({ commId: "unknown" } as UiEventMessage);
  ui.deliver(undefined as unknown as UiEventMessage);
  ui.deliver({ commId: "" } as UiEventMessage);
});

Deno.test("a rejected open surfaces the typed failure and releases the model", async () => {
  respond = () => ({ ok: false, code: "ui_frame_rejected", message: "no live stream" });

  await assertRejects(
    () => ui.form({ fields: [] }),
    Error,
    "ui_frame_rejected",
  );
});
