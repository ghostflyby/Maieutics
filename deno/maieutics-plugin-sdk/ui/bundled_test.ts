/// <reference lib="deno.ns" />
/** Bundled-family source tests: registration ceiling and announcement embedding. */

import { assertEquals, assertThrows } from "@std/assert";
import { bindUiHost, JUPYTER_DISPLAY, model, registerFamily, type UiHost } from "./index.ts";
import { MAX_BUNDLED_SOURCE_BYTES } from "./runtime.ts";

function fakeHost(): UiHost {
  return {
    broadcast: () => Promise.resolve(),
    onComm: () => {},
  };
}

const ECHO_CONTRACT = {
  family: "test/echo",
  target: "maieutics.view/test.echo",
  displayMime: "application/vnd.maieutics.view+json",
  version: "1.0",
  commOpenData: (state: Record<string, unknown>) => ({ state }),
  commUpdateData: (key: string, value: unknown) => ({
    method: "update",
    state: { [key]: value },
    buffer_paths: [],
  }),
  decodeIncoming: () => undefined,
  announcement: (commId: string, state: Record<string, unknown>) => ({
    modelId: commId,
    viewFamily: "test/echo",
    version: "1.0",
    state,
  }),
} as const;

Deno.test("a bundled family's announcement embeds the component source", async () => {
  bindUiHost(fakeHost());
  registerFamily(ECHO_CONTRACT, {
    esmSource:
      `registerViewFamily("test/echo", { component: (p) => h("div", {}, p.state.title) });`,
    cssSource: ".test-echo { color: red; }",
  });
  const view = model("test/echo", { title: "hi" });
  const bundle = await view[JUPYTER_DISPLAY]();
  const announcement = bundle["application/vnd.maieutics.view+json"] as Record<string, unknown>;
  assertEquals(typeof announcement.esmSource, "string");
  assertEquals((announcement.esmSource as string).includes("registerViewFamily"), true);
  assertEquals(announcement.cssSource, ".test-echo { color: red; }");
  assertEquals(announcement.viewFamily, "test/echo");
});

Deno.test("oversized bundled sources are rejected at registration", () => {
  bindUiHost(fakeHost());
  const huge = "x".repeat(MAX_BUNDLED_SOURCE_BYTES + 1);
  assertThrows(
    () => registerFamily(ECHO_CONTRACT, { esmSource: huge }),
    Error,
    "the ceiling is",
  );
});

Deno.test("families without a bundled source announce without esm fields", async () => {
  bindUiHost(fakeHost());
  registerFamily(ECHO_CONTRACT);
  const { model } = await import("./index.ts");
  const raw = model("test/echo", {});
  const bundle = raw.announce()["application/vnd.maieutics.view+json"] as Record<string, unknown>;
  assertEquals("esmSource" in bundle, false);
  assertEquals("cssSource" in bundle, false);
});
