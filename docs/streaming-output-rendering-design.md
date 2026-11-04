# Streaming Output Rendering with VitePress

Status: Design direction agreed; runtime, protocol, and package integration proposed. Not implemented.

Date: 2026-11-04

Last updated: 2026-11-04

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
output across frontends. The package combination, authoring syntax, transport endpoints, wire schema,
permission profile, and process topology require the validation stages below. Examples here describe
proposed semantics and do not extend the shipped frontend protocol.

## Existing integration boundary

The VS Code frontend currently projects events into ordered timeline entries in
`deno/maieutics-vscode/turnTimeline.ts`. `timelineOutputs.ts` assigns independent output identities and
retains unchanged output payloads. `media/turnRenderer.js` rebuilds the changed output's DOM and delegates
Markdown to VS Code's default Markdown renderer, with the protections in `markdownSafety.js`.

The new renderer should first replace assistant Markdown inside these existing output boundaries.
Tool activity, permitted reasoning summaries, rich MIME values, widgets, and interactive view families
retain their owning contracts until separately adapted. The Preact view-family lane in ADR 0038 does
not implicitly become a Vue application.

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
authorized producer results. Rendering must not execute through or depend on the user's REPL.

## Reusable ecosystem capabilities

| Capability | Candidate | Integration role and limit |
|---|---|---|
| VitePress content semantics | `createMarkdownRenderer` behind a local adapter | Reuse Markdown extensions, code groups, containers, links, and code markup. The exported API is experimental; lock the chosen version. Its output can still contain Vue template syntax. |
| Markdown and Vue integration | `@mdit-vue` component, SFC, frontmatter, headers, title, and TOC plugins | Compose these directly if the VitePress adapter proves too dependent on site context. They do not provide late binding or incremental document lifetime. |
| Vue compilation | `vue/compiler-sfc` and Vue template compilers | Compile complete components or templates and support binding transforms. Import resolution, TS execution, resources, and producer lifetime remain separate concerns. |
| SSR | `vue/server-renderer` | Render a presentation unit against a captured data snapshot. Streaming one SSR result does not update HTML already delivered. |
| Code highlighting | Shiki and `@shikijs/stream` | Reuse a highlighter and incrementally highlight code; account for recall tokens when later context changes earlier highlighting. |
| Content styling | VitePress content styles and Shiki theme variables | Bundle selected styles and adapt variables to the host. Default-theme components may depend on VitePress page context and browser behavior. |
| DOM application | Idiomorph or morphdom | Merge HTML inside an explicitly owned boundary. Focus, selection, expansion, and scrolling still need host verification. |
| Incomplete Markdown | Markstream and `stream-markdown-parser` | Evaluate partial syntax handling and structured nodes. Their parser composition is not established as equivalent to the selected VitePress pipeline. |

The preferred initial stack is a VitePress renderer adapter, the Maieutics document runtime, Vue SSR,
and a small HTML update client. Direct `@mdit-vue` composition is an alternative adapter strategy.
Markstream is a comparison candidate, not an additional authoritative parser running beside VitePress.

Package reuse must be checked in the supported Deno runtime with packaged, offline dependencies.
Node compatibility, compiler imports, Shiki assets, stylesheet packaging, and notebook CSP compatibility
are acceptance conditions rather than assumptions. Vite is useful for building shipped assets; the
production design must not require a development server or site rebuild for each text delta.

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

This component and syntax are proposed. The runtime resolves the binding and supplies its ready
value through the slot. The producer registration and publication API remains to be designed.

Supporting bare `{{ report.total }}` with a declaration that arrives later requires an explicit
compiler transform or a complete compilation context. Ordinary VitePress compilation does not turn
unknown identifiers into future data slots. Full arbitrary Vue SFC and TS semantics can require
waiting for an entire script or document; early rendering is guaranteed only for independent units
and the defined streaming subset.

Complete producer modules execute at explicit execution boundaries. Rendering reads their published
results. Source reparsing, data invalidation, reconnect, and snapshot restoration must not rerun
producer side effects. Model-authored Markdown does not gain permission to execute scripts, imports,
or arbitrary Vue expressions by passing through a template compiler. The executable template subset
and authorized component or producer registration require an explicit contract before implementation.

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
contracts ship. This design does not currently change protocol v1 or notebook snapshots.

## Implementation stages and acceptance

| Stage | Scope | Acceptance |
|---|---|---|
| 1 | Deno and package prototype | Render representative VitePress content offline with pinned dependencies; verify compiler, highlighter, resources, and host CSP. Compare Markstream partial parsing with the selected semantics. |
| 2 | Assistant Markdown replacement | Share SSR output through the executable projection and existing timeline boundaries; retain raw Markdown fallback and offline readability. |
| 3 | Streaming document updates | Implement stable identities, versioned snapshots and patches, syntax buffering, cross-reference corrections, and final reconciliation. |
| 4 | Late data bindings | Add explicit binding states and authorized producers; verify conditional boundaries, local failures, cancellation, cycles, and stale-result rejection. |
| 5 | Interactive components and broader output | Add isolated client component lifetimes, then evaluate reasoning summaries, tool presentation, and other rich output separately. |

Each stage starts pending. Completion requires implementation and evidence, not a design-only checkbox.
Focused regression coverage must include a late binding updating an earlier unit while later prose
remains, late link definitions, incomplete expressions and code fences, ready `undefined` versus
pending, obsolete asynchronous renders, duplicate and missing patches, snapshot recovery during live
publication, renderer failure, cancellation, and offline restoration without side effects. Final
streamed output must match a one-shot render under the same source, retained data, and content policy.
Use explicit barriers and completion signals to control interleavings, never sleeps as correctness
assertions. Lifecycle and protocol changes require their owning integration tests and fixtures.

## Open decisions

- Which pinned VitePress version and adapter boundary work in the supported Deno runtime?
- Which template subset and producer registration model permit safe late binding while preserving scope?
- Which internal endpoints and frontend presentation transport provide independent lifetime and recovery?
- What are the exact limits, overflow behavior, reconstruction policy, and renderer permission scope?
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
