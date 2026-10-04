# Custom UI Framework Design

<<<<<<< HEAD
Status: Accepted for stages 0–1b (implemented 2026-10-04); later stages
=======
Status: Accepted for stages 0–1a (implemented 2026-10-04); later stages
>>>>>>> origin/main
remain design-only.

Date: 2026-10-04

Related: ADR 0024 (frontend comm plane and widgets), ADR 0021 (plugin HTTP UI
gateway), ADR 0037 (plugin declaration approval), ADR 0023 (custom web
frontend protocol), `docs/web-frontend-protocol.md`,
`docs/declarative-extensions-design.md`, `docs/agent-ux-gap-analysis.md`

## Problem

Custom UI in Maieutics is growing along four lanes with no shared pattern:

1. **REPL widgets** — the one complete custom-UI pipeline: TSX cells author
   ipywidgets-compatible models that render in the notebook and stay live
   over the comm plane (ADR 0024). It works, but its renderer is hard-coded
   to classic controls and its SDK is bound to the REPL worker only.
2. **Built-in features** — elicitation forms, plugin approval, queue and
   history affordances each hand-roll their presentation (QuickPick,
   InputBox, status-bar, ad-hoc markdown), with no way to ship a richer
   interactive surface through the same live-model machinery the widgets
   already use.
3. **Plugin UI** — plugin workers have no presentation surface at all
   (ADR 0020 invariant 25: no comm surface for extensions). The ADR 0021
   HTTP gateway is implemented host-side but invisible: the kernel does not
   know the gateway address or entrance token, no endpoint surfaces plugin
   pages, and nothing renders inside the product.
4. **Future frontends** — a sidebar webview or a browser SPA would have to
   reinvent model state, transport, and rendering from scratch.

The question this design answers: what is the **one framework** through
which all custom UI — built-in features, REPL/TSX code, and plugins — is
authored, announced, transported, and rendered? The widget pipeline is the
precedent to generalize, not a special case to keep isolated.

A sub-question with a decision of its own: for UI that is *served over
HTTP* (the ADR 0021 web path), should pages be server-rendered (SSR) or
client-rendered? §7 rules on it per lane.

## Current state (verified)

The widget pipeline end to end, layer by layer:

| Layer | Today | Files |
|---|---|---|
| Authoring | JSX compiles with the preact automatic runtime shape (`jsx`/`jsxs` producing `{type, props, key}` vnodes, `jsxImportSource: "maieutics-widgets"`); control elements become widget models directly; `@preact/signals-core` powers the SDK's reactive core | `deno/maieutics-plugin-sdk/widgets/jsx-runtime.ts`, `vnode.ts`, `transform.ts`, `deno/maieutics-plugin-sdk/reactive.ts` |
| Model runtime | `WidgetRuntime`: transport-neutral registry of models — `state` + `sync(key, value)` (single-key `comm_msg` update) + `onChange` (uplink deltas); ipywidgets classic wire shapes (`comm_open` target `jupyter.widget`, `comm_msg {method:"update", state, buffer_paths}`) | `deno/maieutics-plugin-sdk/widgets/runtime.ts`, `index.ts` |
| Host binding | The REPL worker injects the transport at bootstrap: `bindWidgetHost({broadcast, onComm})` wired to `Deno.jupyter.broadcast` and the `maieutics.comm` proxy; API exposed as `maieutics.widgets` | `deno/maieutics-deno-repl/repl_worker.ts:176-191` |
| Announcement | Display mime `application/vnd.jupyter.widget-view+json` `{model_id, version_major, version_minor}` inside the `repl.display` mime bundle | `runtime.ts` `mimeBundle()`; renderer contribution in `deno/maieutics-vscode/package.json` |
| Transport | Session-scoped full-duplex WS `GET /v1/agent/sessions/{sid}/comms`, fixed binary codec with native buffers, 8-byte envelope sequences, `sinceSeq` resume, hello carries the live-comm snapshot; bounded replay (4096) and per-subscriber queues (1024), overflow closes `1011 backpressure` | ADR 0024; `deno/shared/comm_codec.ts`, `Maieutics/Frontend/FrontendHost.cs` (`HandleComms`), `Maieutics/Frontend/FrontendComm.cs` (`FrontendCommRouter`, `FrontendCommStream`) |
| Host bridge | `WidgetBridge` connects renderer messaging to the comms socket; caches latest state per model id so a late-mounting renderer gets current state; merges `update` deltas last-wins; lazily opens the socket on first mount | `deno/maieutics-vscode/widgets.ts` |
| Renderer | Dependency-free plain-DOM script; dispatches on `_model_name` (sliders, text, toggle, button, box); nested `IPY_MODEL_<id>` children resolve recursively; unknown models degrade to live state JSON | `deno/maieutics-vscode/media/widgetRenderer.js` |

Drift worth reconciling: ADR 0024 decision 7 designed the announcement as
`application/vnd.maieutics.widget-view+json` `{commId, version, esm?, css?,
state}` with esm/css traveling as content-addressed `$object` references.
The implementation shipped the Jupyter-compatible mime `{model_id, …}`
instead, and the esm/css members were never implemented. This design keeps
the Jupyter mime for compatibility and realizes the native announcement
under its own name (§2), retiring the never-shape that ADR 0024 sketched.

Gaps this design closes:

- The renderer dispatches on ipywidgets `_model_name`; there is no
  component registry, so a new view kind means editing the renderer.
