# Streaming Output Rendering with VitePress

Status: Passive SSR integrated with the kernel and actual Notebook renderer; upstream TOC, anchors,
containers, and alerts integrated. Explicit data-only template snapshots and interpolation are implemented;
explicit live Vue components compile, SSR and hydrate with scoped styles. Bounded YAML/JSON
frontmatter now supplies `$frontmatter` paths and readonly Vue template data. Successful completed live
assistant replies now select Vue-in-Markdown automatically when they contain authored top-level scripts.
Connected history restoration can renew this selection after verifying exact assistant source against
the kernel-owned committed transcript. Progressive Vue template prefixes now preserve one client setup scope after a complete script.
The broader document runtime and revisioned presentation protocol remain proposed. Append-only passive previews now commit
completed SSR prefixes while newer text continues arriving. Built-in per-run instructions now
advertise the supported output syntax and completion boundary to the model.

Date: 2026-11-04

Last updated: 2026-11-10

Related: [Frontend protocol](web-frontend-protocol.md),
[custom UI framework](custom-ui-framework-design.md),
[ADR 0023](architecture/decisions/0023-custom-web-frontend-protocol-and-vscode-notebook.md),
[ADR 0026](architecture/decisions/0026-virtual-resource-urls.md), and
[ADR 0038](architecture/decisions/0038-universal-custom-ui-framework.md).

## Purpose and direction

Replace the primary assistant Markdown output renderer with a VitePress-based content pipeline.
Rendering runs in a kernel-owned Deno service so VS Code and other frontends can share server-rendered
output. Frontends apply presentation updates and provide host-specific interaction and resource routing.

VitePress supplies content parsing and compilation capabilities. Maieutics supplies a document runtime
for incomplete source, late data bindings, cross references, render scheduling, and recoverable updates.
The standard VitePress site build does not supply this runtime.

The agreed direction is kernel-side Deno rendering, SSR as the primary rendering model, and shared
output across frontends. The passive content subset and stateless kernel API are implemented below.
Complete Vue SFC/Markdown authoring is implemented through an explicit live-output lane. The document
runtime and revisioned update operations remain proposed and require the validation stages below. The initial demo API has its own normative frontend contract.

### Content scope, not a site runtime (2026-11-10)

The rendering unit is one output document in a host frontend, not a page in a renderer-owned site.
VitePress site generation, page collections, routing, site navigation/default-theme layouts, SEO
and document `<head>` management are intentionally out of scope, rather than compatibility gaps
waiting to be filled. The host owns its enclosing document, navigation and presentation shell.
One output must not change the enclosing document's title, metadata, scripts or styles through
frontmatter `head` declarations. Such fields can remain ordinary inert data.

Frontmatter is useful as output-local presentation data independent of site features. Future data
helpers, if needed, should expose the current output's data without inventing a site/page context.
Cross-references within an output and late TS bindings remain core work. If a frontend later groups
multiple output documents, that needs an explicit frontend/document navigation design; it does not
implicitly require adopting VitePress's site runtime or ownership of the host `<head>`.

## Existing integration boundary

The VS Code frontend currently projects events into ordered timeline entries in
`deno/maieutics-vscode/turnTimeline.ts`. `timelineOutputs.ts` assigns independent output identities and
retains unchanged output payloads. `media/turnRenderer.js` immediately renders changed assistant text
with VS Code's default Markdown renderer, with the protections in `markdownSafety.js`, then requests
shared SSR through the extension's existing connection when available.
Both live controller output and snapshot restoration use `serializer.ts`'s
`timelineNotebookOutput`: assistant text must carry the structured turn MIME, just like other timeline
nodes. A direct `text/markdown` item bypasses this bridge and cannot receive shared SSR. Older
Markdown-node output metadata remains readable for snapshot compatibility.

The renderer replaces assistant Markdown inside these existing output boundaries. Successful live
turn completion now selects the assistant endpoint described below; streaming partials and restored
notebook snapshots decode through the passive lane; connected history can renew assistant provenance
only after canonical transcript verification as described below.
Tool activity, permitted reasoning summaries and rich MIME values retain their owning contracts.
Built-in forms now use ordinary Vue components and a separate data-only contract (ADR 0050);
the old family/widget engine is retired.

The first rendering primitive is now implemented in
[`deno/maieutics-output-renderer`](../deno/maieutics-output-renderer/README.md), a Deno workspace member.
It renders complete passive Markdown snapshots and heading metadata with independent parser and
highlighter ownership. The executable embeds its offline service bundle and starts an owned Deno
child lazily for authenticated rendering requests. The VS Code bridge applies complete SSR snapshots
only to the current output revision; unsupported or failed rendering retains native Markdown.
Opening an offline notebook never starts a kernel solely for rendering.

## Ownership and data flow

```text
Agent events and source text ----> executable presentation projection
                                              |
                                              v
                                  Deno document runtime
                                  | parse and compile
explicit producer results ------> | bind data and invalidate dependents
                                  | render presentation units with Vue SSR
                                              |
                                              v
                                  executable presentation API
                                              |
                                              v
                                  frontend bridge and DOM updates
```

`Maieutics.Frontend` owns the product presentation projection and frontend contract. Deno process
supervision and permissions use the existing executable-owned layers. Rendering types, Vue objects,
and HTML patches do not enter `Maieutics.Agent`, provider contracts, or canonical history. No new .NET
assembly is required just to represent this boundary.

The executable remains authoritative for the conversation and presentation revision it publishes.
Deno owns bounded, disposable derived state that can be reconstructed from retained source and
authorized producer results. Rendering must not execute through or depend on the user's REPL. An authorized REPL producer
may publish data snapshots through the existing rich-display lane; the renderer never calls it back.

## Reusable ecosystem capabilities

| Capability | Candidate | Integration role and limit |
|---|---|---|
| VitePress content semantics | Selected upstream plugins behind a local adapter | Source extraction of containers, alerts, fence wrappers and line numbers works offline. Patch escaping, identity and host behavior; treat code markup, client handlers and CSS as one contract. The public `createMarkdownRenderer` remains experimental and owns global state. |
| Markdown and Vue integration | `@mdit-vue` component, SFC, frontmatter, headers, title, and TOC plugins | Prefer public packages. Frontmatter needs explicit data-only parsers and uncached options; attrs needs an allowlist. Component/SFC parsing supplies neither late binding nor incremental document lifetime. |
| Vue compilation | `vue/compiler-sfc` and Vue template compilers | Compile complete components or templates and support binding transforms. Import resolution, TS execution, resources, and producer lifetime remain separate concerns. |
| SSR | `vue/server-renderer` | Render a presentation unit against a captured data snapshot. Streaming one SSR result does not update HTML already delivered. |
| Code highlighting | Shiki and `@shikijs/stream` | Reuse a highlighter and incrementally highlight code; account for recall tokens when later context changes earlier highlighting. |
| Content styling | VitePress content styles and Shiki theme variables | Bundle selected styles and adapt variables to the host. Default-theme components may depend on VitePress page context and browser behavior. |
| DOM application | Idiomorph or morphdom | Merge HTML inside an explicitly owned boundary. Focus, selection, expansion, and scrolling still need host verification. |
| Incomplete Markdown | Markstream and `stream-markdown-parser` | Evaluate partial syntax handling and structured nodes. Their parser composition is not established as equivalent to the selected VitePress pipeline. |

The initial implementation uses direct ecosystem composition: Markdown-it 14.3.1, `@mdit-vue` 2.1.3
heading metadata, frontmatter extraction, TOC and shared utilities, markdown-it-anchor 9.2.0, markdown-it-attrs 4.3.1,
markdown-it-container 4.0.0, Shiki and `@shikijs/transformers` 2.5.0, and Vue 3.5.13 SSR. It supports a selected passive content subset rather than the full VitePress page
compiler. The explicit Vue lane additionally reuses public component/SFC plugins, Vue
`@vue/compiler-sfc` 3.5.13, Sucrase 3.35.0 for TypeScript erasure, and Vue runtime-dom 3.5.13
for hydration. The complete-snapshot update client is implemented; the document runtime and incremental
HTML patch client remain to be implemented.
Markstream is a comparison candidate, not an additional authoritative parser running beside VitePress.

Package reuse must be checked in the supported Deno runtime with packaged, offline dependencies.
Node compatibility, compiler imports, Shiki assets, stylesheet packaging, and notebook CSP compatibility
are acceptance conditions rather than assumptions. Vite is useful for building shipped assets; the
production design must not require a development server or site rebuild for each text delta.

The [upstream extraction audit](vitepress-upstream-reuse.md) pins VitePress v1.6.4 at commit
`1fc537b78cda287fa23c1129a815ad9455fd8106`. Twenty-three offline checks establish that selected
plugins and public ecosystem packages can be bundled without the site entry. They also reproduce
title/attribute escaping gaps, default JavaScript-frontmatter execution, and a global source cache.
These require explicit policy adapters. The audit recommends public packages first, then a small
licensed vendor subset with tracked patches. The first production migration now uses the public TOC
and attrs packages plus extracted ordinary containers and GitHub Alerts, with the adapters below.
Code-group markup, CSS, and client behavior remain the existing coherent native-radio contract;
migrating the remaining code family is separate work.

### Initial implementation evidence

The direct `vitepress@1.6.4` entry rendered headings, containers, and highlighted TypeScript under Deno
2.9.8, but bundling that entry reached Vite's optional `lightningcss` dependency and failed. The entry
also exposes a globally cached parser and highlighter. The current implementation avoids that entry
and owns independent instances of the underlying packages rather than adding unused native build
dependencies or suppressing the diagnostics.

The composed core produces a single JavaScript smoke bundle with statically included grammars and
themes. The offline smoke disables npm and remote-module resolution and denies network, filesystem,
and child-process access. TextMate requires one allowlisted environment read,
`VSCODE_TEXTMATE_DEBUG`; source tests also require Vue's `NODE_ENV`. The bundled production output
does not require the latter. The kernel service derives launch permissions through its owning
effective policy.

The supported content includes scoped headings and anchors, reference links, tables, emphasis,
TypeScript/JavaScript/JSON highlighting, code groups, fence titles and line metadata, Shiki notation
classes, and passive tip/info/warning/danger/details containers. `[[toc]]` uses the public TOC plugin
and includes top-level level-2/3 headings, matching its VitePress defaults. Header metadata still
includes levels 1–6. `{#id}` uses attrs with an `id`-only allowlist; only heading IDs survive the
adapter. Explicit IDs must start with a Unicode letter or underscore and contain at most 128 Unicode
code points from letters, numbers, `_`, `.`, `:`, and `-`; invalid IDs use the generated heading slug.
Heading IDs are deterministically suffixed in source order on duplicates and collisions with native
code controls, then document-prefixed once before both metadata and TOC rendering. Unlike the
upstream anchor default, duplicate explicit IDs do not fail a streamed snapshot. Adding headings
later regenerates earlier TOCs through full-source reparsing; there is no persistent outline cache.

