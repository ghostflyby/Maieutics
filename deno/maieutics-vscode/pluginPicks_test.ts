/// <reference lib="deno.ns" />
/** Plugins-surface pick models: ranking, badges, actions, iframe view state. */

import { assertEquals, assertStrictEquals } from "@std/assert";
import { pluginPageUrl, type PluginPick, pluginPicks } from "./pluginPicks.ts";
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

Deno.test("empty pageUrl is dropped from picks without crashing", () => {
  const picks = pluginPicks([plugin({ pageUrl: "" })]);
  assertStrictEquals((picks[0] as PluginPick).pageUrl, undefined);
});

Deno.test("gateway page validation remains available without a component-family scaffold", () => {
  const valid = "http://127.0.0.1:9123/token/plugins/acme/";
  assertEquals(pluginPageUrl(valid), valid);
  for (
    const invalid of [
      "",
      "not a url",
      "file:///etc/passwd",
      "https://example.com/token/plugins/x/",
      "http://localhost/other/x/",
    ]
  ) {
    assertEquals(pluginPageUrl(invalid), undefined);
  }
});
