# Output renderer

Executable-owned rendering primitives for the
[streaming output design](../../docs/streaming-output-rendering-design.md). The first implementation
provides complete passive Markdown, explicit data-only templates and live Vue component snapshots
through Markdown-it, the `@mdit-vue` heading plugin, Shiki, and Vue SSR. The executable embeds
`bundle/service.js` and starts the rendering child on the first authenticated
`POST /v1/render/markdown` request. No user REPL or plugin host is needed. The
[Notebook demo](../maieutics-vscode/demo/README.md) exercises this service through the production
extension bridge with native Markdown fallback.

```ts
import { createOutputRenderer } from "./mod.ts";

using renderer = await createOutputRenderer();
const output = await renderer.render("message1", "# Result\n\n**Ready**", signal);
// output.html is derived passive HTML; output.headings describes scoped heading anchors.
```

The caller owns the renderer and its highlighter. Creation is asynchronous; disposal is idempotent.
Each render creates fresh Markdown environment state, so headings and reference definitions do not
leak between calls or documents. Render results are deterministic for identical source and document
identity. Source is capped at 200,000 UTF-16 code units and generated HTML at 4,000,000 code units.
Cancellation or disposal during SSR prevents the result from being returned. Parsing and
highlighting are synchronous within one bounded request; prompt cancellation of CPU work will
require the supervised service boundary.

## Content subset

Supported content includes headings, document-local anchor links, reference-style links, tables,
emphasis, code blocks, and `tip`, `info`, `warning`, `danger`, and `details` containers. `[[toc]]`
uses `@mdit-vue/plugin-toc` (top-level level-2/3 headings); custom anchors use `markdown-it-attrs`
with only `id` allowed and a heading-only adapter. IDs are document-prefixed; explicit IDs must
match `[\p{L}_][\p{L}\p{N}_.:-]{0,127}`. Invalid IDs fall back to the generated slug. Duplicates and
code-control collisions receive deterministic numeric suffixes in source order, so duplicate
explicit IDs cannot fail a streaming snapshot. Headings, TOC links, and header metadata share the
resulting ID; later headings update an earlier TOC on full-source re-render.

Ordinary containers and GitHub Alerts reuse extracted VitePress v1.6.4 source. Container titles
support inline Markdown, local links, and reference definitions, and details defaults to `Details`.
Quoted `[!TIP]`, `[!NOTE]`, `[!INFO]`, `[!IMPORTANT]`, `[!WARNING]`, `[!CAUTION]`, and `[!DANGER]`
markers use upstream recognition with escaped literal titles. Unknown markers remain blockquotes.
The [vendor manifest](vendor/vitepress/upstream.json) pins original file hashes and adaptation
notes; the MIT license is retained in source and the shipped bundle. Raw/v-pre containers are
excluded. VS Code handles local-fragment scrolling/focus inside the output's shadow root and removes
these handlers on replacement/disposal; other Shadow DOM hosts need an equivalent frontend adapter.

TypeScript, JavaScript, and JSON grammars and both GitHub light and dark themes are bundled
statically. Other code languages use escaped plain text without dynamically loading packages or
starting workers. `styles.css` provides host-scoped baseline styles using VS Code variables with
browser fallbacks.

Fences accept literal filename titles (`ts [report.ts]`), line numbers (`ts:line-numbers`,
`ts:line-numbers=7`, `ts:no-line-numbers`), and highlighted line lists or ranges (`ts{1,3-5}`).
These modifiers combine without disabling the selected grammar. Line-number starts must be positive
integers no greater than one billion; invalid starts use 1. Highlight ranges are stored as
intervals, never expanded into arrays based on the supplied endpoints.

Shiki's pinned `@shikijs/transformers` 2.5.0 implements VitePress 1.6.4's comment notations:
`[!code ++]`, `[!code --]`, `[!code focus]`, `[!code highlight]`, `[!code error]`, and
`[!code warning]`. Recognized comments become presentation classes, with host-scoped styles.

````md
::: code-group

```ts:line-numbers {1} [report.ts]
const total = 42;
```

```json [report.json]
{ "total": 42 }
```

:::
````

Code groups use deterministic, document-scoped native radio controls and CSS, with an accessible
fieldset legend and labels. They need no JavaScript, Vue hydration, network, or additional runtime
permissions. Only direct fenced code children become tabs; empty, nested, or mixed-content groups
retain their ordinary visible Markdown instead of hiding non-code content. Unclosed groups accept
later source through the existing complete-snapshot path. VS Code remembers up to 128 non-default
tab choices per output across snapshots when group ordinal, tab ordinal, and literal title still
match (titles longer than 512 characters are not retained). Structural reconciliation remains later
work. Other frontends can use the same HTML/CSS without this optional choice retention.

HTML, scripts, styles, Vue expressions, and directives in Markdown remain text. Executable link
schemes such as `command:` and `javascript:` are inactive. Filesystem snippet imports and include
directives are never installed. Product resource schemes are retained for a future frontend adapter;
resource retrieval and rewriting are not implemented here.