Ordinary containers now extract VitePress v1.6.4's `createContainer` behavior, including inline
Markdown titles, document-scoped local links, references resolved against the complete current
source, and the `Details` default label. GitHub-style quoted alerts recognize TIP, NOTE, INFO,
IMPORTANT, WARNING, CAUTION, and DANGER, case-insensitively. Their custom titles remain escaped
literal text, retaining upstream syntax without its raw HTML interpolation. Unknown alert markers
remain ordinary blockquotes. Raw/v-pre containers and the upstream code-group implementation are
excluded from the extraction. The [vendor manifest](../deno/maieutics-output-renderer/vendor/vitepress/upstream.json)
records the tag, commit, original file hashes, and adaptations; its MIT notice is retained in source
and prepended to the embedded service bundle.

VS Code handles plain document-fragment clicks inside the owning rendered subtree: it opens enclosing
details, scrolls and focuses the matching heading, and prevents host URL navigation. Modified clicks
keep host behavior. The handler is removed on source replacement and disposal with the output's
AbortController; no window-level listener or document-authored handler is installed. Other frontends
need the equivalent local-fragment adapter when rendering inside Shadow DOM.

Unknown languages fall back to escaped text. On the passive endpoint, raw HTML, document scripts,
styles and directives remain text. Passive Markdown keeps Vue interpolations literal; the explicit template path below evaluates the defined data expression subset. No filesystem snippet or include rules are installed. Complete snapshots
are rendered through Vue SSR without compiling document expressions or SFC scripts.

`renderer_test.ts` covers reference corrections and isolation, inert content and executable links,
container title escaping, deterministic highlighting, independent ownership and repeated disposal,
source and expanded-output bounds, and cancellation or disposal during SSR. `smoke.ts` is the offline
integration fixture; CI builds it and runs it under the restricted permissions above. Host-scoped
baseline styles are provided in `styles.css`. Both a strict-CSP VS Code webview and the production
Notebook renderer have been verified against the live NativeAOT kernel. Resource delivery,
full template compilation, and Markstream comparison remain unverified and keep stage 1 open.

### Initial kernel rendering API

`POST /v1/render/markdown` accepts version 1, a document identity, and source text and returns a complete
passive HTML snapshot with host-scoped CSS. This stateless formatting request does not submit a turn,
modify canonical history, or append frames to a completed run. Capabilities advertise
`rendering: {version: 1}`; clients retain original Markdown when unavailable. Normative shapes are in
the frontend protocol document.

The process-scoped service admits at most eight requests, serializes rendering, and applies one
30-second request budget including startup and queueing. It materializes the offline bundle, acquires
the app/workspace effective policy, and launches through `DenoRunProcess`. An authenticated,
generation-specific nonce-bound readiness callback reports the child's actual loopback port. The internal child endpoint
authenticates every bounded render request. Host shutdown cancels requests, stops the child, and
removes its private module/cache directory under the configured data root's `.rendering` directory.
Cancellation may additionally await the owned process's bounded cleanup. Session overrides do not govern this shared passive
formatting scope; it executes no session code.

`FrontendOutputRendererTests` drives the real child through the composition root, verifies SSR and
reference corrections without adding history, rejects anonymous or invalid requests and forged
readiness, and asserts child and staging-directory cleanup. Missing Deno and denied renderer
environment permissions produce recoverable rendering failures without disabling other frontend
routes. A process smoke test renders through the published executable's embedded offline child.

### Initial Notebook demo

The [demo instructions and snapshot](../deno/maieutics-vscode/demo/README.md) exercise the production
path without a model request: Notebook MIME renderer → extension bridge → authenticated executable
API → supervised Deno child → HTML/CSS → the same Notebook output. The fixture includes containers,
a table, highlighted TypeScript, details, and reference links. No derived HTML is persisted.

Each mounted output has a stable document identity and a monotonically increasing local request
revision. One extension-owned request pump permits one active request and at most 64 pending
documents, coalescing intermediate source updates per document. Superseded computations are never
applied; output disposal cancels its request and prevents late replies from changing the DOM.
This coalesces derived formatting requests, not canonical Agent events or committed protocol frames.

The first presentation finishes native rendering, then requests SSR. For subsequent source or binding
updates with the same output layout, the browser keeps the committed SSR boundary attached while
preparing the new native fallback offscreen and awaiting its matching SSR response. Styles and HTML
commit together without clearing the visible document or reverting it to native Markdown on every
text delta. Existing fragment handlers and Vue apps remain active until that commit. If SSR is
unavailable, the fully prepared latest native fallback replaces the retained content; obsolete or
disposed responses still cannot commit. A changed outer activity layout is staged before a single
shell replacement. This retains the existing layout for ordinary text streaming, not arbitrary
structural DOM reconciliation. The initial native-to-SSR transition remains available for immediate
readability; no fixed delay hides output. A matching commit emits a local application acknowledgement.
The extension-host tests verify actual DOM output under CSP, require the live controller to produce
structured text nodes, and round-trip updated text through the production serializer. An optional
live-kernel test restores output through that serializer and waits for the application acknowledgement
from the production Notebook renderer. It must not manufacture a custom MIME output that bypasses the
serializer's routing decision. Neither signal affects Agent completion.

This demo reparses the complete source, so later reference definitions correct earlier text. It has
no persistent server-side document or local subtree patching. The explicit template display lane
below now accepts published TS producer results as binding snapshots. Complete region
replacement can reset text selection and expanded details. Code-tab choices have the bounded retention
described below. Resource URL rewriting, interactive components,
and languages beyond the bundled grammar subset require subsequent stages.

### Code presentation compatibility

Code fences support literal filename titles, `:line-numbers` / `:no-line-numbers`, custom starting
numbers, and `{1,3-5}` line highlights without losing syntax highlighting. The matching-version Shiki
transformers implement VitePress 1.6.4's diff, focus, highlight, error, and warning comment notations.
Displayed line numbers are assigned after comment-only notation removal, so hidden annotation lines
do not leave numbering gaps. Line ranges use sorted intervals and logarithmic membership checks;
hostile range endpoints never allocate an author-sized array. Titles are escaped text, not templates
or HTML attributes.

`::: code-group` wraps direct fenced-code children in native radio controls with scoped, deterministic
identities, labels, and fieldset semantics. Shared HTML/CSS provides switching without document scripts
or Vue hydration in every compatible browser frontend. Empty, nested, or mixed-content groups keep
all their normal visible content. Closed and auto-closed groups reparse on complete snapshots.

The VS Code adapter captures selected non-default tabs before replacing a source region and restores
them when document/group ordinal, tab ordinal, and title match. Retention is bounded to 128 choices
per output with titles of at most 512 characters, and disposal releases that state. This is a local
presentation convenience, not the stable structural identity or revisioned patch runtime of stage 3.
Moving or removing groups or tabs can reset selection. Native controls do not establish an arbitrary
interactive-component execution contract.

The offline smoke includes code groups and fence metadata. Renderer tests cover escaping, document
isolation, deterministic control identities, comment notation isolation, enormous line ranges,
auto-closing groups, and mixed-content fallback. Kernel and published-process smoke tests exercise the
embedded artifact; browser verification checks CSS visibility, native label activation, and retaining
the selected tab through another SSR snapshot.

The remaining content gaps are frontmatter value exposure and metadata,
additional bundled languages, and product resource URL adaptation. File includes and snippet imports
need an explicit authorized resource contract. Complete live SFC snapshots use the separate lane below;
ordinary Markdown and data templates keep their existing policies.

## Explicit template snapshots (implemented 2026-11-05)

`POST /v1/render/template` and MIME `application/vnd.maieutics.template+json` explicitly select
version-1 data template evaluation. `/v1/render/markdown` continues to leave `{{ }}` literal even
if a caller supplies template fields. Capability `rendering.templateVersion: 1` advertises the new
lane separately. This is a complete-snapshot primitive, not the revisioned document protocol below.

A snapshot supplies Markdown source plus at most 128 uniquely named bindings. Binding states are
`pending`, `ready`, `failed`, and `cancelled`. Ready JSON data uses `value` (including `null`, `false`
and zero); ready `undefined` uses `undefined: true` with no `value`. Other states carry no value.
Missing names remain pending while source is open; `sourceSealed: true` makes missing names an
`unresolved_binding` failure. Explicitly pending bindings stay pending after sealing, since source
completion does not imply producer completion. The renderer captures and freezes bounded plain data
before asynchronous SSR and retains no binding state between requests or documents.

Acorn 8.18.0, already pinned in the workspace, supplies expression parsing and delimiter tokenization.
Maieutics interprets its AST to preserve pending semantics and restrict effects. The initial subset
includes primitive/array/object literals, own-property reads, optional chaining, unary `!`, `+`, `-`
and `typeof`, arithmetic, comparisons, strict equality, `&&`, `||`, `??`, ternaries, and numeric
`.toFixed(0..20)`. Only evaluated branches acquire dependencies: pending cannot select a fallback
branch or become false. Arbitrary calls, functions, assignment/update, loops, imports, `new`, prototype
paths and host globals are not exposed. This is a data expression subset, not arbitrary Vue or TS
execution. Vue's public `toDisplayString` supplies ready-value formatting; ready undefined/null render
empty text, distinct from pending's ellipsis and state metadata.

A Markdown-it inline rule recognizes interpolation only in normal inline content, including headings
and ordinary container titles. Escapes, code spans and fences stay literal. Acorn tokenization ignores
closing braces inside quoted strings, templates and regex literals; unsupported expressions still
produce local failures. Incomplete source is shown escaped and marked `incomplete`, or failed when
sealed, without executing guessed syntax. Inserted values are never reparsed as Markdown, HTML,
attributes, URLs or expressions. Heading metadata and TOCs read the same captured text and update
with the whole snapshot; explicit anchors are recommended for headings whose text changes.