- `WidgetRuntime`/`WidgetBridge` are widget-named and widget-shaped; other
  producers cannot reuse them without reaching into widget semantics.
- Only the REPL can publish comm models. Kernel-side C# producers and
  plugin workers have no path onto the comm plane.
- The ADR 0021 gateway lane exists host-side but is dark: no kernel
  wiring, no discovery, no embedding.

## Design overview

One pipeline, five layers, exactly the widget pipeline's shape — each layer
generalized from "widget" to "custom UI view":

```
producers                    announcement              transport            bridge + renderer
┌────────────────────┐   ┌──────────────────────┐   ┌──────────────┐   ┌─────────────────────┐
│ REPL / TSX cells   │   │ repl.display mime    │   │ comm plane   │   │ UiBridge (per host) │
│ built-in C# code   │──>│ bundle member        │──>│ (ADR 0024,   │──>│ component registry  │
│ plugin workers     │   │ vnd.maieutics.view   │   │  unchanged)  │   │ Preact components   │
│ plugin HTTP pages  │   │ +json                │   │              │   │ per view family     │
└────────────────────┘   └──────────────────────┘   └──────────────┘   └─────────────────────┘
        │                        │                      │                     │
        └── model contract: state + sync/onChange ─────┴── frames are family-agnostic envelopes, family-specific payloads
```

- A **view family** names a component set and its state dialect.
- A **model** is producer-side state synced bidirectionally over the comm
  plane; the announcement (a display mime member) tells frontends a model
  exists, which family renders it, and where optional component payloads
  live.
- The **transport** is the existing comm plane, byte-for-byte unchanged.
- The **renderer** dispatches through a component registry keyed by family,
  with Preact as the component runtime.
- The **bridge** is the per-host adapter (notebook renderer messaging
  today; a sidebar webview or browser SPA later) that caches state and
  pumps frames.

## 1. View families and the model contract

### View family

A **view family** is a kernel-defined closed-catalog string that names:

- the **component set** a frontend needs to render the model's states, and
- the **frame payload dialect** for that family's `comm_msg` traffic.

The catalog starts with two entries and grows the way
`PluginExtensionKind` grows (kernel release events, unknown entries
degrade — `docs/declarative-extensions-design.md`):

| Family | Purpose | Dialect |
|---|---|---|
| `jupyter.widget` | The existing ipywidgets classic controls; compatibility family, kept forever | ipywidgets classic: identity fields `_model_name`/`_view_name`, `comm_msg {method:"update", state, buffer_paths}` |
| `maieutics/form` | Structured declarative forms (fields, choices, submit/cancel) — the first native family; targets elicitation surfaces and plugin settings | Native: `comm_msg {method:"update"|"event", state|event, buffer_paths}` — same envelope shape as widgets, family-defined payload schema |

Families after that (each its own stage): `maieutics/iframe` (embeds an
ADR 0021 gateway page), `maieutics/progress` (long-task surfaces), rich
per-feature families as built-in UI demand appears.

### Model contract (producer side)

`WidgetRuntime` generalizes to a UI model runtime with the same shape —
transport-neutral, injected broadcast + subscription:

```ts
interface UiModelRuntime {
  register(input: {
    family: string;            // catalogued view family
    state: Record<string, unknown>;
    onChange?: (key: string, value: unknown) => void;   // uplink deltas
    onEvent?: (name: string, payload: unknown) => void; // uplink events (form submit etc.)
  }): UiModel;                   // { commId, get, set, sync, announce() }
  handleIncoming(frame: IncomingCommMessage): void;
  remove(commId: string): void;
}
```

- `sync(key, value)` emits the family's update dialect; `announce()`
  returns the family's display mime member (§2).
- The `jupyter.widget` family is a thin adapter over today's
  `WidgetRuntime` semantics — same wire bytes, so the current renderer and
  any committed notebook keep working.
- Family payload schemas are validated on both ends; a malformed frame is
  ignored (invariant 18 — the kernel keeps running; today's
  `handleIncoming` already no-ops unknown shapes).
- Model lifetime follows comm lifetime: registration broadcasts the family's
  `comm_open`, frontend close removes the model, replay re-sends frames
  verbatim, state merges are last-wins (all inherited from ADR 0024 and
  already proven by widgets).

## 2. View announcement (display mime family)

A view becomes visible the way widgets do today: a **display mime bundle**
member over `repl.display` event frames, pairing with the comm channel by
model id (no cross-socket ordering requirement — ADR 0024 consequence).

Two members, one per mime:

1. **Compatibility member** — `application/vnd.jupyter.widget-view+json`
   `{model_id, version_major, version_minor}`. Unchanged. The
   `jupyter.widget` family keeps emitting exactly this; committed notebooks
   and stored snapshots keep rendering.

