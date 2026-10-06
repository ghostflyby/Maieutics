/// <reference lib="deno.ns" />
/** Plugins-surface pick models: ranking, badges, actions, iframe view state. */

import { assertEquals, assertStrictEquals } from "@std/assert";
import { iframeViewState, type PluginPick, pluginPicks } from "./pluginPicks.ts";
import type { PluginInfo } from "./protocol.ts";

function plugin(overrides: Partial<PluginInfo> = {}): PluginInfo {
  return {
    id: "acme-tools",
    name: "acme-tools",
    approvalState: "Approved",
    form: null,
    formError: null,
    pageUrl: null,
    ...overrides,
  };
}

Deno.test("pluginPicks ranks forms, then pages, then the rest", () => {
  const picks = pluginPicks([
    plugin({ id: "b-plain", name: "b-plain", approvalState: "PendingApproval" }),
    plugin({ id: "z-page", name: "z-page", pageUrl: "http://127.0.0.1:9/t/plugins/z-page/" }),
    plugin({ id: "a-form", name: "a-form", form: { fields: [{ name: "q", type: "text" }] } }),
  ]);
  assertEquals(picks.map((pick) => pick.pluginId), ["a-form", "z-page", "b-plain"]);
});

Deno.test("non-approved plugins carry no action even with form and page", () => {
  const picks = pluginPicks([
    plugin({
      approvalState: "PendingApproval",
      form: { fields: [] },
      pageUrl: "http://127.0.0.1:9/t/plugins/x/",
    }),
  ]);
  assertEquals(picks.length, 1);
  const pick = picks[0] as PluginPick;
  assertEquals(pick.publishForm, false);
  assertEquals(pick.pageUrl, undefined);
  assertEquals(pick.description.includes("pending approval"), true);
});

Deno.test("approved plugins expose their actions", () => {
  const picks = pluginPicks([
    plugin({
      form: { fields: [{ name: "q", type: "text" }] },
      pageUrl: "http://127.0.0.1:9/t/plugins/x/",
    }),
  ]);
  const pick = picks[0] as PluginPick;
  assertEquals(pick.publishForm, true);
  assertEquals(pick.pageUrl, "http://127.0.0.1:9/t/plugins/x/");
});

Deno.test("unknown approval states read as pending", () => {
  const picks = pluginPicks([plugin({ approvalState: "SomethingNew" })]);
  assertEquals((picks[0] as PluginPick).description.includes("pending approval"), true);
});

Deno.test("iframeViewState accepts only loopback gateway plugin paths", () => {
  assertEquals(
    iframeViewState("http://127.0.0.1:9123/tok/plugins/acme/", "m1"),
    {
      modelId: "m1",
      viewFamily: "maieutics/iframe",
      version: "1.0",
      state: { url: "http://127.0.0.1:9123/tok/plugins/acme/" },
    },
  );
  assertEquals(
    iframeViewState("http://localhost:9123/tok/plugins/acme/", "m1")?.viewFamily,
    "maieutics/iframe",
  );
  // Foreign hosts, non-plugin paths, and non-http schemes are refused.
  assertEquals(iframeViewState("http://example.com/tok/plugins/acme/", "m1"), undefined);
  assertEquals(iframeViewState("https://example.com/tok/plugins/acme/", "m1"), undefined);
  assertEquals(iframeViewState("http://127.0.0.1:9123/other/acme/", "m1"), undefined);
  assertEquals(iframeViewState("file:///etc/passwd", "m1"), undefined);
  assertEquals(iframeViewState("", "m1"), undefined);
});

Deno.test("empty pageUrl is dropped from picks without crashing", () => {
  const picks = pluginPicks([plugin({ pageUrl: "" })]);
  assertStrictEquals((picks[0] as PluginPick).pageUrl, undefined);
});