The response includes local expression state/dependency metadata, indexed by the HTML's
`data-template-index` within that snapshot. These ordinals follow parser/render registration order
(including inline container titles), not stable source identities. A failure affects its own span;
independent later prose remains visible. The limits are 256 interpolations, 2,048 UTF-16 units per
expression, 256 interpreter steps and 32 AST traversal levels; data is limited to 8,192 values,
16 nesting levels and 64,000 aggregate UTF-16 units in keys/string values. Existing source, HTML,
request admission, cancellation and process limits still apply. Binding names are ASCII identifiers
of at most 64 characters; `undefined`, `constructor`, `prototype` and `__proto__` are reserved.
Non-finite numbers, accessors, class instances, cycles and prototype keys are rejected.

The VS Code turn renderer also claims the template MIME. The production rich-display serializer
retains its source and frozen bindings, and the extension forwards it to the explicit API only when
the kernel advertises support. Native Markdown remains the offline/unavailable fallback. Existing
`display_id` and `update: true` replace one display with a new complete snapshot even when source is
unchanged. The bridge's request revision rejects obsolete binding results; no new Agent event, binary
encoding or notebook schema version is introduced. Restoring a snapshot evaluates only pure data
expressions; it never executes producer code or starts a kernel by itself.

Producers run only through an explicitly selected existing runtime, such as `repl_execute`, under
its owning session's permissions and cancellation. They publish values through the existing structured
rich-display lane; there is no renderer-to-REPL call and no producer execution during re-render,
reconnect or restoration. `repl_execute` now describes this MIME and the complete-snapshot update
pattern. The [template demo](../deno/maieutics-vscode/demo/README.md) supplies runnable producer code
and frozen pending/ready snapshots. A producer should publish terminal failed/cancelled snapshots
when possible; an interrupted producer can leave the last frozen display pending. Automatic producer
registration, terminal settlement after cancellation, subscriptions beyond run lifetime, dependency
cycles between producers and document patches remain future document-runtime work. Complete live Vue
components have their own stateless lane below. The stateless renderer does not claim ownership of an external producer's lifetime.

## Explicit live Vue components (implemented 2026-11-05)

ADR [0047](architecture/decisions/0047-live-vue-output-components.md) defines this additional lane.
`rendering.vueVersion: 1`, authenticated `POST /v1/render/vue`, and rich-display MIME
`application/vnd.maieutics.vue+json` select it explicitly. The MIME contains
`{version:1, format:"markdown"|"sfc", source}` and should accompany a `text/plain` fallback.
The passive Markdown endpoint, streaming partial answers and restored notebook snapshots do not
execute `<script>` or Vue directives. After ADR 0048 below, successful completed live assistant
replies also select the component runtime directly. The dedicated MIME remains available for
explicit REPL displays and producer-driven updates.

For Markdown, public `@mdit-vue/plugin-component` and `plugin-sfc` extract SFC scripts/styles and
recognize component syntax. The shared Markdown pipeline supplies containers, highlighting and
anchors, then wraps the content as a Vue template. Fences and inline code use `v-pre` so literal
example interpolations do not execute. For SFC input, the source goes directly to the same official
Vue compiler. `compileScript`, `compileTemplate`, and `compileStyle` implement setup bindings,
directives, events and scoped CSS; Sucrase erases TypeScript. This is ecosystem reuse, not a second
implementation of Vue semantics. Static imports from `vue` bind to the shipped runtime. Other
imports, dynamic import, other `import.meta` expressions, external SFC blocks, custom blocks, CSS
modules and style preprocessors are rejected. Script `import.meta.env.SSR` is the target boolean
described below. VitePress page context, filesystem includes and resource loading are
not installed. A component must be complete before compilation; this lane does not infer future
bindings or execute partially received script text.

The compiler parent never evaluates authored code. Each SSR evaluation runs in a fresh owned Deno
worker with `deno.permissions:"none"`, a five-second deadline and a private transferred MessagePort.
Authored global `postMessage` cannot forge the private result. The worker has no filesystem,
network, environment or process-launch permissions; failures expose fixed diagnostics. Deno's
self-signal exception can nevertheless terminate the renderer process, as documented below. Cancellation and
renderer disposal terminate the worker and close its ports. Only one request is active under the
existing service gate. The trusted worker runtime is bundled ahead of time, embedded as JavaScript
text, and loaded from a module data URL: there is no runtime download or binary-to-text transfer.
The deadline is a CPU/lifetime bound, not a memory quota or a claim of protection against runtime bugs.

The response carries SSR HTML, baseline plus component CSS, and the browser component factory.
SSR uses the same factory unless a target constant requires a private server variant. The browser loads the shipped Vue runtime once, hydrates with `createSSRApp`, and owns
one app per output boundary. Reactive refs, computed values, directives and event handlers then
update that app locally. The renderer unmounts the old app when a changed-source replacement commits,
and on output or renderer disposal; `onUnmounted` runs and owned Vue effects stop. A complete output replacement
creates fresh component state. Identical source/mode/binding redelivery on an unchanged layout keeps
the committed DOM and Vue app, sends no redundant SSR request and preserves client state. Pending
source changes keep the old app interactive until their replacement commits; they do not unmount it
in anticipation of a response. This is not HMR or state-preserving structural patching. Browser
coverage checks initial SSR state, a real button click/reactive flush, computed scoped styles, and
unmount on replacement/disposal through both webview and actual notebook paths.

The live browser evaluation follows ADR 0038's existing executable bundled-view posture. It uses a
compiled factory under the notebook output CSP; this is an executable live output, not an HTML trust
upgrade or a browser sandbox. Setup runs once for SSR and again for client hydration. Initial state
must be deterministic, and external operations/producers belong in the authorized REPL rather than
setup. Vue subscriptions, listeners and timers created outside its effect scope still need their
normal cleanup hooks. ClientOnly now defers child regions until browser mount, as detailed below.
Async setup/Suspense, external imports and asynchronous resource hydration remain outside the
verified contract.

Persistence never replays component code: notebook saving and reading both remove this MIME,
retaining its producer-provided text fallback. The same read-time rule strips executable bundled-view
sources, matching their existing save-time rule. The notebook format remains v1; opening a file or
reconnecting does not make a saved component executable. A live producer can redisplay it through
the existing channel. The data-template MIME continues to persist its data-only source and bindings.

Source is limited to 200,000 UTF-16 units, compiled factory to 400,000, scoped styles to 48,000 and
HTML to 4,000,000. Existing request/admission/revision guards remain in force. SSR failure, malformed
source or unsupported frontend capability leaves readable native source/fallback. Stable document
nodes, late producer registration, binding subscriptions, incremental SFC compilation and patch
recovery remain the later document-runtime stages below.

## Default completed live assistant Vue (implemented 2026-11-06)

The user selected direct rendering of Vue in realtime assistant answers. ADR
[0048](architecture/decisions/0048-default-live-assistant-vue.md) supersedes the earlier decision to
require a REPL rich display for every component. This is a live presentation policy, not a change
to the passive Markdown endpoint or notebook trust.

The authenticated, stateless `POST /v1/render/assistant` accepts the existing version-1
`{version,documentId,source}` request and returns the existing HTML/CSS plus optional Vue component
metadata. `rendering.assistantVersion:1` advertises it. All validation, admission, source/code/CSS/HTML
bounds and worker isolation remain unchanged. It does not submit a turn or modify canonical history.
The compiler uses the public Markdown/SFC pipeline to recognize authored top-level script blocks
after frontmatter removal. Such Vue-in-Markdown compiles with its template, events, directives and
styles. Fenced, inline, escaped and quoted examples, and script-looking text in metadata, do not
select execution. Plain assistant text stays passive SSR. Standalone SFC input continues to use the
explicit `format:"sfc"` rich-display contract; no filesystem component resolution is inferred.

The VS Code controller emits local MIME `application/vnd.maieutics.live-assistant+json` only for
assistant text after **successful run completion**. Before that, source is still streaming through
the existing passive turn MIME. Failed, cancelled and truncated executions never acquire the live
marker. This conservative first implementation waits for the whole source/context: it neither
executes partial scripts nor substitutes undefined for future declarations. Native/SSR text remains
readable while source arrives. The text output cache explicitly invalidates on stream-to-sealed
transition even when the final text bytes match the last delta, so completion cannot leave a passive
output behind accidentally. Reasoning, tools and child reports keep their existing rendering lanes.

The extension selects the assistant endpoint only when the connected kernel advertises support;
older kernels retain passive SSR. The live renderer accepts optional compiled factories only for
this selected request or the existing explicit Vue MIME. Its existing revision guards, retained
presentation and Vue lifecycle apply. Identical completed redelivery reuses the app; a changed source
replaces it when ready. Setup still runs in SSR and hydration and must be deterministic. External
producers remain explicit tool work. Source completion here is not automatic producer settlement or
the proposed streaming document protocol.

### Passive streaming progress around Vue source (implemented 2026-11-09)

The initial passive implementation waited for successful source completion; the progressive Vue
increment below now admits complete setup/template prefixes on supporting frontends. Ordinary Markdown must continue
rendering while that source is incomplete: a Vue component does not make the entire reply an
executable unit during streaming.

The frontend rendering bridge previously discarded every SSR result whose request identity was
older than the latest text. When text arrived faster than rendering, every completed snapshot
could be discarded until the model stopped. Retaining the previous SSR DOM to avoid flicker then
made the entire answer appear frozen. The native fallback had a separate parsing problem: escaping
an `html_block` after parsing did not restore the Markdown that an unfinished HTML/Vue block had
already consumed.

Passive streaming requests now opt into the extension/webview bridge with `streaming:true`.
The bridge still coalesces queued updates and owns one active render, but also publishes a completed
snapshot when its source is an exact prefix of the latest streaming source. Its response includes
`streaming:true` and `renderedSource`, the bounded source of that rendered snapshot. This is local
frontend messaging; the authenticated kernel Markdown API and its version remain unchanged.
These previews always use passive Markdown, never the template, explicit Vue or sealed assistant
evaluation lanes. A rendered prefix is reparsed as one Markdown document, so later reference
definitions and TOC targets can update earlier content; this does not render detached text tails.

The browser validates document identity, source-prefix ancestry, monotonically increasing request
revisions and the current output lifetime before committing progress into the existing SSR boundary.
A source rewrite starts a new revision epoch even if later text returns to the old prefix. Sealing,
mode changes, cancellation and disposal invalidate the streaming ancestry. Old prefixes cannot
replace a completed component or execute a Vue factory. Source and binding replacements outside
the passive streaming lane retain exact-revision matching. Only the latest unavailable result may
commit its native fallback.