2. **Native member** — `application/vnd.maieutics.view+json`:

   ```jsonc
   {
     "modelId": "<uuid>",          // pairs with the comm channel
     "viewFamily": "maieutics/form",
     "version": "1.0",             // family protocol version
     "state": { … },               // display-time state: seeds the view and
                                   // is what a snapshot restore renders
     "esmSource": "…",             // optional bundled component source (1b; ≤1 MiB;
                                   // stripped from persisted snapshots)
     "cssSource": "…"              // optional stylesheet scoped to the family
   }
   ```

   - `state` is the display-time snapshot: it seeds the view before (or
     instead of) live comm state, and the frozen view a reopened notebook
     renders.
   - `esmSource`/`cssSource` realize what ADR 0024 decision 7 sketched for
     esm/css, delivered as inline text in stage 1b (the notebook webview
     CSP admits no URL-based module sources — §4): a module that registers
     its family's components in the renderer, plus its scoped stylesheet.
     The serializer strips both when persisting snapshots (untrusted
     notebook files carry no executable renderer code). The `$object`
     content-addressed form remains the production path for larger
     payloads once a cell-side upload surface exists.
   - Both are optional. Native families with built-in components
     (`maieutics/form` ships in the renderer) omit them.

Frontends that do not know the native mime ignore it (invariant 17); the
comm frames that follow are inert without the announcement.

## 3. Transport: the comm plane, unchanged

ADR 0024's plane serves as-is — session-scoped full-duplex WS, binary
codec, envelope sequences, `sinceSeq` resume, live snapshot in hello,
bounded replay and per-subscriber queues, `comm.open` REPL-originated only
(loosened per-producer in §6, never per-frontend).

The only transport-adjacent change is **target-name namespacing**:
`comm_open.target_name` gains values beside `jupyter.widget`:

- `jupyter.widget` — compat family, verbatim.
- `maieutics.view/<family>` — native families (e.g. `maieutics.view/maieutics.form`).

The relay (`FrontendCommRouter`) is target-agnostic today and stays that
way; targets matter to producers and the renderer registry, not to the
pipe. Capability negotiation stays additive: the existing
`capabilities.comm {version, maxMessageBytes}` covers the plane; native
families add a `capabilities.ui {version, families: string[]}` object so a
frontend can declare which families it renders (§10).

## 4. Renderer: component registry and Preact

### The registry

`widgetRenderer.js`'s `kindOf()` dispatch becomes an explicit registry:

```js
registerViewFamily("jupyter.widget", { mount(container, model, ctx) { … } });
registerViewFamily("maieutics/form", { mount(container, model, ctx) { … } });
// unknown family → live state JSON fallback (today's behavior, kept)
```

- Components are **Preact**, matching the producer side: the SDK already
  compiles JSX with the preact automatic runtime shape and uses
  `@preact/signals-core`. One component model across the wire means a
  TSX-authored widget and a renderer-side component speak the same idiom,
  and signals-based state binding works on both ends.
- The classic-controls DOM of today's `widgetRenderer.js` ports to Preact
  components one-to-one (slider, text, toggle, button, box, fallback
  state view); behavior, theming (`var(--vscode-*)`), and the
  `IPY_MODEL_` child resolution carry over.
- **Bundled families**: an announcement carrying `esm` makes the bridge
  fetch the object bytes and hand the source text to the renderer, which
  materializes it as an inline module (or eval shim) and lets it register
  its family. The delivery path follows VS Code's actual notebook-webview
  CSP (verified — Appendix A): the CSP VS Code applies when
  `notebook.experimental.enableCsp` is on allows
  `script-src … 'unsafe-inline' 'unsafe-eval'` but lists no `blob:` or
  `data:` module sources, so producer modules cannot be `import()`ed by
  URL — they arrive as text through the bridge and run inline. The
  renderer never opens sockets itself (`connect-src https:` excludes the
  http loopback under CSP-on), which costs nothing: every frame already
  rides the bridge. A spike (roadmap stage 1b) validates both postures —
  today's default (CSP meta absent) and the experimental CSP-on — before
  bundled families ship.

### The build change (deliberate)

Renderer scripts are hand-written dependency-free JS today because there
is no bundler. Introducing Preact requires one: `deno/maieutics-vscode`
gains a `build:media` deno task (esbuild, single-file ESM output into
`media/`, committed like `dist/extension.js` is today). The constraint
"renderer scripts have no bundler" is thereby amended, not violated: the
shipped artifact remains a single self-contained script per renderer; only
its source of truth moves from hand-written JS to a bundled entry point.
`turnRenderer.js` migrates on its own schedule — the task exists for both.

## 5. Host bridge

`WidgetBridge` generalizes to `UiBridge` — same class shape, wider
envelopes, still transport- and vscode-free and unit-tested:

```
renderer → bridge: { source: "maieutics-ui", type: "mount",  modelId }
renderer → bridge: { source: "maieutics-ui", type: "update", modelId, state }
renderer → bridge: { source: "maieutics-ui", type: "event",  modelId, name, payload }
bridge → renderer: { source: "maieutics-ui", type: "state",  modelId, state }
```

- The outbound source follows the model's comm target (`jupyter.widget` →
  `maieutics-widget`, byte-compatible with the old widget bridge; native
  targets → `maieutics-ui`). A model with no cached open (replay
  truncation) posts under both sources; renderers filter by their own.

- One bridge instance per host surface serves every renderer messaging
  channel (the widget renderer's channel today, the native view renderer's
  channel alongside it); the `jupyter.widget` envelopes
  (`source: "maieutics-widget"`) remain accepted for the compat family
  during migration.
- State caching per model id (late mount gets current state), last-wins
  merge, lazy socket open on first mount, and pump ownership are all
  inherited from `WidgetBridge` unchanged.
- The `event` envelope is new: families with actions (form submit) use
  events rather than state keys, so one-shot semantics don't round-trip
  through state. The `jupyter.widget` family doesn't use it.
