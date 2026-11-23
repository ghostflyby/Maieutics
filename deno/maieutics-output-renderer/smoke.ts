import { createOutputRenderer } from "./mod.ts";

// Exercise the bundled engines with package resolution and I/O denied.
using renderer = await createOutputRenderer();
const markdown = await renderer.render(
  "offline",
  "~~~ts\nconst answer: number = 42;\n~~~\n\n$x^2$",
);
if (!markdown.html.includes("shiki") || !markdown.html.includes("<svg ")) {
  throw new Error("The offline bundle could not highlight TypeScript or render math.");
}

const template = await renderer.renderTemplate("template", "Total {{ total.toFixed(2) }}", {
  version: 1,
  bindings: [{ name: "total", state: "ready", value: 42 }],
});
if (!template.html.includes(">42.00</span>")) {
  throw new Error("The offline template engine could not interpolate a bound expression.");
}

const vue = await renderer.renderVue(
  "vueoffline",
  '<script setup>import {ref} from "vue";const count=ref(0)</script><template><span>{{count}}</span></template>',
  "sfc",
);
if (!vue.vue || !vue.html.includes(">0</span>")) {
  throw new Error("The offline Vue engine could not compile and render a component.");
}

const assistant = await renderer.renderAssistant(
  "live",
  '<script setup>import {ref} from "vue";const count=ref(42);</script>\n\n# {{ count }}',
);
if (!assistant.vue || !assistant.html.includes(">42</h1>")) {
  throw new Error("The offline assistant engine could not render executable Markdown.");
}

console.log(
  "Offline bundle rendered highlighted Markdown, math, templates, Vue and assistant output.",
);