For synthetic Maieutics Markdown items the native parser uses `html:false`, matching the kernel's
passive parser, before tokenization. Raw Vue/HTML remains escaped readable source, while adjacent
Markdown headings, lists and other syntax continue to parse, including after cancellation or
passive snapshot restore. The policy is scoped to Maieutics output items; fenced/inline code
examples stay literal and ordinary notebook Markdown retains its own HTML policy. Vue setup is
not executed with guessed closing tags or undefined future bindings.

Deterministic bridge tests hold a newer render open and require the completed prefix to be posted
before releasing it. Native parser tests pin Markdown swallowed by an unfinished Vue/HTML block.
Real VS Code webview coverage checks prefix commits while rendering continues, stable SSR boundary
identity, out-of-order responses, rewrite epochs, sealed transitions and rejection of executable
streaming metadata. Independent component units, state-preserving incremental Vue hydration and
late-binding dependency scheduling remain later document-runtime work.

Assistant provenance is frontend runtime state: notebook serialization reads the local MIME but
saves only the ordinary source/timeline snapshot. The pure codec reconstructs passive turn MIME and
never trusts a persisted live marker. The connected restoration adapter can renew provenance only
by verifying the session, run, submitted input and exact assistant text against the authenticated
kernel transcript (see historical recovery below). Offline or unverifiable snapshots remain passive.
An explicitly resumed live run/replay can also recreate derived component state. No external producer
executes during rendering, history recovery or replay.

Frontmatter extraction reuses `@mdit-vue/plugin-frontmatter` 2.1.3 and its existing locked
`js-yaml` 3.15.2 dependency. The 2026-11-10 increment parses bounded YAML/YML or JSON objects with
non-executable JSON-schema types, instead of discarding all metadata. Explicit JSON engines use
strict JSON parsing. Headers remain limited to 16,000 UTF-16 units; data is copied/frozen through
the existing template capture limits (8,192 nodes, depth 16, 64,000 aggregate characters). Cycles,
prototype keys, unsupported tags, malformed/root-array/scalar data and executable engines fail
with `invalid_frontmatter`. Unfinished headers remain readable source until closed.

`{{ $frontmatter.title }}` and nested dot/bracket paths work in ordinary/passive Markdown, including
streaming prefixes after header closure. Values are escaped text, never reparsed Markdown/HTML.
Other passive expressions remain literal. Data-only paths resolve before heading/TOC extraction,
so heading text and scoped anchors agree in passive and Vue Markdown. Fenced/inline examples stay
literal. Explicit template contexts use document-owned `$frontmatter` in preference to a caller
binding of that name; other bindings retain their existing lifecycle and expression behavior.

Live Vue Markdown also receives a readonly `$frontmatter` global from its component setup wrapper,
identically in the isolated SSR worker and browser factory. The wrapper preserves Vue's two-argument
setup contract so expose/context injection remains available. Pre-resolved metadata spans use
`v-pre`, preventing metadata containing mustaches from becoming Vue source. Runtime Vue expressions
and bound attributes can read the same metadata. `useData()` is not implemented. Site generation, frontmatter-driven document head, site layouts
and navigation are intentional scope exclusions; resource loading needs its own explicit contract.
Metadata does not itself authorize component execution, start producers or modify conversation state.

Frontmatter validation passes 54 renderer/interpreter/worker tests and 27 real VS Code scenarios
against the osx-arm64 NativeAOT executable, including SSR-to-browser frontmatter hydration and
reactive clicks. Offline bundled smoke passes with network/filesystem/process access denied. The
committed service bundle is regenerated; no HTTP, IPC or notebook format changes are required.
The full Deno workspace passes 725 tests plus 20 Node scenarios. Types/lint/format checks pass.
NativeAOT publish and real browser verification run on macOS arm64; Linux/Windows CI is not
run locally.

The stylesheet supplies the counter's common `--vp-c-brand-1`, `--vp-c-brand-soft`, `--vp-c-bg-soft`,
`--vp-c-text-1`, `--vp-c-text-2` and `--vp-c-divider` aliases from VS Code colors with browser fallbacks.
These are a content subset, not the complete VitePress default theme.

The screenshot counter regression covers initial zero, computed disabled controls, increment/reset,
`v-if`/`v-else` hints, scoped CSS, duplicate delivery and pending replacement lifecycle. Core tests
prove script-like code examples and metadata stay inert. Controller tests verify successful live
selection and failed-run exclusion, invalidate a sealed text cache without requiring a changed
final delta, and save/reopen live answers as passive source. Real Notebook tests use the production
output factory and the new endpoint against a published osx-arm64 NativeAOT kernel. The complete
.NET suite passes 1,585 tests with eight existing platform/external-service skips (1,593 total);
All 708 Deno workspace tests and 20 Node scenarios pass. Four published-process rendering
smoke tests and the offline assistant component probe pass. Release build has no warnings and
InspectCode has no error-level findings (two existing non-gating warnings).

### Restored Markdown during connection startup (implemented 2026-11-10)

A restored output can request formatting before the extension has published its connection.
The bridge now joins an existing startup promise instead of immediately returning unavailable.
This does not start a kernel for an offline notebook. Rendering cancellation stops only that
request's wait; connection startup retains its owner, deadline and failure observation. Startup
failure still selects the prepared native fallback.

The real VS Code regression holds the capabilities response until the saved notebook's renderer
requests formatting. Before the fix, that output applied native fallback with zero SSR requests
and never upgraded after startup. With the fix it receives one passive SSR result. A separate
saved-notebook test forces unavailable rendering and checks actual native heading and table nodes,
including Markdown following a script block. Restore retains passive MIME and does not execute Vue.

Native rendering failures now report a content-free diagnostic through the existing application
acknowledgement. If the native renderer fails after attaching a shadow root, plain source is placed
inside that root so it remains visible. The bridge logs this failure without logging source or
exception messages. No public HTTP protocol or notebook snapshot format changes.

Verification on macOS arm64 with VS Code 1.144.1 passes all 22 extension-host scenarios,
335 frontend unit tests, types, lint and formatting checks. The cold-start regression was first
run against the unchanged connection path and failed with `engine=native, SSR requests=0`.
The Release solution build passes with warnings as errors; all 1,668 .NET tests pass with eight
existing platform/external-service skips. This frontend-only change does not republish NativeAOT;
Linux/Windows and CI validation were not run locally.

### Historical Vue recovery from canonical session history (implemented 2026-11-10)

Dropping the local live MIME on save correctly prevents files from granting execution authority, but
previously also meant that every reloaded historical component became literal source. The connected
notebook serializer now separates decoding from renewing provenance: it first uses the unchanged
passive codec, then reconnects the kernel for a valid session/run binding and reads that session's
canonical transcript when `assistantVersion:1` is supported. Unbound portable snapshots do not
launch a kernel. Connection startup is owned by the extension and shared with commands; notebook
cancellation only stops verification waiting. Restart/shutdown cancel and observe startup before
releasing its handle. Verification has one 20-second budget and observes notebook cancellation
without cancelling the connection owner; failure retains readable passive output.

Only a single assistant text node can be promoted. Its persisted turn binding must match a committed
non-truncated kernel turn's run ID and submitted user text, and its complete source must match the
joined text parts of one assistant message byte-for-byte. The returned transcript must match the
requested session and carry a valid nonnegative history revision. Its `version` is a changing
history revision, not the frontend wire version; recovery does not require it to equal one.
Tool/user text, altered source, missing bindings, truncated history, and file-authored live markers
cannot grant provenance. Promotion reconstructs only the verified text node, discarding other
file-authored payload fields. Compiled factories remain response-only. This covers both portable
files tied to a session and the virtual session notebooks materialized from canonical history,
including history written before this fix. No snapshot or HTTP shape changes.

Verified nodes use the same assistant compiler, isolated SSR worker, factory validation and owned
Vue hydration/disposal as newly completed replies. Components regain their UI and interactivity.
Setup reruns and local reactive state starts from its declared initial value; previously clicked
counts, timers and browser-local state are not persisted. This does not re-submit an Agent turn,
call a model, execute tools, launch external producers or modify canonical history.

The real VS Code regression opens saved and virtual history through the activated extension and
requires a `vue` application acknowledgement. It also checks altered/unbound source, truncated
turns, session mismatch and transcript failure stay passive. Its baseline run fails with `ssr`
instead of `vue`. The optional history-rendering discovery fixture exercises the same restoration
path against the actual NativeAOT executable and Deno compiler without any model request.

Verification passes 337 frontend unit tests and all 23 real VS Code scenarios on macOS arm64
(VS Code 1.144.1), including the actual NativeAOT/Deno history compiler path with history revision
17 and no pre-established connection. Types, lint, formatting and diff checks pass. Release build
has no warnings; the .NET suite passes 1,668 tests with eight existing platform/external-service
skips. No executable source changed, so NativeAOT is not republished. Multi-platform CI is not
run locally.

### Model-facing output instructions (implemented 2026-11-06)

The generic `Commands/SystemPromptProfileProvider` captures system-instruction contributions when a
conversation run acquires its selected profile. It preserves the configured `SystemPrompt`,
client, tools, capabilities and limits, then composes with the existing skill and title decorators.
New, resumed and forked product sessions use this same path, including session-selected models.
Each run captures the section once; it never accumulates in configuration or canonical history.
Each contribution is retained as an item in immutable `AgentSessionOptions.SystemInstructions`
and delivered as an independent native system message. The configured base, skill catalog and
title context are not concatenated with plugin bodies. OpenAI retains message items; Anthropic
retains separate top-level `system` text blocks, including across tool continuations. Source/name
attribution remains inside its own message; content cannot manufacture another protocol message by
inserting a heading, code fence, JSON fragment or XML closing tag. These are API framing boundaries,
not a guarantee of semantic isolation between same-role instructions.
Metadata-only title requests bypass the conversation decorators. The provider-neutral Agent runtime
does not acquire a dependency on the frontend or VitePress.

The instructions advertise the shipped VitePress Markdown subset and optional direct Vue-in-Markdown,
with valid multiline container and counter examples. They distinguish live source from fenced
examples, passive streaming from successful completed Vue compilation, and deterministic SSR setup
from authorized external tool work. They describe the `vue`-only import surface, passive notebook
restore and unavailable site/resource/async features. These describe the product's rendering path,
not a guarantee that every connected or older frontend supports interaction; readable prose remains
part of interactive output. User-requested output formats take precedence over optional presentation.

The VitePress instructions are now a static `extensions.SystemPrompt` contribution in the embedded
`deno/maieutics-output-renderer/system-prompt.json` manifest. The composition root registers this
code-shipped contribution with `SystemPromptCatalog`, alongside approved external plugin contributions.
There is no renderer-specific profile decorator or unconditional output instruction in the session
manager. This data resource is embedded separately; it is not part of `bundle/service.js` and changes
to its text do not require rebuilding the SSR bundle.

