# Kernel rendering demo

This demo uses the actual Notebook renderer, extension messaging bridge, executable API, and owned
offline Deno child. It needs no model request: the notebook contains a portable output snapshot.

Build the executable and extension from the repository root:

```sh
dotnet build Maieutics.slnx
deno task --config deno/maieutics-vscode/deno.json build
```

Load this extension in VS Code using its development workflow or a locally packaged VSIX. Start a
kernel in another terminal from the repository root, with private demo state:

```sh
render_demo_root="$(mktemp -d)"
printf 'Discovery file: %s/discovery.json\n' "$render_demo_root"
MAIEUTICS_PLUGINS_ROOT="$render_demo_root/plugins" \
MAIEUTICS_PLUGIN_APPROVALS="$render_demo_root/approvals.json" \
Maieutics/bin/Debug/net10.0/maieutics \
  --config deno/maieutics-vscode/demo/maieutics.json \
  --frontend-discovery "$render_demo_root/discovery.json" \
  --Maieutics:DataRoot "$render_demo_root/data"
```

On Windows, use the corresponding `maieutics.exe` and an explicitly created temporary directory. In
VS Code set `maieutics.discoveryFile` to the printed absolute path, then run **Maieutics: New
Session** to attach the extension. Open `ssr.maieuticsnb` from this directory. Its output should
show a clickable table of contents, custom heading anchors, a tip with an inline Markdown title, a
GitHub Note alert, a table, highlighted TypeScript, expandable details, filename tabs with line
numbers, and diff/focus annotations. Outline clicks scroll and focus the heading inside the output.
Switch a code tab to inspect the alternative JSON example. The fixture does not need to be executed.
The demo model configuration supplies an unused local placeholder endpoint; normal Agent turns need
a real provider configuration.

Open the same snapshot without an attached connection to inspect its native Markdown fallback.
Opening a snapshot never starts a kernel solely for rendering. Stop the demo kernel with Ctrl+C; its
child and staging directory are released with the host. Clear the temporary discovery setting when
returning to an ordinary workspace configuration.

## Automated verification

With a kernel running, this command also verifies the production extension bridge and actual
Notebook MIME renderer against that kernel, in an isolated VS Code instance:

```sh
MAIEUTICS_RENDERING_DEMO_DISCOVERY="/absolute/path/discovery.json" \
deno task --config deno/maieutics-vscode/deno.json test:vscode
```

The probe restores assistant text through the production serializer and waits for the renderer's
application acknowledgement after it replaces the output DOM. Standard controller tests also require
live text nodes to enter this same structured output route. Without the environment variable, the
standard webview test uses a snapshot fixture. Other coverage checks coalescing, stale-result
rejection, cancellation, native fallback, raw HTML remaining inert, real child startup/disposal, and
published NativeAOT rendering.

This initial demo updates complete text regions. Revisioned document patches, late TS bindings,
automatic producer subscriptions and frontend resource URL rewriting remain future stages in the
[design document](../../../docs/streaming-output-rendering-design.md).

## Template interpolation and producer updates

`template.maieuticsnb` contains frozen pending and ready rich-display snapshots. Open it with an
already attached kernel to inspect SSR. Without one, the source remains readable in native Markdown.
Restoring it starts no producer. The `pending` snapshot intentionally remains pending; it is saved
presentation data, not a live task registration.

To publish a live value, run this code through the explicitly selected `repl_execute` tool. It uses
the existing authorized REPL and display update channel; rendering never evaluates the producer.

```ts
const templateMime = "application/vnd.maieutics.template+json";
const templateSource =
  "## Report {#report}\n\nTotal {{ report.total.toFixed(2) }}\n\nLater independent prose.";
const templateId = "report-demo";
const publishTemplate = async (binding, update = false) => {
  await Deno.jupyter.display({
    [templateMime]: { version: 1, source: templateSource, sourceSealed: true, bindings: [binding] },
    "text/plain": binding.state === "ready"
      ? `Total ${binding.value.total}`
      : `Report: ${binding.state}`,
  }, { raw: true, display_id: templateId, update });
};
await publishTemplate({ name: "report", state: "pending" });
try {
  // Replace this data-producing operation with your authorized calculation or resource request.
  const report = await Promise.resolve({ total: 42 });
  await publishTemplate({ name: "report", state: "ready", value: report }, true);
} catch (error) {
  await publishTemplate({
    name: "report",
    state: error instanceof Error && error.name === "AbortError" ? "cancelled" : "failed",
  }, true);
  throw error;
}
```

