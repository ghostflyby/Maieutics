import { createOutputRenderer } from "./mod.ts";

// This entry is bundled and run without package resolution, network, filesystem, or process grants.
using renderer = await createOutputRenderer();
const source = [
  "# Offline output",
  "",
  "[[toc]]",
  "",
  "## Contents {#contents}",
  "",
  "> [!NOTE] <img src=x>",
  "> Offline alert.",
  "",
  "::: tip **Advice** [jump](#contents)",
  "Shared SSR content.",
  ":::",
  "",
  "::: code-group",
  "```ts:line-numbers=7 {2} [report.ts]",
  "// [!code highlight]",
  "const total: number = 42;",
  "```",
  "",
  "```json [report.json]",
  '{ "total": 42 }',
  "```",
  ":::",
  "",
  "[Earlier reference][result]",
  "",
  "[result]: https://example.test/result",
  "",
  "<script>throw new Error('must remain text')</script>",
  "",
  "{{ globalThis.mustNotExecute() }}",
].join("\n");
const output = await renderer.render("offline", source);
for (
  const expected of [
    'id="offline-offline-output"',
    'class="custom-block tip"',
    'href="#offline-contents">Contents</a>',
    'class="custom-block note github-alert"',
    "&lt;img src=x&gt;",
    "<strong>Advice</strong>",
    'href="#offline-contents">jump</a>',
    "shiki",
    '<fieldset class="vp-code-group"',
    'name="offline-code-group-0"',
    'class="line highlighted" data-line="7"',
    'href="https://example.test/result"',
    "&lt;script&gt;",
    "{{ globalThis.mustNotExecute() }}",
  ]
) {
  if (!output.html.includes(expected)) throw new Error(`Offline renderer is missing ${expected}.`);
}
console.log(
  JSON.stringify({ status: "ok", htmlCharacters: output.html.length, headings: output.headings }),
);

const templateSource =
  "Total {{ report.total.toFixed(2) }}\n\nLater independent prose\n\n{{ empty }}";
const pendingTemplate = await renderer.renderTemplate("template", templateSource, {
  version: 1,
  bindings: [{ name: "report", state: "pending" }, {
    name: "empty",
    state: "ready",
    undefined: true,
  }],
});
if (pendingTemplate.expressions?.[0].state !== "pending") {
  throw new Error("Offline template must retain pending state.");
}
const readyTemplate = await renderer.renderTemplate("template", templateSource, {
  version: 1,
  bindings: [{ name: "report", state: "ready", value: { total: 42 } }, {
    name: "empty",
    state: "ready",
    undefined: true,
  }],
});
if (
  !readyTemplate.html.includes(">42.00</span>") ||
  !readyTemplate.html.includes("Later independent prose") ||
  readyTemplate.expressions?.[1].state !== "ready"
) throw new Error("Offline template did not reconcile ready values.");
console.log("Offline template interpolation passed.");

const vue = await renderer.renderVue(
  "vueoffline",
  '<script setup>import {ref} from "vue";const count=ref(0)</script><template><button @click="count++">Count</button><span>{{count}}</span></template><style scoped>button{color:red}</style>',
  "sfc",
);
if (!vue.html.includes(">0</span>") || !vue.vue?.css.includes("data-v-vueoffline")) {
  throw new Error("Offline Vue worker failed.");
}
console.log("Offline isolated Vue SSR passed.");

const assistant = await renderer.renderAssistant(
  "live",
  `---
title: Counter
initial: 0
---
<script setup>import { ref } from 'vue'; const count = ref(0);</script>

<button @click="count++">+</button><span>{{ count + $frontmatter.initial }}</span>

# {{ $frontmatter.title }}`,
);
if (
  !assistant.vue || !assistant.html.includes(">0</span>") ||
  assistant.html.includes("title: Counter")
) {
  throw new Error("Offline live assistant Vue failed");
}
console.log("Offline sealed assistant Vue passed.");