Plugin instruction contribution routes are:

- Approved plugins can declare `extensions.skills` roots, return inline skills through the `Skills`
  extension point, or publish them with the declared/granted `skills.publish` capability (ADR 0039).
  The catalog supplies names, descriptions and `skill://` pointers to subsequent run prompts;
  skill bodies are read on demand or included through explicit user selection. They are plugin
  context, not arbitrary unconditional system-prompt fragments.
- A plugin-declared MCP server can supply `prompts/list` and `prompts/get` (ADR 0041). Explicit
  `mcp-prompt://` user references expand at submission as framed user-side instructions, not system
  instructions. Approval/revocation and the existing content budgets still apply.

- `SystemPrompt` supplies actual system-level instructions (ADR
  [0049](architecture/decisions/0049-system-prompt-extensions.md)): static `{name, content}` manifest
  entries or a `defineExtensionPoint("SystemPrompt", ...)` worker hook explicitly selected by an
  approved `{provider: "worker-export"}` declaration. Contributions follow the base as native messages,
  in stable source/export order. Worker collection shares one five-second wait budget; errors and
  invalid output are logged and omitted with no stale cache. Explicit selectors route cold workers
  before their first registry frame. Revocation, provider removal, descriptor reload
  and host stop are rechecked before the snapshot is returned. An acquired run keeps its text.
  Disposed generation wait resources produce an unavailable/empty contribution set, while explicit
  caller cancellation still propagates. A provider selector must match the worker-map key exactly
  (`main` for `entrypoints.worker.main`, not a module-relative path).

The external plugin declarations and provider selectors join the existing extensions fingerprint
domain, so their changes revoke approval. Code-shipped built-ins are registered by the composition
root and create no exemption for external plugins. The additive hook uses existing `host.invoke`
IPC with `{workspaceRoot}` context and `{name, content}[]` results; frontend wire and persisted
formats remain unchanged. Unlike a skill, these instructions need no on-demand body read.

Product integration coverage checks actual provider requests across consecutive turns with empty
and configured base prompts, with and without a skill catalog. It verifies one output section per
request, preserved base instructions/catalog entries, unchanged configured profiles and title-model
acquisition, and canonical history containing only the user's input and the model's response.
Additional coverage exercises static approval/change/reapproval/revoke, explicit provider admission,
worker failure without sticky text, cancellation/revocation during a hook, strict size/shape limits,
inner-lease release on acquisition failure and real Deno worker discovery/invocation of the marker.
Kernel/real-Deno integration also invokes the first hook before any registry frame, using the
production host class and bus with eager startup/registry publication disabled by its owned test
entry. Native-provider conformance checks delimiters and tool loops against all three APIs.

## Progressive Vue setup and template prefixes (implemented 2026-11-10)

This first increment supports a complete single synchronous `<script setup>` followed by growing
Markdown/Vue templates in the same assistant text output. All declarations must belong to that
sealed setup module. Refs, computed values, closures and hooks live in one mounted client instance;
a later template reads its existing bindings rather than reinitializing them. This is not a JS
interpreter that executes incomplete statements, arbitrary later modules, or undeclared names.
Cross-message/tool-round/cell scope and automatic producer subscriptions remain separate work.

The controller now grants runtime assistant provenance to admitted streaming text, not just the
successful terminal snapshot. Native Markdown remains immediately readable. Supporting kernels
advertise `rendering.progressiveVersion:1`; old kernels continue passive streaming and may compile
only the completed answer. Pure notebook decoding still strips provenance; canonical historical
verification remains required. Cancellation/failure/truncation seal back to passive output and
unmount any admitted component. Already executed presentation code cannot be undone by a later
run failure. No Agent event ordering, canonical history or notebook format changes are introduced.

The existing assistant endpoint adds `progressive:true` and optional `compileOnly:true`. The compiler
uses the public Vue SFC/script/template APIs; template usage is excluded from script-binding
collection so later templates can read bindings that the initial template did not use. A SHA-256
`setupKey` identifies the compiled setup, captured frontmatter and document scope. `renderCode`
contains a separate render-function factory. Unknown render-context names and async setup scopes
are rejected before admission. On incomplete trailing syntax or unresolved template names, at most
16 blank-line prefix candidates are compiled; only a genuinely compilable prefix is accepted.
There is no insertion of guessed script/tag closings. A new valid prefix can include later Markdown
references and updated TOC definitions because the entire accepted prefix is parsed together.

The first accepted component uses isolated SSR and ships initial HTML plus the full factory.
After delivery, the extension's bounded initialization hint requests compile-only updates: the
service compiles the new render function/CSS but executes no SSR setup and returns no new component
factory. This avoids hydrating new server-initial state over interactive client state. Recovery or
hint eviction can require fresh SSR; deterministic presentation setup remains required, and external
producers still belong to authorized tools. The service does not retain JS closures or a shared VM.
All frontends share compilation/initial SSR through Deno; each frontend owns its local UI state.
Multi-frontend shared mutable state requires a separate kernel-owned data/action protocol.

The client retains the Vue app, setup bindings and DOM boundary and replaces only its render
function and CSS. Render caches are invalidated when the compiled template changes; compiler block
optimizations from an older template are not reused. Ordinary Vue keyed reconciliation preserves
compatible existing nodes and child instances. Source/shape/key changes may legitimately replace
DOM; this is not the proposed stable document-unit protocol. Vue-owned DOM is never externally
morphed. Changed setup identity requires a fresh full factory for a new source epoch; render-only
updates cannot silently reinitialize the scope. Ending the reply preserves an already mounted
progressive instance. New ordinary completed/history components retain their existing full-render
lane when no progressive scope was established.

The bridge coalesces source updates, publishes useful completed prefixes while newer source streams,
and tracks rewrite epochs independently of text prefix equality. Returning to an older prefix does
not resurrect its initialization. Browser generation/revision checks reject stale results; sealing
rejects late streaming replies. Cancellation/disposal remove pending requests and initialization
hints, and Vue unmount stops effects exactly once. Hints and source ancestry are bounded to 64
entries. A pending tail retains the last usable interactive prefix. A sealed incomplete or invalid
update reports a local status while preserving the last usable presentation; it cannot silently
substitute JS `undefined` or a false conditional branch.

Coverage checks delayed prefix delivery, compile-only selection, rewrite/revert epochs, cancellation,
wire validation, isolated SSR, and the real controller publishing live MIME before terminal frames.
Actual browser tests click an early counter, append a computed-value template, append incomplete
markup, finish the markup against the current count, seal without resetting setup, report incomplete
sealed source, then rewrite setup and verify one disposal per scope. The same scenario is exercised
against the real NativeAOT executable and Deno compiler.

Validation passes 27 real VS Code scenarios on macOS arm64 (1.144.1), including actual
NativeAOT/Deno compilation and client updates, and 1,669 .NET tests with eight existing
platform/external-service skips. The Deno workspace passes 731 tests plus 20 Node scenarios.
Release build is warning-free; NativeAOT publishing, offline
smoke, types/lint/format and diff checks pass. Linux/Windows CI is not run locally.

## Math, code controls and source diagnostics (implemented 2026-11-10)

The content pipeline now renders inline/block dollar math as offline SVG with MathJax 3.2.2.
Reuse is deliberate: extract only the MIT dollar-tokenization rules from markdown-it-mathjax3 4.3.2,
and use MathJax's public TeX/SVG/document APIs directly. The original plugin's Juice/resource graph,
all-package loader and repeated global HTML-handler registration are excluded. The dependency review
pins MathJax and adds no native binary, runtime I/O or permission. Its Apache-2.0 license and the
extracted-rule license ship in the service bundle. Every formula creates its own document and macro
state, so earlier formulas/documents cannot mutate later compilation. Base/AMS/local newcommand
support is bounded: 128 formulas, 4,096 source units, 1,000 macro expansions and 200,000 output units
per formula; excess/errors remain literal. No URL/HTML/require/autoload package is enabled. SVG paths
need no remote fonts or scripts. Incomplete block delimiters remain literal until closed; Vue math
wrappers use v-pre. General output/compiler limits still apply.

The statically bundled highlighter expands from TS/JS/JSON to common programming, shell, data and
markup languages; unknown grammars remain escaped text. Code fences carry escaped original-source
copy payloads. An explicit button click delegates to the frontend clipboard owner, bounded to the
existing 200,000 source limit. Copy contains original comments/newlines and no synthetic line numbers;
status is request-correlated and disposal removes pending UI references. The VS Code clipboard API
is used without reading clipboard contents or logging code. No kernel request is needed for copying.

Rendering snapshots and typed 400 component errors add optional diagnostics (at most 32): fixed code
and one-based source line/column, without source fragments or compiler exception messages. The kernel
validates their bounds before forwarding, the client validates the shape, and old clients ignore the
additive field. Fence diagnostics use parser token ranges and include stripped frontmatter offsets.
They point to the unterminated opening fence and advise longer outer fences for nested examples;
parsing is not changed or repaired. Math diagnostics identify the paragraph/block start. Component
errors identify the actual authored script/component start (fenced examples cannot claim this
position); precise generated-template source maps remain future work. Current codes are
unclosed_code_fence, component_compile_failed, math_invalid and math_limit.

The notebook displays diagnostics only after sealing, outside Vue-owned DOM, with stale request
checks and explicit node ownership. Streaming incompleteness remains quiet, while a failed sealed
update preserves its last valid interactive component. This does not add document-unit patches,
producer subscriptions, cross-cell state, resource delivery or arbitrary component imports.

Validation passes 739 Deno tests plus 20 Node scenarios, 1,669 .NET tests (eight existing
platform/external-MCP skips), and 27 real VS Code scenarios on macOS arm64. The browser coverage
includes actual NativeAOT/Deno output, SVG namespaces and clipboard controls in passive, completed
Vue and progressive Vue paths, source-line diagnostics, and existing ref/computed state retention.
Warning-free Release build, NativeAOT publishing, offline smoke, bundle freshness, types/lint/format
and diff checks pass. Linux/Windows CI remains to be run.

## Upstream default content theme (implemented 2026-11-10)