This is a selected VitePress-compatible content subset, not complete VitePress page semantics.
Frontmatter value exposure, automatic late-binding subscriptions, HTML patches and host resource
delivery remain later work. Complete Vue components use the separate explicit lane below.
Re-rendering with a later link definition corrects earlier references in a complete snapshot; stable
incremental nodes require the document runtime.

## Explicit data templates

`renderer.renderTemplate(documentId, source, {version:1, bindings, sourceSealed}, signal)` evaluates
`{{ expression }}` against an independently captured snapshot. The ordinary `render` method remains
passive. The kernel exposes this lane at `POST /v1/render/template`; it advertises
`rendering.templateVersion: 1`.
[Protocol details](../../docs/web-frontend-protocol.md#explicit-template-rendering-v1) define the
JSON shape, bounds, error states and corresponding rich-display MIME.

```ts
const result = await renderer.renderTemplate("report1", "Total {{ report.total.toFixed(2) }}", {
  version: 1,
  bindings: [{ name: "report", state: "ready", value: { total: 42 } }],
  sourceSealed: true,
});
```

Bindings distinguish pending, ready, failed and cancelled; ready undefined uses `undefined:true`
without `value`. Vue formats undefined/null as empty text, while pending shows an ellipsis. Missing
names are pending until sealing, when they fail as unresolved; explicitly pending bindings can
remain pending. Short-circuit evaluation reads only selected branches. Results include expression
state and actual dependencies. Subsequent complete snapshots re-render existing values without
re-executing a producer, including when only bindings changed.

The interpreter reuses Acorn for parsing and Vue for value formatting. It implements literal data,
record/array own-property reads, string length, optional chaining, arithmetic, comparison/strict
equality, unary `!`, `+`, `-`, `typeof`, logical/nullish operators, ternaries, and numeric
`toFixed`. It exposes no eval, assignment, functions, imports, host globals, prototype paths or
arbitrary calls. Code spans/fences and escapes stay literal; values are escaped text, never
reinterpreted as markup. Headings, TOCs and ordinary container titles share the captured values.
Incomplete syntax remains escaped source and reconciles on later snapshots; local errors leave other
content visible.

Limits: 128 bindings, 8,192 data values, 16 data nesting levels, 64,000 aggregate UTF-16 key/string
units, 256 interpolations, 2,048 units per expression, 256 interpreter steps and 32 traversal
levels. Non-JSON values, accessors, prototype keys and duplicate binding names are rejected. The
renderer retains no document or producer. Automatic producer subscriptions, cancellation settlement,
automatic SFC producer registration and document patches remain later work. Producers can publish
snapshots through the existing authorized REPL display API;
[the demo](../maieutics-vscode/demo/README.md) shows the pattern.

## Explicit live Vue components

`renderer.renderVue(documentId, source, "markdown" | "sfc", signal)` compiles complete
Vue-in-Markdown or a Vue SFC, executes SSR in a fresh permissionless worker, and returns initial
HTML plus a compiled component factory and scoped styles. Public mdit-vue component/SFC plugins,
Vue's official SFC/template/style compilers and Sucrase TypeScript erasure are reused. Code spans
and fences remain literal. Only static `vue` imports are supported; external imports/blocks and
style preprocessors are rejected. Ordinary `render` and data templates do not execute scripts.

The executable exposes `/v1/render/vue` with `rendering.vueVersion:1`. The live rich MIME is
`application/vnd.maieutics.vue+json`, `{version:1,format:"markdown"|"sfc",source}`. The frontend
hydrates using the same compiled factory and bundled Vue runtime, then unmounts on replacement or
disposal. Setup runs twice (SSR and hydration); initial state must be deterministic, and producers
belong in the authorized REPL. Async setup/Suspense and resource hydration are not verified. Live
source is removed on notebook saving and reading; supply a text fallback. See the
[protocol](../../docs/web-frontend-protocol.md#explicit-live-vue-rendering-v1) and
[runnable demo](../maieutics-vscode/demo/README.md#live-vue-counter).

## Default completed live assistant output

`renderer.renderAssistant(documentId, source, signal)` and `/v1/render/assistant` recognize complete
Vue-in-Markdown with authored top-level script blocks and return optional compiled Vue metadata.
Plain content stays passive. Code fences, inline/escaped/quoted examples and metadata never select
execution. Frontmatter uses the public `@mdit-vue/plugin-frontmatter` 2.1.3 adapter with explicit
uncached engines: bounded YAML/YML/JSON bodies are omitted, their values ignored and never
evaluated; JS/CoffeeScript engines are rejected. Common VitePress content color variables map to
host colors.

The live controller selects this lane only after successful turn completion; streaming partials and
failed/cancelled output remain passive. Saving/reopening removes live provenance and retains source.
The [protocol](../../docs/web-frontend-protocol.md#completed-live-assistant-rendering-v1) defines
the additive capability, endpoint and local MIME. Standalone SFCs keep their explicit format
selector. This enables default direct assistant components without running incomplete scripts or
producers.

## Why compose the underlying packages

The prototype verified `vitepress@1.6.4` rendering in Deno 2.9.8, but importing its public entry
also loads site-build dependencies. A direct `deno bundle` encountered Vite's optional
`lightningcss` dependency. Its Markdown renderer also caches one parser and highlighter globally.
The standalone implementation composes public ecosystem packages with independent ownership and
static grammar imports, plus the small licensed upstream container/alert extraction described above.
It does not suppress bundler diagnostics or add unused native build dependencies.

## Verification

The renderer's model-facing instructions live in `system-prompt.json`, a static
`extensions.SystemPrompt` declaration. The kernel composition root registers it through the generic
system-instruction contribution pipeline. It is embedded separately from `bundle/service.js`;
editing this prompt does not require rebuilding the SSR JavaScript bundle. External plugins can
provide the same contribution under declaration approval (ADR 0049).

Run from this directory after installing the workspace's locked dependencies:

```sh
deno task check
deno task test
deno task build
git diff --exit-code --stat -- bundle/
deno task smoke:offline
```

The build produces an ignored `dist/smoke.js` bundle containing the rendering engine, bundled
grammars, and a representative fixture. The smoke command disables npm and remote-module resolution
and denies network, child-process, filesystem read, and filesystem write access. It validates SSR,
containers, TOC/custom anchors, alerts, highlighting, reference links, and inert document code. The
generated bundle is an integration probe, not a shipped executable resource. The build also first
builds a browser-ESM worker using the separate `deno.worker.json`/`deno.worker.lock`, embeds its
JavaScript source into committed `bundle/vue_worker_source.js`, and regenerates the committed
`bundle/service.js` embedded by the executable. The separate worker lock prevents its standalone
bundle graph from pruning workspace dependencies. Offline smoke also executes real Vue SSR. Rebuild
and commit it whenever its source or dependencies change. CI uses Deno 2.9.6 and rejects a tracked
bundle diff after rebuilding, before running offline smoke; use the pinned runtime to reproduce the
check.

## Deno permissions

Source tests allow reads of `NODE_ENV` (Vue) and `VSCODE_TEXTMATE_DEBUG` (TextMate) from the
environment. The bundled smoke requires only `VSCODE_TEXTMATE_DEBUG`; Deno's bundler substitutes
`NODE_ENV` during the build. Neither command grants runtime network, process launch, or filesystem
access. Data-template interpretation uses the same permissions and executes no producer code. Vue
SSR adds `--unstable-worker-options` and creates a worker with `deno.permissions:"none"`; it adds no
parent grants. A private MessagePort carries the result, with a five-second deadline and
deterministic termination on cancellation/disposal. The trusted prebundled worker source is a
JavaScript module data URL, not a filesystem module or remote import. Browser execution follows ADR
0038/0047 live-output trust; the worker restriction is not a browser sandbox. Dependency
installation and bundling require package-cache access and, on a cold cache, registry access; the
build reads the vendored license to add the distribution notice and writes only its output
directories.

The supervised service additionally reads `MAIEUTICS_RENDERER_CALLBACK`, `MAIEUTICS_RENDERER_TOKEN`,
and `MAIEUTICS_RENDERER_NONCE`. Its built-in policy permits listening on `127.0.0.1:0` and calling
the kernel's concrete loopback frontend port. The kernel overlays app and workspace permission
layers, renders that captured effective policy before launch, and clears the inherited environment.
It provides only renderer variables, a private `DENO_DIR`, and Windows `SYSTEMROOT` when needed.
Readiness and internal render requests use authenticated HTTP. No binary data or conversation state
travels through stdout. The child belongs to the executable's presentation scope independently of
Agent runs and frontend subscribers. Its private module/cache directory lives under the configured
data root's `.rendering` directory and is removed on deterministic service disposal.

## Process control and recovery

Permissionless workers isolate I/O, not every process-control effect. Deno 2.9.6/2.9.8 worker exit
ends the worker, but `Deno.kill(Deno.pid, "SIGKILL")` can end the entire renderer as a permitted
self-signal. This is a known runtime constraint, not a claim of a complete untrusted-code sandbox.
Real child-process regressions pin worker exit behavior, self-signal recovery and rejection of a
signal to a separately owned kernel PID. OS-process isolation below the renderer remains future
work.

The executable observes each generation continuously, warns on unexpected death, invalidates its
port/readiness and fails the affected request with typed unavailable. A later request lazily starts
a new generation with fresh nonce/readiness, without replaying the failed render or a producer.
Transient launch failures do not latch rendering off. Retirement and host shutdown join owned
observers/processes, drain admitted requests and remove staging state. API and notebook versions
remain unchanged. See the design for cancellation and concurrency semantics.
