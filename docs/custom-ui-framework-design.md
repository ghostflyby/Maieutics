# Vue UI and form output

Status: implemented form migration, 2026-11-12. This replaces the former custom view-family
architecture; [ADR 0050](architecture/decisions/0050-retire-custom-component-families.md) supersedes
ADR 0038's component engine and authoring contract.

## One browser component runtime

`deno/maieutics-output-client` owns the browser's Vue runtime and both assistant output and built-in
form components. `createFormRenderer` is an ordinary ESM API with injected message delivery. It
starts no I/O and requires no VS Code global. The notebook's `media/formRenderer.js` only translates
renderer calls/messages. There is no dynamic family registry, dependency collector, manual paint
loop, bundled component-source execution or injected JSX factory API.

Forms receive plain state and emit submit/cancel events. Local Vue drafts preserve edits and focus
when unrelated server fields change. Accepted state updates modify a shared reactive model slot;
several mounted views share server state but own their local drafts. A view unmount releases its
Vue scope and attachment, without closing another view's model. Model closure or transport loss
makes retained content readonly. Terminal renderer disposal unmounts all owned apps.

The kernel and plugin host retain approval, session and comm ownership. The bridge accepts only the
`maieutics.form` target and actions from a mounted matching view with confirmed live state. The
form renderer is a presentation component, not a worker or a new permissions authority.

The VS Code host binds each editor's comm scope to its notebook's pinned session and targets state
delivery to that editor. An unbound notebook cannot attach through the foreground session. The last
view unmount, editor disappearance or session repin releases the editor's socket without closing the
producer or other editors' views. Visibility owns the connection independently of document lifetime
and without comparing TabGroup snapshots. Hidden editors retain only weak mount identities; showing
them resumes confirmed state without another browser mount or replaying actions. An early mount is
retained with diagnostic readonly feedback until visibility admission. Dormant views never migrate
to another session. Replacement mounts proceed while retired connections are being joined.
Retired scopes cannot publish into replacements. At most 128 active/retiring editor scopes are
admitted; an excess mount receives local readonly `form_scope_limit` feedback.

Each editor scope admits at most 1,024 live form states. At capacity it skips only a new model's
open, logs `form_state_limit`, and sends any mounted views of that model a local
`closed` notification with `code:"form_state_limit"`. Existing models can still refresh, receive
updates and send actions; their shared socket stays connected. The rejected producer is not closed
upstream, since other frontends may use it. Rejected models cannot send actions or gain state from
updates; after another model closes, a fresh open can admit them. This local admission failure is
distinct from actual transport loss, which makes all retained views readonly.

## Data contract

Discovery advertises `ui: {version: 2, formVersion: 1}`. Forms announce through
`application/vnd.maieutics.form+json`:

```json
{"version":1,"modelId":"opaque-comm-id","state":{"title":"Approve","fields":[],"values":{}}}
```

The generic comm endpoint and binary framing are unchanged. Open uses target `maieutics.form` and
`{state}`. Downlink messages use `{method:"update",state:{...}}`. Uplink actions use
`{method:"event",name:"submit",payload:{values:{...}}}` or `name:"cancel"`. Component source and
component registrations have no place in this contract. See the normative
[frontend protocol](web-frontend-protocol.md#comm-channels-and-declarative-forms).

## Producer migration

SDK 0.3 removes widget exports, the widget JSX dialect, general UI family/model registration and
bundled component sources. The REPL uses the upstream ordinary TypeScript transform and exposes
only `maieutics.ui.form` for declarative forms:

```ts
const approval = await maieutics.ui.form({
  title: "Approve?",
  fields: [{ name: "note", type: "text", required: true }],
}, { onSubmit: (values) => console.log(values.note) });
approval; // the REPL display hook emits form data
```

The handle exposes a readonly Vue ref for state, awaited `update(patch)` and idempotent `close()`.
Updates are serialized, bounded to 64 queued operations, and use detached plain-data patches.
Creation awaits comm publication; errors propagate to the caller. Controller state is bounded to
128 models and close releases callbacks. Plugin workers continue to use the approved `ui.models`
capability through `@maieutics/plugin-sdk/plugin-ui`; it now accepts only the form target. Kernel
elicitation and declarative plugin forms produce the identical data contract.

## Saved outputs and removed features

Saving and reading form output whitelists the data fields and adds `snapshot:true`. Such output
never mounts a live comm, sends actions or accepts a later live state message. Legacy form
announcements are converted to this frozen shape. Other old family output becomes ordinary JSON;
old widget announcements retain a textual fallback. Embedded legacy source/styles are discarded.
Opening a file starts neither a model nor component execution.

The classic widget factories/runtime, widget renderer, TSX-to-widget transform, family registration
API and custom bundled-source dialect are removed. Old SDK imports must migrate to 0.3; they are
not silently reinterpreted as Vue components. Arbitrary reusable Vue plugin components and a general
business-model projection/action protocol remain future work, documented in the
[Vue migration roadmap](vue-reactive-ui-migration.md). This form migration does not claim to provide
that catalog, action-result RPC, model persistence, resource loading or SSR for built-in forms.