The output now uses VitePress v1.6.4 default content styling rather than the former hand-written
approximation. Four upstream sources are retained verbatim with SHA-256 entries in the existing
MIT vendor manifest: base.css, vars.css, vp-doc.css and custom-block.css. A deterministic build
adapter uses the pinned Vue style compiler's PostCSS tree, selects content resets and the variable
dependency closure, scopes selectors to the output boundary, wraps host color/font tokens around
upstream defaults, and emits the committed, licensed styles.css. Runtime rendering still reads one
embedded CSS resource. No site runtime, host html/body/global layout, downloaded fonts, form resets,
navigation or team layout is introduced.

Headings, paragraphs, lists, quotes, striped tables, inline code, rounded semantic containers and
code blocks retain upstream declarations. An explicit product adapter handles native radio tabs,
copy buttons, inline data-line numbers, template states and math. Diff symbols use ::after so they
do not replace line numbers' ::before. Mobile code margins stay within the notebook boundary.
The low-specificity :where output guard preserves authored scoped component style precedence.
Shadow DOM dark contexts recognize VS Code dark/high-contrast and generic .dark ancestors; standalone
hosts can mark the root/ancestor .dark. Upstream fallback palettes and Shiki tokens switch together
without recompiling output. Template-only updates also leave an identical mounted stylesheet in place,
avoiding unnecessary reparsing or animation restarts. The external-link icon stays an embedded SVG; no resource fetch is added.

Baseline and authored component CSS each retain a 48,000-character budget; the combined receiver
budget grows to 96,000. Older 64k clients handle normal content but may fall back on unusually large
component styles. No wire shape, notebook version, execution provenance or lifecycle changes.
CI now rebuilds and checks styles.css together with the committed SSR bundle; focused tests verify
source hashes, complete content variables, scoping, freshness and bounds. Browser coverage checks
actual heading/container geometry, theme switching across a shadow boundary, line-number/diff
coexistence, native code controls, authored-style precedence and a combined CSS response above 64k.

## Client-only component regions and target constants (implemented 2026-11-11)

Compiled live output now includes the VitePress 1.6.4 ClientOnly mount gate as a self-contained
local component. The adapted source records the original path/hash in the MIT vendor manifest. SSR
and the first browser hydration render the same empty placeholder. Vue onMounted then activates the
default slot. Nested browser-only setup/slot expressions are not evaluated by SSR. This is a
presentation boundary, not a way to defer parent setup or change execution provenance. Parent DOM
work still requires onMounted, an SSR guard or a child component. No fallback slot, async loader or
module resolver is introduced; defineClientComponent remains future resource/module work.

Acorn rewrites the exact script expression import.meta.env.SSR into a target constant. SSR receives
true, while browser setup and later render functions receive false. Strings/comments are unchanged;
other meta/environment access and dynamic imports remain rejected. Vue's pinned template parser
rejects raw import.meta expressions, so template consumers declare a script binding first. Two
factories are emitted only when target code differs. The private SSR factory is selected by the
owned worker launcher and never crosses the frontend response boundary. Initial HTML still requires
deterministic state; target constants do not imply server state serialization.

Every initial factory registers the gate, even if it first appears in a later accepted prefix.
Compile-only updates refer to the retained component registry/setup bindings and do not rerun setup
or SSR. Gate types, child reactive state and owned cleanup hooks survive template additions and
sealing. Disposal follows normal Vue unmount and stops child effects/listeners. Setup identity also
covers the built-in gate contract. Existing provenance, factory/output bounds, worker deadlines,
permissions and passive fallback remain unchanged; old frontend runtimes can execute the standard
Vue factory without new host services. No public wire/notebook format or capability version changes.

Coverage includes real worker exclusion of browser-only child setup and slot expressions, correct
target constants, unsupported meta/import/write rejection, private-factory wire exclusion, late gate
admission, browser state/DOM preservation, sealing, exactly-once listener cleanup and verified
history restoration through the real host pipeline.

## Standalone browser ESM client (implemented 2026-11-11)

The browser implementation now lives in deno/maieutics-output-client rather than in the notebook
entrypoint. Its single dist/outputClient.js bundles the pinned Vue runtime, passive Markdown-it and
all existing DOM/SSR/progressive-component logic; accompanying declarations describe the public API.
There are no external runtime imports, VS Code APIs, transport credentials or automatic I/O. This
is the shared presentation implementation, not a second Markdown compiler or a move of SSR to the
browser. Compilation, highlighting, math and initial SSR remain in the executable-owned Deno service.

createOutputRenderer accepts host message dispatch, optional native Markdown and explicit action
handling. render takes an id, output kind, value, element and optional cancellation signal; receive
applies correlated backend replies and returns the asynchronous Vue-update work. render completion
means fallback preparation/request dispatch, while the existing applied envelope means a committed
presentation. disposeOutput releases selected outputs; terminal dispose aborts owned preparation,
unmounts all apps and rejects future rendering. Transport subscriptions remain owned by the host. A low-level mountVueIsland export supports hosts
that already own SSR DOM and verified factories; it does not grant execution authority itself.
Existing revision guards prevent late work from publishing after disposal. The default Markdown
fallback is passive, disables HTML/command links and uses a scoped shadow boundary.

The VS Code adapter maps MIME/items, injects protected native Markdown and forwards message/action
services. The same built ESM is copied into media/outputClient.js; extension-host Node code does not
import another workspace module. build:media and CI rebuild/copy it, and CI rejects a stale committed
artifact. At extraction time the widget/view renderers remained separate; ADR 0050 retires them in the
subsequent Vue UI migration described below. Public frontend envelopes, capability
versions, provenance and notebook persistence are unchanged: assistant/vue kinds are explicit host
execution choices, never inferred from an unverified saved marker. CSS can still inherit editor
variables, with browser defaults when absent.

Coverage includes ESM shape/no external imports, headless import, direct standalone browser Markdown
and SSR, progressive ClientOnly state, terminal/idempotent disposal, aborted preparation with no late
commit, and the existing actual VS Code notebook/history paths. Registry publication, a custom element
wrapper and a browser HTTP/clipboard transport adapter are not introduced by this extraction.

## Vue UI migration (implemented 2026-11-12)

[ADR 0050](architecture/decisions/0050-retire-custom-component-families.md) removes the former
component-family engine, widget runtime and JSX authoring dialect. SDK 0.3 uses a form-only data
producer; the shared output ESM owns normal Vue form components using the same bundled Vue runtime
as assistant islands. The notebook adapter only supplies transport. There is no custom family
registration, dependency collector/paint loop or executable bundled component source.

Forms use a separate version-one MIME/comm target, with confirmed live state, local Vue drafts,
shared backend projections across views and deterministic unmount. Snapshot save/read marks forms
permanently readonly; legacy component sources are discarded. The form contract is advertised by
UI capability version 2; rendering capabilities and assistant SSR/progressive semantics are unchanged.
Built-in form rendering is client-side; it does not add SSR model hydration or a plugin catalog.

The VS Code form bridge bounds cached live states to 1,024 models. An excess new open is rejected
locally with `form_state_limit`; only that model's mounted views receive a readonly/closed
notification. The shared receive pump and admitted models remain usable, including state updates,
actions and refresh opens at capacity. No upstream producer is closed by this frontend-local limit.
Once a model closes and releases capacity, a fresh open may admit a previously rejected model.
Actual transport loss still makes all retained views readonly. Regression coverage fills the cache,
rejects the next open, verifies surviving interactions and tests admission after capacity is freed;
the standalone ESM export gate also asserts `createFormRenderer`.

General business-model projections, correlated action results and approved reusable Vue component
registration remain the next stages in the [migration roadmap](vue-reactive-ui-migration.md).

Validation on macOS arm64: 681 Deno tests and 20 Node scenarios, 1,679 .NET cases
(1,671 passed; eight existing platform/external-MCP skips), 25 real VS Code cases and 13 published
NativeAOT process smokes pass. The real notebook tests exercise both current and migrated historical
form MIME through the production serializer/renderer registration; browser tests verify field
values, draft/focus preservation, multi-view updates, readonly closure and cleanup even when host
unmount delivery fails. The published-process smoke creates, displays, updates and closes a form
through the actual Deno REPL and proves that old authoring APIs are absent. Warning-free Release
build, pinned/frozen install, format/lint/types, SDK publish dry-run, offline SSR and committed asset
freshness checks pass. Linux/Windows validation is delegated to the new PR's CI.


## Plugin HTTP transport foundation (implemented 2026-11-12)

[ADR 0051](architecture/decisions/0051-plugin-scoped-http-fetch.md) selects HTTP as the primary
transport for future plugin component data/actions. The shared browser ESM exports a host-owned
`createPluginHttpClient` and a Vue `useFetch()` composable, retaining native Response and body
streams. A low-level island can receive a client explicitly; the compiler links the named
`useFetch` import from `@maieutics/output-client`. Browser injection uses the same bundled Vue
runtime. Neither ordinary assistant output nor a saved artifact selects a plugin's authority.

Each HTTP mount gets an independent credential, validated against its plugin by the gateway.
Authenticated discovery publishes only approved live scoped URLs; old registry payloads without
`accessToken` produce no page link. Scoped CORS supports opaque browser origins without ambient
credentials; the existing HTML CSP is unchanged. Component unmount and host disposal cancel active
requests/bodies. Redirect following, automatic JSON conversion, retries and background reconnects
are disabled. Binary stays binary and responses are consumed as streams by callers.

SSR can compile the interface but receives no HTTP client and cannot perform plugin requests.
Prefetch and hydration response reuse require a later explicit read policy. Approved component
delivery and notebook-side assignment of plugin context are also subsequent work; existing form
comms remain usable. Tests cover real workers, isolation/revocation, binary/streaming/cancellation,
Vue scopes, browser native Fetch, SSR denial and compatible kernel discovery.

Review follow-up (2026-11-13): root admission lasts for the registration even while its live value
is `undefined`; another provider fails at `provide()` until the owner releases it. Handler changes
cross the per-element collection codec instead of cloning functions. Real kernel/Deno coverage
now verifies scoped URL invalidation through approval revocation and fresh credentials after
re-approval. Shutdown preserves all stage failures using `AggregateError` when needed.

Pause/resume coverage also pins the existing URL lifecycle: root admission survives `undefined`,
but the live mount token does not. Resume publishes a new URL; the old one remains 404. Clients
must rediscover the surface after a pause. Shutdown failure fixtures retain sole host ownership
by cleaning up the underlying gateway before returning the injected failure.

## Document state and presentation units

Each document retains source and a monotonically increasing source version, parsed structure,
document-level definitions, binding state, and a tree of presentation units. Units carry stable
identities, source ranges, compilation state, dependencies, and the last committed render version.

