# Streaming Output Rendering with VitePress

Status: Passive SSR integrated with the kernel and actual Notebook renderer; upstream TOC, anchors,
containers, and alerts integrated. Explicit data-only template snapshots and interpolation are implemented;
the streaming document runtime and revisioned presentation protocol remain proposed.

Date: 2026-11-04

Last updated: 2026-11-05

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
Full SFC/script authoring, the document runtime, and revisioned update operations remain proposed and
require the validation stages below. The initial demo API has its own normative frontend contract.

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

The initial renderer replaces assistant Markdown inside these existing output boundaries.
Tool activity, permitted reasoning summaries, rich MIME values, widgets, and interactive view families
retain their owning contracts until separately adapted. The Preact view-family lane in ADR 0038 does
not implicitly become a Vue application.

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
heading metadata, TOC and shared utilities, markdown-it-anchor 9.2.0, markdown-it-attrs 4.3.1,
markdown-it-container 4.0.0, Shiki and `@shikijs/transformers` 2.5.0, and Vue 3.5.13 SSR. It supports a selected passive content subset rather than the full VitePress page
compiler. The complete-snapshot update client is implemented; the document runtime and incremental
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

Unknown languages fall back to escaped text. Raw HTML, document scripts/styles and directives remain text. Ordinary Markdown keeps Vue
interpolations literal; the explicit template path below evaluates the defined data expression subset. No filesystem snippet or include rules are installed. Complete snapshots
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
nonce-bound readiness callback reports the child's actual loopback port. The internal child endpoint
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

The browser first finishes native rendering, then requests SSR. A matching response replaces the
assistant text region, including its scoped stylesheet, and emits a local application acknowledgement.
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

The remaining content gaps are frontmatter and metadata,
additional bundled languages, and product resource URL adaptation. File includes and snippet imports
need an explicit authorized resource contract; arbitrary SFC/template execution remains stage 4/5.

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
cycles between producers, generic SFC/script modules and document patches remain future document-runtime
work. The stateless renderer does not claim ownership of an external producer's lifetime.

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
or arbitrary Vue expressions by passing through a template compiler. The implemented data template subset is defined above. Authorized component registration and
producer modules require their own contract before implementation.

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
initial state. DOM merging must not traverse a subtree managed by Vue, Preact, or a widget runtime.
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
| 5 | Pending | Interactive components and broader output | Add isolated client component lifetimes, then evaluate reasoning summaries, tool presentation, and other rich output separately. |

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

## Open decisions

- How much VitePress page/compiler compatibility is needed beyond the composed passive subset, and
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