- **Mount with no live model** degrades to the snapshot-frozen state when
  the announcement came from a reopened notebook: the renderer shows the
  last committed view instead of "Connecting…" forever. (Today's widget
  renderer spins on a dead model; the fix rides the registry.)

## 6. Producers (staged)

### 6.1 REPL / TSX cells (exists; extended)

Today: `maieutics.widgets` exposes classic controls; `bindWidgetHost` in
`repl_worker.ts` wires transport. Extension:

- The generalized runtime is exposed as `maieutics.ui` beside
  `maieutics.widgets` (which stays as the compat facade).
- Custom families register producer-side the way renderer-side does —
  `defineViewFamily(name, {open, update, decode})` — so a TSX cell can
  author beyond the classic catalog.
- `esm`/`css` payloads flow through `Deno.jupyter` object upload (the same
  `$object` path attachments use).

### 6.2 Built-in kernel producers (C#)

The comm plane's downlink seam (`commFrontendSink` → `FrontendCommRouter`)
currently only carries REPL traffic, and uplink routes unconditionally to
`PushCommMessageAsync` (the REPL). Generalization:

- `FrontendCommRouter` (`Maieutics/Frontend/FrontendComm.cs`) learns
  **comm ownership**: a registry entry records
  its owner — `repl` (today), or a kernel-side **UI model host** that C#
  features register models with. Uplink frames route to the owner; the
  REPL is just the first owner, not the only one.
- The kernel-side model host mirrors the producer contract in C#:
  register(state, family) → publishes `comm_open` into the stream;
  uplink deltas/events arrive on a callback. Backpressure and bounds are
  the stream's existing ones — kernel producers are just another publisher.
- Direction discipline (ADR 0024 decision 4) extends rather than breaks:
  `comm_open` remains *kernel-side-originated* (REPL or kernel host);
  an uplink open is still a protocol error.
- First consumers, in impact order: **plugin approval UI** (ADR 0037's
  planned `GET /v1/plugins` + approve/revoke rendered as a live
  `maieutics/form` model instead of a QuickPick chain) and **elicitation
  forms** (ADR 0029 schemas rendered in-notebook while the QuickPick flow
  stays the fallback for hosts without the plane).

### 6.3 Plugin interactive UI (declarative + live)

Two declaration forms, following the declarative-extensions axes (kind =
kernel catalog, form = code and/or data):

- **Code form — a `ui` service extension point** (symmetric to ADR 0021's
  `http`): a plugin worker `provide()`s a handler that receives its
  models' uplink events and emits state updates. Frames relay
  worker → host → kernel (`host.invoke`-style control messages, the same
  path as today's extension-point calls, 30 s invoke timeout) → comm
  router, tagged with the plugin id as owner.
- **Data form — a catalogued `entrypoints` data name** (`ui`): a static
  JSON form the kernel itself interprets into a `maieutics/form` model.
  No worker runs; actions map onto kernel verbs the plugin already has
  grants for (capability-gated invokes). Suited to settings and status
  cards; interactive state needs the code form.

Gating and fingerprint:

- The `ui` **extension-point kind** and the `ui` **data-entry name** are
  new catalog entries in existing generic collections.
  `PluginDeclarationFingerprint.Compute` hashes `extensions` (all kinds,
  canonical JSON — §6 of the fingerprint) and `data` (all names — §7)
  generically: **both forms are fingerprint-covered with zero
  fingerprint-code changes**, verified against
  `Maieutics/Plugins/PluginDeclarationFingerprint.cs` and
  `PluginManifest.cs` (`PluginExtensionKind`, `PluginDataName`).
- Workers with UI providers need a grant from the closed capability
  catalog (`PluginCapabilityCatalog`, today `["tools.invoke"]`) — e.g.
  `ui.models` — deny-by-default; the grant rides the `caps` fingerprint
  section and the approval summary.
- ADR 0037 enforcement applies unchanged: a blocked plugin ships no worker
  config, its extension invokes are refused (`plugin_pending_approval`),
  so its UI models never publish and its live models are dropped on
  revoke (change-revokes: the fingerprint covers the declaration, so
  editing UI code + redeploying under a changed declaration surface
  revokes until re-approval).
- Kernel-side: the blocked-plugin checks that already guard
  `InvokeExtensionPointAsync` and capability grants gain the UI publish
  path — one more enforcement point in `PluginHostManager`, not a new
  gate design.

### 6.4 Plugin web lane (ADR 0021 gateway wiring)

The gateway (`deno/maieutics-plugin-host/http.ts` `HttpGateway`) serves
plugin HTTP handlers today on a host-owned loopback listener with
entrance-token paths, one-root-per-plugin admission, CSP injection on
`text/html`, and no-referrer hygiene — but the kernel cannot see it. The
wiring stage:

1. The host reports the gateway in its control-bus registry snapshot
   (`extension.registry` payload gains a `httpGateway` section: base URL,
   entrance token, per-plugin mounted roots), alongside today's
   extension registrations and worker states.
2. The kernel surfaces it over the frontend API: ADR 0037's planned
   `GET /v1/plugins` carries, per plugin, its approval state and its UI
   page entries (`{path, title}` discovered from the gateway's admission
   registry).
3. Frontends render entries as links and/or **embedded views**: the
   `maieutics/iframe` view family — a native model whose state is
   `{url}` pointing at the gateway (entrance-token path included) —
   rendered as a sandboxed iframe per ADR 0021's embedding posture
   (sandboxed iframe, host CSP, SSE not WS for push). Embedding targets
   the extension's webview surfaces (sidebar `WebviewView`, editor
   `WebviewPanel`), where the extension authors the CSP and `frame-src *`
   admits loopback http — VS Code's own Simple Browser extension is the
   in-product precedent (Appendix A). Notebook-output iframes are
   constrained by VS Code's notebook CSP (`child-src https: data:`), so
   in-notebook gateway embedding waits for an https gateway or a
   bridge-proxied variant.