A unit can be a paragraph, code block, or component subtree. Structural constructs such as `v-if`,
`v-for`, and slots must keep the scope and context they need. Splitting every Markdown paragraph into
an independent Vue component would change semantics. Where a context-preserving split is unavailable,
the unit expands to the enclosing subtree or document.

Node identity is independent of the rendered text, title slug, or position in an output array. IDs are
scoped to the document and remain stable across updates while the logical unit survives. Source
reconciliation must explicitly handle merges, splits, removals, and changes to an enclosing unit.

A complete syntax block can remain dependent on later source or data. It can keep its DOM identity
without becoming immutable. Start with conservative reparsing of the current document and comparison
of resulting units; optimize parse ranges and dependency tracking after correctness is established.

## Incomplete source and late values

Incomplete syntax and unavailable data have different states and recovery rules.

| Condition | Presentation and transition |
|---|---|
| An expression, tag, or script is still incomplete | Buffer that construct or show escaped source according to the content policy; continue displaying independent complete content. Do not execute guessed closing syntax. |
| A complete expression references an undeclared binding | Register an unresolved reference and show a placeholder. |
| A registered producer has not completed | Keep the boundary pending and subscribe to its result. |
| A producer supplies a value | Render dependents using the new binding version. A ready value may legitimately be `undefined`, `null`, `false`, or zero. |
| A producer fails or is cancelled | Resolve the affected boundary to a typed failure or cancellation presentation. |
| Source is sealed and a reference has no provider | Resolve it to an unresolved-reference error. Known pending producers may continue under their own lifetime. |

Pending is a distinct runtime state, never a replacement value injected as JavaScript `undefined`.
Do not evaluate `report.total.toFixed(2)` until its required data is ready. A pending condition must
not become false and select `v-else`. The relevant conditional boundary remains pending. Dependency
handling must respect the selected expression and branch rather than requiring unused branches to
resolve.

An initial authoring option is a registered component with an explicit binding name:

```vue
<AwaitValue name="report" v-slot="{ value }">
  <ReportSummary :report="value" />
</AwaitValue>
```

This component and syntax remain proposed. The initial shipped lane instead accepts explicit data
snapshots through the template MIME above. Automatic producer registration and slot ownership remain
to be designed.

The shipped inline interpreter supports bare `{{ report.total }}` in explicit data snapshots.
Extending this to declarations in later Vue/SFC source requires an explicit compiler transform or
a complete compilation context. Ordinary VitePress compilation does not turn
unknown identifiers into future data slots. Full arbitrary Vue SFC and TS semantics can require
waiting for an entire script or document; early rendering is guaranteed only for independent units
and the defined streaming subset.

Complete producer modules execute at explicit execution boundaries. Rendering reads their published
results. Source reparsing, data invalidation, reconnect, and snapshot restoration must not rerun
producer side effects. Model-authored Markdown does not gain permission to execute scripts, imports,
or arbitrary Vue expressions by passing through the passive Markdown compiler. Explicit live Vue
output uses the separate contract above; it does not register external producers. The implemented
data-template subset is defined above. Automatic producer modules require their own lifetime contract.

## Cross references and invalidation

Two kinds of dependencies invalidate previously displayed units:

- Data dependencies connect bindings to expressions and components that read them. A new binding
  version invalidates the affected units.
- Document dependencies connect definitions, headings, footnotes, component declarations, and other
  source context to their consumers. New source may invalidate earlier units and their compilation.

Reparse conservatively when dependency scope is uncertain. Keep Markdown context available across
unit boundaries so a later reference-style link definition can update an earlier paragraph. Changes
to a heading containing a late value must keep anchors and their consumers consistent, either through
stable explicit anchors or a coordinated revision of definitions and links.

Invalidate enclosing structural units when conditional output, list shape, or component context
changes. Handle dependency cycles with a typed diagnostic rather than indefinite waiting. Compilation
can be refreshed without re-executing the producers that supplied existing data.

## Rendering and update flow

For a paragraph containing a late `report` reference followed by independent prose:

1. Parse the paragraph and assign a unit identity, for example `p17`.
2. SSR its pending presentation and publish it with the current document revision.
3. Append later independent units as source becomes available.
4. When `report` resolves, invalidate `p17` and capture the relevant source and data versions.
5. SSR the affected boundary; commit it only if its captured versions are still current.
6. Publish the update to `p17`. Frontends preserve later units and host interaction state.

Rendering tasks may finish out of order, but publication is serialized by the document owner.
Discard superseded render computations before publication. Each committed update uses a consistent
source and data snapshot. Coalescing computations must not silently drop committed protocol updates.

## Proposed protocol semantics

The minimum operation vocabulary is `snapshot`, `patch`, and `settled`. Names and encoding are
illustrative until the transport and compatibility review fixes the wire contract.

| Operation | Semantics |
|---|---|
| `snapshot` | A complete bounded document projection at one generation and revision, including unit identities and referenced resources. Used on first mount and recovery. |
| `patch` | A committed transition from one document revision to the next, containing ordered insertion, replacement, and removal operations. Apply the transition as one logical update. |
| `settled` | A revision-scoped statement that source is sealed and all owned producers and render work for that scope are terminal. It can include failed or cancelled boundaries. |

The contract must correlate session, run, message, and document identity where applicable. It needs
a protocol version, document generation, source version, and presentation revision; patches identify
their base and resulting revisions. The generation changes on reconstruction or invalidating reset,
so old process results cannot affect a replacement document.

Receivers recognize already-applied updates, reject stale generations, and request a snapshot on a
revision gap. Recovery must establish the snapshot revision and subsequent subscription without a
race that loses updates. Unknown fields remain tolerated, and unsupported operations trigger explicit
recovery or fallback. Bounded queues and replay retention require an explicit backpressure and gap
response; updates are never silently discarded.

Presentation revisions are distinct from the existing run-local event sequence. Agent completion
does not imply that asynchronous rendering has finished. Do not append rendering events after the
terminal frame on the current run stream or delay Agent completion behind an unrelated producer.
The protocol review must define an independently owned presentation stream or another explicit
lifecycle that permits late rendering. Source sealing, producer completion, run termination, and
presentation settlement remain separate signals; cancellation must settle owned pending work.

The executable exposes frontend routes through its existing host. The Deno service uses deliberately
designed internal HTTP or WebSocket endpoints with explicit direction, authentication, limits,
cancellation, and error semantics. Neither this document nor the internal service design adds a second
public executable web server or assumes one multiplexed IPC bus. The VS Code bridge owns transport;
the notebook renderer continues to obey host CSP restrictions.

## Frontend application and resources

SSR produces HTML for passive content. The frontend applies versioned changes inside owned unit
boundaries, using a DOM merger if validated. Verify focus, selection, expanded details, copy controls,
and scroll position under updates rather than relying on replacement of whole output trees.

Interactive components use explicit client-owned islands with matching server and client code and
initial state. DOM merging must not traverse a subtree managed by Vue or another host-owned runtime.
Define mount, update, and disposal through that subtree's adapter. SSR alone does not install event
handlers or make a VitePress component interactive.

Bundle styles, component assets, and highlighter resources offline. Resolve images and other resources
through the product resource and object contracts with frontend-specific URL adaptation. Binary
payloads stay binary; snapshots and patch metadata carry references rather than base64. Do not retain
server-local asset URLs as portable notebook identities. Host theme changes should use CSS variables
where possible without requiring document recomputation.

## Lifetime and failure behavior

The executable owns the rendering service's startup, cancellation, completion task, process exit, and
deterministic disposal. Document scopes own their producers, render tasks, subscriptions, and retained
data. Deleting a document or closing its owning scope cancels and joins its work. A frontend disconnect
detaches that subscriber; it does not implicitly destroy a document used by another frontend.

Launch through the existing permission machinery with an owning scope's captured effective policy
and allowlisted environment. Do not inherit the plugin host's exceptional full-permission launch.
Bound source, binding values, dependency state, render output, queues, and retained revisions. Observe
background failures and use one total budget for multi-stage shutdown without abandoning live owners.

A recoverable unit failure produces a local diagnostic and leaves independent content visible. A
renderer crash invalidates its generation, fails or cancels pending operations exactly once, and lets
the executable expose raw Markdown or a typed unavailable state. Reconstruction uses retained results
without repeating side effects. Sanitize output and validate URLs under the content policy; server
rendering does not make model content or raw HTML trusted.

### Renderer generation recovery and runtime process controls (implemented 2026-11-06)

The executable continuously observes each renderer process through an owned generation task.
Every generation has fresh readiness state, nonce, process reference, port and cancellation source.
Unexpected completion invalidates its port, cancels its active request and records a Warning with
PID/exit status or failure category. The observer and request-side completion check coordinate one
exit warning, including the race where a request notices completion before the observer is scheduled.
Retiring an old generation cannot clear or authenticate readiness for its replacement.

A failed launch, readiness failure or transport failure retires that attempt without a permanent
failure latch. The next admitted rendering request may start a new child, after the old child's
process/observer resources are joined. The failed request is not automatically replayed. This is
on-demand recovery, not an autonomous restart loop or re-execution of an external producer. Each
launch still captures policy through the existing permission module; no start builds its own grants.
Caller cancellation preserves a healthy service. Shutdown closes admission, cancels and drains all
admitted requests, then joins the generation before disposing HTTP/semaphore/token resources and
removing staging files. Retirement cancels its observer wait before the supervised process's bounded
stop, so an inherited output pipe cannot turn observer joining into an unbounded shutdown wait.

**Worker permissions do not guarantee that the renderer process remains alive.** Real process tests
on macOS arm64 with the CI-pinned Deno 2.9.6 and local 2.9.8 establish these separate behaviors:
`Deno.exit()`/worker close end the worker; Node's worker exit path and exit-code attempts do not kill
the parent renderer. A missing private reply becomes a bounded component failure. However,
`Deno.kill(Deno.pid, "SIGKILL")` is permitted as a self-signal and can terminate the entire Deno
renderer despite `permissions:"none"`. That request becomes typed unavailable and the next request
starts a fresh generation. An isolated kernel-process smoke test verifies that the worker cannot
signal the kernel PID, and also verifies recovery after the worker signals its own renderer PID.
These facts replace any implication that no I/O permissions means no process-control effects.
Per-evaluation OS-process separation would be required to contain that self-signal below the shared
renderer; it is not implemented or claimed here. The existing worker boundary is not a complete
untrusted-code or memory/liveness sandbox.

