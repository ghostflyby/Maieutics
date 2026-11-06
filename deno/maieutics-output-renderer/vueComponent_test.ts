import { assertEquals, assertRejects, assertStringIncludes, assertThrows } from "@std/assert";
import { compileVueComponent, VueComponentError } from "./vueComponent.ts";
import { renderVueComponent } from "./vueSsr.ts";

export const CounterSfc = `<script setup lang="ts">
import { ref } from 'vue';
const count = ref<number>(0);
</script>
<template><button class="counter-btn" @click="count++">Count</button><span class="counter-display">{{ count }}</span></template>
<style scoped>.counter-btn { padding: 6px 14px; color: red; }</style>`;

Deno.test("official Vue compilers preserve setup, TS, events and scoped CSS", () => {
  const component = compileVueComponent("counter", CounterSfc);
  assertStringIncludes(component.code, 'Vue["ref"]');
  assertStringIncludes(component.css, "[data-v-counter]");
  assertEquals(compileVueComponent("counter", CounterSfc), component);
  assertThrows(
    () => compileVueComponent("bad", '<script setup>import x from "node:fs"</script><template/>'),
    VueComponentError,
  );
  assertThrows(
    () =>
      compileVueComponent(
        "bad",
        '<script setup>import("https://example.test/x")</script><template/>',
      ),
    VueComponentError,
  );
  assertThrows(() => compileVueComponent("bad", "<script setup>const x = "), VueComponentError);
});

Deno.test("isolated worker renders the actual Vue counter without filesystem or network permissions", async () => {
  const result = await renderVueComponent(compileVueComponent("counter", CounterSfc));
  assertStringIncludes(result, "data-v-counter");
  assertStringIncludes(result, ">0</span>");
  assertStringIncludes(result, "counter-btn");
  for (
    const source of [
      'Deno.readTextFileSync("/etc/passwd")',
      'Deno.env.get("HOME")',
      'await fetch("https://example.test")',
    ]
  ) {
    const component = compileVueComponent(
      "denied",
      `<script setup>${source}</script><template><p>denied</p></template>`,
    );
    await assertRejects(() => renderVueComponent(component), VueComponentError);
  }
});

Deno.test("cancellation terminates an owned component worker and a later request remains usable", async () => {
  const cancellation = new AbortController();
  const component = compileVueComponent(
    "loop",
    "<script setup>while (true) {}</script><template/>",
  );
  const pending = renderVueComponent(component, cancellation.signal);
  cancellation.abort(new DOMException("Cancelled", "AbortError"));
  await assertRejects(() => pending, DOMException);
  assertStringIncludes(
    await renderVueComponent(compileVueComponent("next", CounterSfc)),
    ">0</span>",
  );
});

Deno.test("authored worker postMessage cannot forge the private SSR reply channel", async () => {
  const component = compileVueComponent(
    "private",
    '<script setup>postMessage({html:"forged"});</script><template><p>actual</p></template>',
  );
  const html = await renderVueComponent(component);
  assertStringIncludes(html, ">actual</p>");
  assertEquals(html.includes("forged"), false);
});

Deno.test("the SSR deadline terminates non-cooperative setup without poisoning later renders", async () => {
  const component = compileVueComponent(
    "deadline",
    "<script setup>while (true) {}</script><template/>",
  );
  const error = await assertRejects(() => renderVueComponent(component), VueComponentError);
  assertEquals(error.code, "component_timeout");
  assertStringIncludes(
    await renderVueComponent(compileVueComponent("afterTimeout", CounterSfc)),
    ">0</span>",
  );
});