Every update contains all bindings, not a partial merge. No fixed delay is needed. A fast producer
may be coalesced directly to ready by the presentation bridge. `undefined:true` without `value`
represents ready undefined; `pending` never substitutes for it and never selects a fallback branch.
Arbitrary scripts, function calls, directives and SFC execution are not enabled by template MIME.
Numeric `.toFixed()` is the initial explicit pure formatting operation.

The live VS Code suite also verifies this MIME through the production notebook serializer, applies a
binding-only update through the actual kernel, and checks that later independent prose remains. The
protocol remains a full-snapshot API; automatic subscriptions, cancellation settlement and local DOM
patching require the later document runtime.

## Live Vue counter

Use the explicitly selected `repl_execute` display API to publish the following live component. A
model can use the same producer pattern. Completed live assistant replies also support direct
Vue-in-Markdown through the default path described below; streaming/restored replies remain passive.
Rebuild both the executable and extension assets (`deno task build:media` as well as
`deno task build`) for this lane.

```ts
await Deno.jupyter.display({
  "application/vnd.maieutics.vue+json": {
    version: 1,
    format: "markdown",
    source: `<script setup lang="ts">
import { ref } from 'vue'
const count = ref<number>(0)
</script>

# Counter

<button class="counter" @click="count++">Count: {{ count }}</button>

::: tip
The button updates the hydrated Vue component locally.
:::

<style scoped>
.counter { padding: 6px 14px; border: 1px solid currentColor; border-radius: 8px; cursor: pointer; }
</style>`,
  },
  "text/plain": "Interactive Vue counter (live output only).",
}, { raw: true, display_id: "vue-counter-demo" });
```

Expect initial count 0 from kernel SSR, then 1 after clicking. The official Vue compiler handles
script setup, TypeScript, template interpolation, event handlers and scoped CSS. Only `vue` imports
are supported; setup must have deterministic initial state because SSR and hydration both run it.
External operations should run in the REPL and publish new snapshots. Changing the displayed source
resets client state and unmounts the old app when the replacement is ready. Identical redelivery
preserves client state; while SSR is pending, the existing app stays visible and interactive. Use
`format:"sfc"` with a complete `<template>` block for ordinary Vue SFC input. Async setup/Suspense
and external resources are not verified.

This MIME intentionally cannot be a frozen executable demo: notebook saving and reading remove its
source and retain the plain-text fallback. Reopening runs no component; redisplay from a live
producer. The live-kernel VS Code suite checks the real notebook MIME route, SSR, click/reactive
updates, scoped CSS and replacement/disposal cleanup. Revisioned source patches and automatic late
producer binding remain later work.

## Direct live assistant counter

With `rendering.assistantVersion:1`, a model can output Vue-in-Markdown directly in its answer;
`vue-counter.md` is a complete authoring example. Script/setup, markup and scoped styles must be
outside code fences. Fenced Vue remains an example. The controller waits for successful run
completion before selecting interactive rendering; partial/failed/cancelled replies remain passive.
No REPL call is needed for this self-contained component. External data producers still use the
explicit tools.

After rebuilding/updating both kernel and extension, reload the window and restart the kernel. A
fresh completed reply should show real buttons, initial zero, computed disabled decrement/reset and
reactive conditional hints. Source frontmatter is omitted; common VitePress color variables follow
the host theme. Opening `vue-counter.md` as an ordinary Markdown file or restoring a saved notebook
does not execute it. Notebook saving preserves answer source, then reopening uses passive rendering;
a new explicit live run/replay can recreate component state. This is not automatic execution of
saved code or progressive evaluation of partial scripts.
