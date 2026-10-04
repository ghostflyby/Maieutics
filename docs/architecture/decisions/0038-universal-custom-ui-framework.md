# ADR 0038: Universal Custom UI Framework

Status: Draft

Date: 2026-10-04

Amends: ADR 0024 (comm plane gains the custom-UI model layer on top; the
announcement mime this ADR defines supersedes the never-implemented
`application/vnd.maieutics.widget-view+json` shape of ADR 0024 decision 7)

Related: ADR 0021 (plugin HTTP UI gateway), ADR 0037 (plugin declaration
approval), ADR 0023 (frontend protocol), ADR 0020 (invariant 25 — plugin
comm surface), `docs/custom-ui-framework-design.md` (full design)

## Context

Custom UI today exists in exactly one complete pipeline: REPL TSX cells
author ipywidgets-compatible models (`deno/maieutics-plugin-sdk/widgets/`),
the comm plane carries their state (ADR 0024), and a hard-coded DOM script
renders them in the notebook (`media/widgetRenderer.js`). It works, but
everything around it is ad hoc: built-in features hand-roll presentation
(QuickPick chains for elicitation and approval), plugin workers have no
presentation surface at all (ADR 0020 invariant 25), and the ADR 0021 HTTP
gateway — implemented host-side — is invisible to the kernel and every
frontend. Each new UI feature must choose between these dead ends.

The widget pipeline is the right precedent: a transport-neutral model
registry, state-over-wire with last-wins merge, a display-mime
announcement paired to a comm channel by id, a caching host bridge, and a
fallback-when-unknown renderer. What is missing is generalization: view
families beyond classic widgets, producers other than the REPL, and a
component registry in place of a hard-coded renderer.

## Decision

1. **One framework, five generalized layers.** The widget pipeline's shape
   becomes the product's custom-UI framework: view families (kernel-defined
   closed catalog naming a component set and its state dialect —
   `jupyter.widget` compat plus native families starting with
   `maieutics/form`), a transport-neutral UI model runtime (the
   `WidgetRuntime` contract: state + `sync`/`onChange`, generalized to
   family dispatch), the display-mime announcement, the existing comm plane
   unchanged, and a Preact component registry in the renderer behind a
   generalized `UiBridge`. Full design: `docs/custom-ui-framework-design.md`.

2. **Announcement is a native display mime beside the compat one.**
   `application/vnd.maieutics.view+json`
   `{modelId, viewFamily, version, esm?, css?}` — the esm/css members
   ADR 0024 designed, realized as content-addressed `$object` references
   (invariant 26). The Jupyter compat mime stays byte-stable forever;
   unknown mimes are ignored (invariant 17).

3. **Transport unchanged; targets namespaced.** The ADR 0024 plane serves
   as-is. `comm_open.target_name` gains `maieutics.view/<family>` beside
   `jupyter.widget`; the relay stays target-agnostic. Capabilities gain an
   additive `ui {version, families}` object, same pattern as `comm`.

4. **Renderer: Preact component registry; the no-bundler constraint for
   `media/` renderer scripts is amended deliberately.** Components render
   per view family with a state-JSON fallback for unknown families
   (today's behavior, kept). A `build:media` deno task (esbuild,
   single-file committed output) makes Preact possible; shipped artifacts
   remain self-contained scripts. Bundled-family ESM is defined on the
   wire and ships behind a verified materialization path: VS Code's
   notebook webview CSP (`notebook.experimental.enableCsp`) allows
   inline/eval scripts but no `blob:`/`data:` module sources and no http
   fetch from the renderer, so producer modules arrive as source text
   through the bridge and run inline; the renderer never opens sockets
   itself (verified evidence: design doc Appendix A).

5. **Rendering model ruled per lane, not globally.** The framework lane is
   client-side state-over-wire (replay idempotence, `--vscode-*` theming,
   per-frame backpressure, snapshot-friendliness). The ADR 0021 web lane is
   SSR-first (zero-permission buildless plugin workers render HTML;
   pages open in any browser); client-side plugin apps remain possible —
   the lane is transport-agnostic, SSR is the recommended default, and
   hydration from a live comm model is a later stage. Rule of thumb:
   form-like theme-bound notebook-embedded UI → framework lane; app-like
   standalone browser-first UI → web lane.

6. **Producers extend by ownership, staged.** (a) REPL/TSX keeps working
   and gains `maieutics.ui` with custom-family authoring. (b) Kernel C#
   producers: `FrontendCommRouter` learns comm **ownership** — the REPL is
   the first owner, not the only one; kernel-side models publish into the
   same stream and receive owner-routed uplink; `comm_open` remains
   kernel-side-originated. First consumers: the ADR 0037 plugin approval
   UI and in-notebook elicitation. (c) Plugin interactive UI: a `ui`
   service extension point (code form) and a catalogued `ui` data-entry
   name (static form), frames relayed worker → host → kernel → comm plane
   with the plugin id as owner. (d) Web lane wiring: the host reports the
   gateway (base URL, entrance token, per-plugin roots) in
   `extension.registry`; `GET /v1/plugins` (ADR 0037's planned REST)
   exposes approval state and page entries; a `maieutics/iframe` family
   embeds pages as sandboxed iframes per ADR 0021's posture — embedded at
   the extension's webview surfaces, where the extension-authored CSP
   admits loopback http (`frame-src *`; the shipped Simple Browser
   extension is the in-product precedent); notebook-output iframes stay
   https-only under VS Code's notebook CSP.

7. **Plugin UI is consent-gated by the existing machinery.** The `ui`
   extension-point kind and data-entry name are new entries in collections
   the ADR 0037 fingerprint already hashes generically (`extensions` §6,
   `data` §7 of `PluginDeclarationFingerprint.Compute`): both declaration
   forms are fingerprint-covered with zero fingerprint-code changes. A
   `ui.models` entry joins the closed capability catalog (`caps` §5 of the
   fingerprint, deny-by-default). ADR 0037 enforcement applies verbatim:
   blocked plugins publish no UI models, revocation drops live models,
   re-approval restores the path.

## Consequences

- All wire changes are additive to frontend protocol v1: one new mime,
  new comm target names, one capabilities object; `protocolVersion` and
  the discovery file are unchanged. Existing widget cells, committed
  notebooks, and the current renderer behavior are preserved by the
  compat family and the fallback state view.
- VS Code host boundaries are verified, not assumed (design doc
  Appendix A): gateway pages embed in-product at the
  WebviewPanel/WebviewView tier with full page complexity (Simple
  Browser precedent); the notebook renderer lane is CSP-robust by
  construction (the bridge owns every socket; bundled modules run
  inline from bridge-delivered text); `portMapping` remaps http
  loopback ports for remote kernels but not `ws://` — which
  independently corroborates ADR 0021's SSE-for-push choice and the
  framework lane's bridge-owned WebSockets.
- The extension gains its first webview-adjacent build infrastructure
  (`build:media`); notebook snapshots keep only the final rendered view
  (ADR 0024 posture), and reopening shows the frozen view unless the
  producing session is live — the bridge degrades a dead-model mount to
  the snapshot state instead of today's endless "Connecting…".
- Plugin authors get two UI idioms with one consent story; the
  SSR-vs-client question is answered per lane in the docs rather than by
  accident per feature.
- Later stages (not v1): bundled-family ESM behind the stage-1b spike,
  hydration of web-lane pages from comm state, sidebar webview surface
  reusing the same bridge and registry, browser SPA consuming the same
  model/transport directly.
- Implementation order and per-stage file touchpoints are in the design
  doc's roadmap (stages 0–4, each independently shippable).