Regression coverage includes transient executable failure followed by success, repeated unexpected
exit and fresh readiness, exit during an active request, caller cancellation without retirement,
concurrent disposal with active/queued requests and post-close rejection, worker exits/self-signals,
and a separately owned kernel target. Assertions wait on generation completion or protocol replies,
not sleeps. Platform test jobs run the pinned runtime and retain their own outcome.

The Deno CI job now rebuilds and runs `git diff --exit-code --stat -- bundle/` from the renderer
module before offline smoke. Both committed service and embedded worker-source artifacts must match;
the .NET job can no longer silently embed a stale committed service while source tests pass against
a freshly overwritten file. The check has been exercised with a deliberately modified artifact
(rejected) and a clean Deno 2.9.6 rebuild (accepted). Renderer wire/API and notebook formats are unchanged.

The recovery increment passes 17 focused renderer integration cases on pinned Deno 2.9.6. The full
.NET suite on local Deno 2.9.8 passes 1,596 tests with eight existing platform/external-service skips
(1,604 total). All 708 Deno tests plus 20 Node scenarios pass on 2.9.6; its rebuild/freshness/offline
checks, format, lint, types and diff checks pass. osx-arm64 NativeAOT publish and five real-process
rendering/signal-boundary smoke tests pass with the pinned runtime. InspectCode reports no errors
and the two existing test warnings. Linux/Windows behavior is delegated to the PR platform jobs;
no frontend UI contract changed in this increment.

## Persistence and compatibility

Canonical history keeps provider-neutral source and structured results. HTML is derived presentation.
Saving a notebook never mutates the live document or session. A portable snapshot retains source and
may retain a versioned frozen presentation with the resources needed for offline display; the exact
schema and migration policy are pending. Restoring it must not execute embedded producer code.

Keep the existing Markdown path available for clients that do not support the new presentation
capability. Define discovery, resource compatibility, sanitization, and fallback before changing the
wire or notebook format. Update representative fixtures and `web-frontend-protocol.md` when those
contracts ship. The shipped optional rendering capability and routes are additive to protocol v1;
the notebook snapshot format is unchanged. Revisioned presentation snapshots and patches remain proposed.

## Implementation stages and acceptance

| Stage | Status | Scope | Acceptance |
|---|---|---|---|
| 1 | In progress; passive core and offline bundle implemented | Deno and package prototype | Render representative VitePress content offline with pinned dependencies; verify compiler, highlighter, resources, and host CSP. Compare Markstream partial parsing with the selected semantics. |
| 2 | Assistant SSR, code presentation, TOC/anchors and extracted containers/alerts implemented; broader content/resource compatibility pending | Assistant Markdown replacement | Share SSR output through the executable projection and existing timeline boundaries; retain raw Markdown fallback and offline readability. |
| 3 | Pending | Streaming document updates | Implement stable identities, versioned snapshots and patches, syntax buffering, cross-reference corrections, and final reconciliation. |
| 4 | Data-only expressions and explicit binding snapshots implemented; automatic producer ownership/subscriptions pending | Late data bindings | Add explicit binding states and authorized producers; verify conditional boundaries, local failures, cancellation, cycles, and stale-result rejection. |
| 5 | Explicit complete Vue components, scoped CSS, isolated SSR and client hydration implemented; broader output integration pending | Interactive components and broader output | Add isolated client component lifetimes, then evaluate reasoning summaries, tool presentation, and other rich output separately. |

Completion requires implementation and evidence, not a design-only checkbox.
Focused regression coverage must include a late binding updating an earlier unit while later prose
remains, late link definitions, incomplete expressions and code fences, ready `undefined` versus
pending, obsolete asynchronous renders, duplicate and missing patches, snapshot recovery during live
publication, renderer failure, cancellation, and offline restoration without side effects. Final
streamed output must match a one-shot render under the same source, retained data, and content policy.
Use explicit barriers and completion signals to control interleavings, never sleeps as correctness
assertions. Lifecycle and protocol changes require their owning integration tests and fixtures.

The 2026-11-04 upstream-content increment passes 25 renderer tests, 676 Deno workspace tests plus
20 Node scenarios, and the actual VS Code extension-host suite against a live NativeAOT kernel.
Browser coverage verifies scoped TOC targets, scroll/focus, source-replacement/disposal cleanup,
existing code-group switching and selection retention, and production notebook serializer routing.
The .NET suite passes 1,579 tests with eight existing platform/external-service skips (1,587 total).
Release build completes without warnings; osx-arm64 NativeAOT publish and its real offline-renderer
smoke pass. Offline smoke, type checks, formatting, lint, source provenance hashes, and diff checks pass.
These checks validate the shipped snapshot path, not the proposed document runtime or late bindings.

The 2026-11-05 template increment passes 38 renderer/interpreter tests, 691 Deno workspace tests
plus 20 Node scenarios, and the actual VS Code extension-host suite against the final NativeAOT
kernel. Browser coverage includes binding-only pending-to-ready updates, later prose remaining,
production template-MIME restoration and ordinary Markdown compatibility. The .NET suite passes
1,581 tests with eight platform/external-service skips (1,589 total). Release build has no warnings;
osx-arm64 NativeAOT publish and both published Markdown/template smoke tests pass. The same pinned
InspectCode version as CI reports no error-level findings; existing non-gating warnings are tracked
in its SARIF output. Type/format/lint, offline smoke and diff checks pass. These results validate
snapshot interpolation, not automatic producer lifecycle or revisioned patches.

The 2026-11-05 live-component increment passes 46 renderer/interpreter/worker tests and 702 Deno
workspace tests plus 20 Node scenarios. The .NET suite passes 1,583 tests with eight existing
platform/external-service skips (1,591 total). Release build has no warnings; osx-arm64 NativeAOT
publish and all three published Markdown/template/Vue smoke tests pass. Real VS Code tests against
the NativeAOT kernel verify the live notebook MIME route, actual component hydration, reactive
clicks, scoped styles and unmount on replacement/disposal. Worker tests verify denied filesystem,
network and environment access, private reply-channel integrity, cancellation, the five-second
non-cooperative-setup deadline, and recovery on the next request. Read/write notebook fixtures verify
executable source removal without losing fallback or mutating live output. Type/format/lint, frozen
lock installation, offline smoke and diff checks pass. InspectCode reports no error-level findings
and the two existing non-gating test warnings. These checks validate complete live component
snapshots, not automatic producer subscriptions or revisioned streaming patches. Linux and Windows
verification is delegated to the PR's platform jobs; local evidence is macOS arm64.

The 2026-11-06 presentation fix adds browser regressions with explicit native-render and reply-delivery
barriers: a committed SSR subtree remains connected throughout replacement preparation, stale replies
leave it intact, unavailable rendering commits the latest complete fallback, duplicate Vue delivery
preserves reactive state, and a changed-source Vue app remains interactive until the replacement
commits. These assertions fail on the previous implementation. The change is frontend-local and
introduces no wire, IPC, notebook format or executable change. Release build passes without warnings,
all 702 Deno tests and 20 Node scenarios pass, and the .NET suite passes 1,583 tests with the eight
existing platform/external-service skips. Real VS Code verification uses the installed NativeAOT
kernel. Format, lint, types and diff checks pass. NativeAOT is not republished for this frontend-only
fix. Full subtree diffing and stable streaming document nodes remain separate work.

## Open decisions

- Which additional VitePress content/compiler capabilities are useful within an output document, and
  can those additions retain offline packaging and independent ownership?
- How should authorized producer/component registration extend the shipped data-expression subset
  with automatic late binding, cancellation settlement and stable document scope?
- Which revisioned presentation transport provides independent lifetime and recovery beyond the shipped stateless API?
- What are the document-runtime limits, overflow behavior, reconstruction policy, and authorized producer permission scope beyond the implemented passive rendering limits?
- Which styles, resources, and interactive components can ship inside notebook host constraints?
- What optional presentation snapshot format supports portability and older clients?

## Documentation maintenance

Update this document in the same change as related rendering implementation or design evolution.
Keep the direction, current integration boundary, package choices, lifecycle rules, protocol semantics,
open decisions, and stage acceptance evidence aligned with the resulting system. Replace superseded
proposals in place rather than accumulating conflicting alternatives. Update the last-updated date
when substantive decisions or implementation status change.

Shipped wire and persisted-format details belong in their normative protocol documentation and
fixtures as well. Record consequential architecture decisions in an ADR when adopted and link them
here; do not label proposed operations or unverified package compatibility as shipped behavior.

## Upstream references

Sources consulted on 2026-11-04. Recheck the selected versions during the package prototype.

- [VitePress overview](https://vitepress.dev/guide/what-is-vitepress) and
  [Vue in Markdown](https://vitepress.dev/guide/using-vue): site generation and SFC compilation model.
- [VitePress 1.6.4 Markdown renderer](https://github.com/vuejs/vitepress/blob/v1.6.4/src/node/markdown/markdown.ts)
  and [mdit-vue](https://github.com/mdit-vue/mdit-vue): renderer composition and reusable plugins.
- [VitePress 1.6.4 code highlighting](https://github.com/vuejs/vitepress/blob/v1.6.4/src/node/markdown/plugins/highlight.ts)
  and [containers](https://github.com/vuejs/vitepress/blob/v1.6.4/src/node/markdown/plugins/containers.ts):
  matching-version notation transforms and code-group source syntax. Maieutics owns its scoped native
  control markup, title escaping, bounded metadata parsing, and host styles.
- [Vue SFC compiler](https://github.com/vuejs/core/blob/main/packages/compiler-sfc/README.md),
  [SSR API](https://vuejs.org/api/ssr.html), and
  [server reactivity](https://vuejs.org/guide/scaling-up/ssr.html#reactivity-on-the-server): compilation
  utilities, HTML rendering, and the absence of an automatic persistent SSR update loop.
- [Shiki Stream](https://shiki.style/packages/stream) and
  [highlighter lifetime](https://shiki.style/guide/best-performance): incremental highlighting,
  recalls, reuse, and explicit disposal.
- [VitePress theme customization](https://vitepress.dev/guide/extending-default-theme): styles,
  theme variables, and application-level integration.
- [Markstream parser](https://markstream.simonhe.me/guide/parser) and
  [Markstream SSR integration](https://github.com/Simon-He95/markstream-vue#ssr--worker-usage-deterministic-output):
  partial parsing and structured-node integration candidates.
- [Idiomorph](https://github.com/bigskysoftware/idiomorph): DOM matching, update hooks, and focus handling.

Frontmatter reference consulted 2026-11-10: [VitePress frontmatter](https://vitepress.dev/guide/frontmatter).
