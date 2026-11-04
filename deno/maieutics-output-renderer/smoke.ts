import { createOutputRenderer } from "./mod.ts";

// This entry is bundled and run without package resolution, network, filesystem, or process grants.
using renderer = await createOutputRenderer();
const source = [
  "# Offline output",
  "",
  "::: tip",
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