4. Browser reachability: the gateway URL is loopback + entrance token —
   the same proof-of-possession posture the events/comms sockets use with
   `?token=`; no cookie, no bearer exposure (ADR 0021's model, unchanged).

## 7. Rendering model: state-over-wire vs served pages (SSR ruling)

Not a global choice — it is a per-lane property, and the lanes have
different physics:

**Framework lane (families): client-side, state-over-wire.** Producers
emit state; frontend components render it.

- Replay and `sinceSeq` resume require re-sendable frames: idempotent
  state deltas (last-wins merge) replay cleanly; rendered HTML does not.
- Theming: components style with `var(--vscode-*)`, so the same model
  renders correctly in every theme and every host surface.
- Backpressure is per-frame and bounded (ADR 0024's 4096/1024 with `1011
  backpressure`); state deltas are small and coalesce naturally.
- Notebook snapshots keep the final rendered view (ADR 0024) — a state
  snapshot plus the family id re-renders it exactly.

**Web lane (ADR 0021 pages): SSR-first.** Plugin workers render HTML on
their own endpoints; the frontend links or iframes it.

- Plugin workers are zero-permission and buildless: string-rendering HTML
  (optionally `preact-render-to-string`) is the zero-dependency path;
  shipping a client-side SPA toolchain to every plugin author is not.
- Reachability is the point of the lane: a gateway URL opens in any
  browser, independent of VSCode — SSR pages degrade gracefully there.
- In-VSCode embedding is a proven tier, not a design bet: a
  WebviewPanel/WebviewView can iframe the gateway page directly
  (`frame-src *`, sandboxed iframe — the shipped Simple Browser extension
  does exactly this for arbitrary http URLs), and the framed page is a
  full browser environment: complexity is bounded by the page author,
  not by VS Code (Appendix A). Two real boundaries: `--vscode-*` theme
  variables and the webview API object do not cross the iframe boundary,
  so theming is bridged at the gateway's HTML-injection point (ADR 0021
  already owns a CSP-injection hook on `text/html`) and any
  product-command affordance relays through the parent webview document
  (`command:` URIs are opt-in per `enableCommandUris`).
- Interactivity inside the page is the plugin's own HTTP round trips
  (same-origin through the gateway projection); full client-side apps
  remain possible (the plugin serves static assets) — the lane is
  transport-agnostic, SSR is the recommended default, not a restriction.
- **Hybrid, later stage**: a gateway page can hydrate from a live comm
  model (the page script speaks the same comm plane), for plugins that
  want framework-lane state inside a web-lane shell.

Rule of thumb the docs give plugin authors: **form-like, theme-bound,
notebook-embedded UI → framework lane; app-like, standalone, browser-first
UI → web lane.**

## 8. Security and invariant mapping

| Concern | Mechanism |
|---|---|
| Consent over what UI may appear | Declaration-plane: plugin UI kinds/names are catalogued, deny-by-default, and fingerprint-covered (§6.3); ADR 0037 approval, change-revokes, and blocked-state enforcement apply verbatim |
<<<<<<< HEAD
| Producer code in the frontend | Built-in families: none (components ship with the renderer). Bundled families (stage 1b, shipped): producer source rides the announcement as text, the renderer materializes it by evaluation with a guarded, tiny injected API (validated module shape; built-in families cannot be overridden; ≤1 MiB; failures memoized and degraded to the fallback view). The serializer strips bundled sources when persisting snapshots, so untrusted `.maieuticsnb` files never carry executable renderer code — bundled code only ever arrives as live output of a running session. CSP gating applies only in the experimental CSP-on posture; the default posture relies on the webview sandbox plus the injected-API boundary. Web lane: producer code stays in the plugin worker / iframe origin, never in the product's context |
=======
| Producer code in the frontend | Built-in families: none (components ship with the renderer). Bundled families: producer ESM in the renderer sandbox, content-addressed, CSP-gated — behind the feasibility spike. Web lane: producer code stays in the plugin worker / iframe origin, never in the product's context |
>>>>>>> origin/main
| Plugin isolation | Plugin frames relay through the host control bus with the plugin id attached; the kernel drops models of blocked/revoked plugins (same enforcement points as extension invokes and capability grants) |
| Binary integrity | esm/css/large state ride `$object` content-addressed references (invariant 26); the objects endpoint is immutable and bearer-authed like every frontend route |
| Unknown/unsafe frames | Family payload validation on both ends; malformed or unknown-family frames are ignored (invariant 18); unknown announcement mimes are ignored by old frontends (invariant 17) |
| Direction discipline | `comm_open` stays kernel-side-originated (REPL, kernel host, or plugin-via-host); frontend-originated opens remain a typed protocol error (ADR 0024 decision 4, extended in §6.2) |
| Auth | No new transport: comm plane auth unchanged; web lane rides ADR 0021's entrance-token paths; `GET /v1/plugins` is a normal bearer-authed frontend route |
| Secret hygiene | UI state is untrusted input like tool output (AGENTS.md); no provider secrets or bearer tokens in announcements; gateway URLs contain the entrance token and are therefore never logged |

## 9. Cross-surface story

The framework is surface-agnostic by construction — model + transport are
host-independent; only the bridge and the component host differ:

| Surface | Bridge | Component host | Stage |
|---|---|---|---|
| Notebook outputs | renderer messaging (today) | notebook renderer webview | 0–1 |
| Sidebar / panel | `registerWebviewViewProvider` + the same `UiBridge` over a webview message port; the extension's first real webview infra (asset pipeline via `build:media`, CSP nonce handling) | webview with the same registry bundle | 2+ (first consumer: plugin approval dashboard) |
| Browser SPA | direct comms WS (`?token=`) + `client.ts`-style fetch; the registry bundle runs unmodified | the SPA itself | later |

The sidebar surface is where built-in features that don't fit a cell
(approval dashboards, task monitors) land; it reuses everything and adds
only host plumbing.

## 10. Wire compatibility

All changes are additive to the v1 frontend protocol:

- New display mime (`application/vnd.maieutics.view+json`) — unknown to
  existing clients, ignored under invariant-17 tolerance; the compat mime
  is byte-stable.
- New comm target names — the relay is target-agnostic; old bridges treat
  unknown comms as inert state entries (today's `WidgetBridge` already
  caches and posts them; renderers without the family fall back to state
  JSON).
- `capabilities` gains a nullable `ui {version, families}` object, same
  pattern as `comm`; the discovery file and `protocolVersion` are
  unchanged.
- `repl.display` frames, event sequences, backpressure semantics: no
  change.
- Notebook snapshots: a native view announcement is an ordinary mime
  bundle member and persists like one; snapshots keep only the final
  rendered view (ADR 0024 posture). Reopened notebooks re-mount live only
  while the producing session is active; otherwise the frozen view shows
  (§5's dead-model degradation).

## 11. Roadmap

Each stage is independently shippable; stages 0–1 unlock everything after
them without further wire changes.

**Stage 0 — registry + Preact renderer (extension only, zero wire
change). — IMPLEMENTED 2026-10-04** `build:media` task (esbuild-free: the
built-in `deno bundle`, ESM output — see the ESM finding below); port
`widgetRenderer.js` classic controls to Preact components behind
`registerViewFamily`; keep the `maieutics-widget` envelopes and the
fallback state view; `turnRenderer_test.ts`-style pure-function tests for
the registry. Files: `deno/maieutics-vscode/{deno.json, media/, package.json}`.
Accept: existing widget cells render identically; `deno task build:media`
reproducible; renderer tests green.

**Load-shape finding (stage 0).** VS Code loads notebook renderer
entrypoints with a native dynamic `import()` — ESM only; there is no global
`exports` in the notebook webview (verified against VS Code source on main
and the 1.99 tag, and empirically in a real browser: a classic script doing
`exports.activate = …` throws `ReferenceError`). The previously committed
hand-written renderers had exactly that shape and therefore never loaded in
real VS Code; unit tests masked it by pre-seeding a global `exports`.
Stage 0 fixed both entries to real ESM exports (`export function activate`
/ `export { … }`), fixed the test to import the named exports directly, and
made `build:media` emit an ESM bundle (`deno bundle` default format — a
`--format cjs` output assigns to `module.exports` and is equally dead).
Bundle artifacts verified loading in a real browser.

**Stage 1a — native announcement + generalized runtime/bridge. —
IMPLEMENTED 2026-10-04 (form family; bundled-ESM delivery deferred to 1b)**
`application/vnd.maieutics.view+json` member; `WidgetRuntime` → UI model
runtime with family dispatch (jupyter.widget as compat adapter);
`WidgetBridge` → `UiBridge` envelopes; `maieutics.ui` beside
`maieutics.widgets` in `repl_worker.ts`; dead-model mount degradation.
Files: `deno/maieutics-plugin-sdk/ui/` (family/runtime/form modules),
`deno/maieutics-deno-repl/repl_worker.ts`,
`deno/maieutics-vscode/{uiBridge.ts (replaces widgets.ts), renderer/families/, media/}`,
renderer contribution for the native mime in `package.json`.
Accept: a TSX cell renders a `maieutics/form` model live; jupyter widgets
unchanged on the wire (fixture diff empty).

<<<<<<< HEAD
**Stage 1b — bundled-family spike. — CONCLUDED 2026-10-04: both CSP
postures green; bundled families ship on the inline-materialization path.**
The mechanism: a producer family may carry `esmSource` (bounded at 1 MiB;
optional `cssSource`) on its registration, and every announcement of that
family embeds it. The view renderer materializes the module by evaluation —
`new Function("registerViewFamily", "h", "Fragment", source)` — an injected,
deliberately tiny API surface (family registration + preact factories); no
URL imports (`blob:`/`data:` module sources are CSP-blocked anyway).
Failure semantics are typed and memoized per source: a broken or
non-registering module degrades every view of that family to the fallback
state JSON (invariants 17/18) without re-evaluating.

Verification (2026-10-04, real browser): under the default posture (no CSP
meta) and under a meta CSP mirroring VS Code's experimental
`notebook.experimental.enableCsp` policy (`script-src … 'unsafe-inline'
'unsafe-eval'`), the bundled module materialized, registered its family,
rendered the announcement's embedded state, and posted the mount envelope
(`bundled-ok | posted=true` in both). Unit tests cover registration,
memoization, thrown-source degradation, and preact vnode construction
inside a bundled module.

Delivery note: v1 rides the source inline in the announcement; the
serializer **strips `esmSource`/`cssSource` when persisting snapshots**, so
an untrusted `.maieuticsnb` never carries executable renderer code — a
reopened notebook renders the frozen announcement state through the
fallback view, and a live re-display re-materializes the family. The
`$object` content-addressed delivery from §2 is the production path for
larger payloads once a cell-side upload surface exists (today only
extension-side `uploadObject` can store objects). Review hardening
(2026-10-04): guarded registration validates the module shape and refuses
to override already-registered families; the renderer bounds the source at
1 MiB and degrades a throwing component to the fallback view (invariant
18).
=======
**Stage 1b — bundled-family spike.** The CSP facts are already verified
(Appendix A); the spike validates the materialization path, not
permission: producer ESM fetched by the bridge and materialized as an
inline module, exercised under both postures — today's default (notebook
CSP meta absent) and the experimental CSP-on. Module-by-URL
(`import()` of `blob:`/`data:`) is expected blocked and is not the path.
Accept: spike report with both postures green on the bridge-delivery
path; no product dependency beyond the report.
>>>>>>> origin/main

**Stage 2 — kernel producers + first built-in consumer. — CORE + ELICITATION
CONSUMER IMPLEMENTED 2026-10-04; plugin approval form deferred to stage 4's
`GET /v1/plugins` round (it needs an approval-state event surface in
Plugins, which rides better with the REST endpoint).** Comm ownership in
`FrontendCommRouter` (`PublishFromKernelAsync` + owner-routed uplink:
kernel-owned comms never travel to the child; plane recycling drops their
owners); C# form model host (`FrontendUiModels.cs`: native dialect bytes,
`repl.display` announcement via the session frame publisher, deterministic
close); `capabilities.ui {version, families}`; first consumer: MCP
elicitation presents a live `maieutics/form` alongside the `input.request`
fallback — schema-derived fields (bounded at 16, tolerant mapping), submit
answers accept with the values object, cancel answers decline, and either
path closes the form deterministically. Accept met: elicitation flow
completes inside the notebook over the comm plane; the input endpoint path
remains the fallback; suite + AOT publish green.

**Stage 3 — plugin UI lane. — KERNEL LANE IMPLEMENTED 2026-10-04 (code form): the
`ui.models` capability (catalog + deny-by-default grant + ADR 0037 approval via grant
clearing) receives worker-pushed frames and publishes them into the foreground
session's comm plane with a `PluginUiOwner` routing frontend uplink back to the
plugin's `UiEvent` export through the generic host.invoke path (zero new control-bus
message types: downlink rides `capability.invoke`, uplink rides `host.invoke`).
Remaining stage-3 work: the SDK worker-side surface (UiEvent extension-point
definition + model API) and the `ui` data-entry form.** `ui` service extension point (SDK +
`deno/maieutics-plugin-host` relay + `PluginHostManager` invoke path with
`plugin_pending_approval` enforcement) and/or `ui` data-entry interpreter;
`ui.models` capability in `PluginCapabilityCatalog`; fingerprint coverage
verified by fixture (approve → renders; touch declaration → revoked →
models drop). Files: `deno/maieutics-plugin-sdk/`,
`deno/maieutics-plugin-host/`, `Maieutics/Plugins/`,
`Maieutics/Control/ReplControlMessages.cs`. Accept: ADR 0037 lifecycle
tests extended to UI models; blocked plugin renders nothing.

**Stage 4 — web lane wiring.** `extension.registry` carries the gateway
section; `GET /v1/plugins` (ADR 0037's planned REST) with page entries;
`maieutics/iframe` family + sandboxed iframe embedding; SSR-first plugin
page guide for authors. Embedding targets a WebviewView/WebviewPanel
with `frame-src *` and a `portMapping` entry for the gateway port —
http maps (so the SSE push channel survives remote kernels); `ws://`
does not map, per `vscode.d.ts`. Files:
`deno/maieutics-plugin-host/{mod.ts, http.ts}`,
`Maieutics/Plugins/PluginHostManager.cs`,
`Maieutics/Frontend/FrontendHost.cs`, `deno/maieutics-vscode`. Accept: a
sample plugin's page opens from the product, embedded and standalone;
entrance token never appears in logs.

## 12. Alternatives considered

- **Port the Jupyter comm wire wholesale** — rejected in ADR 0024; the
  native plane is smaller, binary-native, and replay-aware. Unchanged.
- **React instead of Preact in the renderer** — Preact is already the
  SDK's compile-target idiom (automatic-runtime vnodes, signals-core);
  one component model across the wire beats ecosystem gravity here.
- **HTML-over-the-wire (SSR) as the framework lane's transport** — breaks
  replay (non-idempotent frames), theming (no `--vscode-*` substitution
  in opaque HTML), and snapshot portability; kept where it belongs, the
  web lane (§7).
- **Web lane only for plugins (no plugin framework lane)** — iframes
  everywhere lose notebook composition, theming, and snapshot rendering;
  also every interactive plugin would need the kernel-wiring stage anyway.
  The lanes are complementary (§6.3 vs §6.4), not rivals.
- **All-UI-in-webviews (skip notebook renderer investment)** — cells are
  where model output lives; sidebar surfaces compose *on top of* the same
  registry (§9), so the notebook-first path is the reusable one.
- **A generic JSON-schema UI dialect (no families)** — one dialect fits
  no one well; families keep each component set's state dialect honest
  and the catalog closed, matching the declarative-extensions axis
  discipline.

## Appendix A. VSCode host capability boundaries (verified 2026-10-04)

Verified against the official extension API docs and the VS Code
main-branch source on the date above. Re-verify if the extension's
engine baseline (`^1.99.0` in `deno/maieutics-vscode/package.json`)
moves.

| Surface | HTML | Scripts | Network from the view | Iframes | Theming |
|---|---|---|---|---|---|
| Native views (tree, QuickPick, status bar, decorations) | none | — | — | — | native |
| Notebook output (renderer iframe; all outputs share one iframe, single-file entrypoint) | full DOM from the renderer script | renderer bundle always; inline/eval also allowed under VS Code's notebook CSP (`notebook.experimental.enableCsp` — no default registered on VS Code main as of this date, so the CSP meta is absent unless opted in) | under CSP-on: `connect-src https:` only — no fetch/WS to the http loopback; `img-src … https: http: data:` lets images load from anywhere; all sockets ride the bridge regardless | under CSP-on: `child-src https: data:` only — `http://127.0.0.1` iframes blocked | `var(--vscode-*)` in the renderer document |
| WebviewPanel / WebviewView (sidebar) | extension-authored complete document | `enableScripts: true` required (default off); extension sets its own CSP; `enableForms` defaults on with scripts; `enableCommandUris` opt-in allowlist | extension-defined CSP governs; loopback http fine (Simple Browser ships `frame-src *`) | yes — Simple Browser embeds arbitrary user URLs (including `http://localhost`) via `<iframe sandbox="allow-scripts allow-forms allow-same-origin allow-downloads">` | `var(--vscode-*)` in the webview document; does **not** cross into nested iframes |

Evidence, with sources:

- Webview guide: scripts disabled by default and gated by an
  extension-set CSP; `asWebviewUri`/`localResourceRoots` (default:
  workspace + extension install dir; data URIs always allowed);
  theming via `--vscode-*` variables and `vscode-light/dark/high-contrast`
  body classes; `acquireVsCodeApi` callable once per session; workers
  restricted to `data:`/`blob:` URIs.
  <https://code.visualstudio.com/api/extension-guides/webview>
- Notebook renderer guide: "Output renderers are always rendered in a
  single iframe"; the entrypoint must be a single file;
  `createRendererMessaging` (messages not guaranteed delivered).
  <https://code.visualstudio.com/api/extension-guides/notebook>
- VS Code source, notebook output webview
  (`src/vs/workbench/contrib/notebook/browser/view/renderers/backLayerWebView.ts`):
  the CSP meta is gated on `this.configurationService.getValue('notebook.experimental.enableCsp')`
  and no default is registered for that setting; when on, the policy is
  `default-src 'none'; script-src <cspSource> 'unsafe-inline'
  'unsafe-eval'; style-src <cspSource> 'unsafe-inline'; img-src
  <cspSource> https: http: data:; font-src <cspSource> https:;
  connect-src https:; child-src https: data:`.
- VS Code source, Simple Browser
  (`extensions/simple-browser/src/simpleBrowserView.ts`):
  `enableScripts: true, enableForms: true`; document CSP
  `default-src 'none'; … script-src 'nonce-…'; frame-src *;`;
  body contains `<iframe sandbox="allow-scripts allow-forms
  allow-same-origin allow-downloads">` loaded with the user's URL.
- `vscode.d.ts` (`src/vscode-dts/vscode.d.ts`), `WebviewOptions.portMapping`:
  remaps localhost ports inside the webview onto extension-host ports;
  "port mappings only work for `http` or `https` urls. Websocket urls
  (e.g. `ws://localhost:3000`) cannot be mapped to another port."

Consequences for this design:

- **SSR of nontrivial complexity is a proven capability in-product at
  the WebviewPanel/WebviewView tier.** The framed page is a full browser
  environment (own scripts, own origin, forms, SSE) — VS Code bounds
  embedding, not page complexity. The web lane's SSR-first ruling
  (§7) stands on this tier.
- **The framework lane is CSP-robust by construction**: the renderer
  never opens sockets (the bridge owns every one, so CSP-on's
  `connect-src https:` costs nothing), v1 families need no iframes, and
  the bundled-family path materializes inline modules (allowed under
  CSP-on) from bridge-delivered text rather than module URLs (blocked:
  no `blob:`/`data:` in `script-src`). §4 and roadmap stage 1b encode
  this.
- **Notebook-output gateway embedding is not free**: under CSP-on,
  `child-src https: data:` excludes `http://127.0.0.1`, so the
  `maieutics/iframe` family lands on the webview surfaces (§9), with
  in-notebook gateway iframes as an https-gateway or bridge-proxy later
  option (§6.4).
- **`portMapping` shapes the remote-kernel story**: http loopback ports
  remap for remote development, `ws://` does not — which independently
  corroborates ADR 0021's choice of SSE (plain http) as the gateway
  push channel, and the framework lane's bridge-owned WebSockets (the
  extension host, not the webview, holds the socket).
