/**
 * Form family tests: definition normalization, announcement shape, submit
 * routing, and the module-level bindUiHost fake-host flow (mirrors
 * widgets/e2e_test.ts).
 */

import { assertEquals } from "@std/assert";
import {
  bindUiHost,
  displayable,
  form,
  FORM_FAMILY,
  formState,
  initialValues,
  JUPYTER_DISPLAY,
  normalizeChoices,
  registerFamily,
  type UiHost,
} from "./index.ts";

Deno.test("formState normalizes choices and seeds values from defaults", () => {
  const state = formState({
    title: "Approve?",
    fields: [
      { name: "q", type: "text", default: "hi" },
      { name: "pick", type: "choice", choices: ["a", { label: "B", value: "b" }] },
      { name: "ok", type: "boolean" },
    ],
    submitLabel: "Go",
  });
  assertEquals(state.title, "Approve?");
  assertEquals(state.submitLabel, "Go");
  const pick = state.fields.find((field) => field.name === "pick");
  assertEquals(pick?.choices, [{ value: "a", label: "a" }, { value: "b", label: "B" }]);
  assertEquals(state.values, { q: "hi", pick: "", ok: false });
});

Deno.test("normalizeChoices maps plain strings to label=value pairs", () => {
  assertEquals(normalizeChoices(["x"]), [{ value: "x", label: "x" }]);
});

Deno.test("initialValues seeds booleans false and the rest empty strings", () => {
  assertEquals(
    initialValues({
      fields: [{ name: "a", type: "number" }, { name: "b", type: "boolean", default: true }],
    }),
    { a: "", b: true },
  );
});

function fakeHost() {
  const calls: { messageType: string; content: Record<string, unknown> }[] = [];
  const handlers = new Map<string, (message: unknown) => void>();
  const host: UiHost = {
    broadcast: (messageType, content) => {
      calls.push({ messageType, content });
      return Promise.resolve();
    },
    onComm: (event, handler) => {
      // The runtime subscribes to "msg" and "close"; route both.
      handlers.set(event, handler as (message: unknown) => void);
    },
  };
  const deliver = (message: unknown) => handlers.get("msg")?.(message);
  return { calls, host, deliver };
}

Deno.test("bindUiHost binds a fresh runtime per host with the form family", () => {
  const { host } = fakeHost();
  const runtime = bindUiHost(host);
  assertEquals(runtime.hasFamily(FORM_FAMILY), true);
  const other = bindUiHost(fakeHost().host);
  assertEquals(other === runtime, false);
  assertEquals(other.hasFamily(FORM_FAMILY), true);
});

Deno.test("form() displays through the native mime announcement", async () => {
  const { calls, host } = fakeHost();
  bindUiHost(host);
  const view = form({ fields: [{ name: "q", type: "text" }] });
  assertEquals(typeof view[JUPYTER_DISPLAY], "function");
  const bundle = await view[JUPYTER_DISPLAY]();
  const announcement = bundle["application/vnd.maieutics.view+json"] as Record<
    string,
    unknown
  >;
  assertEquals(announcement.viewFamily, FORM_FAMILY);
  assertEquals(announcement.version, "1.0");
  assertEquals(typeof announcement.modelId, "string");
  // state rides the announcement for snapshot-restore rendering
  const state = announcement.state as Record<string, unknown>;
  assertEquals(Array.isArray(state.fields), true);
  // the comm_open for the model was broadcast with the native target
  const open = calls.find((call) => call.messageType === "comm_open");
  assertEquals(open?.content.target_name, "maieutics.view/maieutics/form");
});
Deno.test("submit flows to onSubmit through the bound comm subscription", () => {
  const { host, deliver } = fakeHost();
  bindUiHost(host);
  const submitted: Record<string, unknown>[] = [];
  const view = form(
    { fields: [{ name: "q", type: "text", default: "x" }] },
    { onSubmit: (values) => submitted.push(values) },
  );
  deliver({
    commId: view.commId,
    data: { method: "event", name: "submit", payload: { values: { q: "typed" } } },
    buffers: [],
  });
  assertEquals(submitted, [{ q: "typed" }]);
});

Deno.test("cancel flows to onCancel", () => {
  const { host, deliver } = fakeHost();
  bindUiHost(host);
  const cancelled: string[] = [];
  const view = form({ fields: [] }, { onCancel: () => cancelled.push("cancel") });
  deliver({ commId: view.commId, data: { method: "event", name: "cancel" }, buffers: [] });
  assertEquals(cancelled, ["cancel"]);
});

Deno.test("registerFamily admits custom native families", () => {
  const { host } = fakeHost();
  const runtime = bindUiHost(host);
  registerFamily({
    family: "test/echo",
    target: "maieutics.view/test.echo",
    displayMime: "application/vnd.maieutics.view+json",
    version: "1.0",
    commOpenData: (state) => ({ state }),
    commUpdateData: (key, value) => ({
      method: "update",
      state: { [key]: value },
      buffer_paths: [],
    }),
    decodeIncoming: () => undefined,
    announcement: (commId, state) => ({
      modelId: commId,
      viewFamily: "test/echo",
      version: "1.0",
      state,
    }),
  });
  assertEquals(runtime.hasFamily("test/echo"), true);
});

Deno.test("displayable wraps any model with the display symbol", async () => {
  const { host } = fakeHost();
  const runtime = bindUiHost(host);
  const raw = runtime.create(FORM_FAMILY, { fields: [], values: {} });
  const wrapped = displayable(raw);
  assertEquals(typeof wrapped[JUPYTER_DISPLAY], "function");
  const bundle = await wrapped[JUPYTER_DISPLAY]();
  assertEquals(
    typeof bundle["application/vnd.maieutics.view+json"],
    "object",
  );
});
