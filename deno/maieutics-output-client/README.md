# Browser output client

Standalone browser ES Module for Maieutics output. It shares the exact implementation used by the VS
Code notebook adapter: timeline DOM, passive Markdown fallback, SSR snapshots, scoped CSS,
progressive Vue templates, ClientOnly, code controls, diagnostics, declarative Vue forms and
component disposal.

```sh
deno task build
```

Serve `dist/outputClient.js` and `dist/outputClient.d.ts` as static assets. The JS is one
self-contained ESM file, including the pinned Vue runtime and Markdown-it. It requires no package
manager, import map, CDN, VS Code extension or global bridge at runtime. Importing it starts no I/O
and mounts no DOM. The TypeScript source entry is `mod.ts`; public declarations are maintained in
`types.d.ts` and used by the implementation and build. This is a repository module; registry
publication is not configured.

```js
import { createOutputRenderer } from "/assets/outputClient.js";

const client = createOutputRenderer({
  sendMessage(message) {
    // Route to your authenticated presentation host; observe asynchronous delivery errors there.
    transport.send(message);
  },
});
const subscription = transport.subscribe((message) => client.receive(message));

await client.render({
  id: "answer-1",
  kind: "turn",
  value: { text: "# Answer\n\nOrdinary Markdown", streaming: true },
}, document.getElementById("answer"));

// Reuse the same id and element when text advances. Receive backend replies separately.
// When the containing view closes:
subscription.dispose();
client.dispose();
```

`transport` is supplied by the application, not an export of this package. `sendMessage` and
`receive` use the existing `maieutics-output` presentation envelopes: render/cancel/copy/applied
outbound and rendered/unavailable/copy-result inbound. A transport adapter owns authentication,
backend route selection, request coalescing, clipboard and subscription lifetime. The VS Code host
uses `OutputRenderingBridge` for these services. This module contains no bearer token and makes no
network or clipboard calls. Server compilation and SSR stay in the kernel's Deno renderer.

`render` resolves after native fallback preparation and request dispatch, not after a backend
commit. The `applied` message identifies a committed request and engine. Await `receive` when
processing a reply because a progressive Vue update can be asynchronous; `vueUpdatesComplete` awaits
queued local Vue DOM changes. Unknown and stale replies are ignored. `disposeOutput(id)` releases
one output; omitting id releases all current outputs while keeping the scope usable. `dispose()`
aborts pending preparation and unmounts all apps, is idempotent and terminal, and prevents late
preparation/replies from committing. Hosts must observe returned promises and release transport
subscriptions themselves. An injected asynchronous `renderMarkdown` must honor its context signal.

Output kinds are `turn` (passive timeline snapshot), `assistant` (host-authorized live assistant),
`vue` (explicit live `{version:1,format,source}`), and `template` (data-only template snapshot).
Selecting assistant/vue authorizes execution of backend-compiled code: do it only for live output or
kernel-verified canonical history. Never select it because an arbitrary saved file supplies a marker
or factory. The module is executable UI, not an untrusted-code sandbox; the host's CSP must permit
its chosen Vue factory execution policy. Source, factory, HTML/CSS bounds and progressive revision
checks are unchanged from the [frontend protocol](../../docs/web-frontend-protocol.md).

The default Markdown renderer disables HTML and executable links, including command URLs. Hosts can
inject `renderMarkdown(text, element, context)` for native rendering, and `onAction(href)` for
explicit timeline action links. CSS inherits optional VS Code theme variables with browser defaults;
hosts can set those variables or `.dark` without installing any editor integration. `renderTurn` and
`renderSubagents` are pure timeline markup helpers; their default Markdown fallback is escaped
source. They do not grant trust to arbitrary HTML callbacks.

For hosts that already own SSR DOM, `mountVueIsland(element, compiledCode, progressive?)` exposes
the lower-level hydration/update/disposal primitive using the same bundled Vue runtime. Only pass
backend-verified factories; it does not authenticate or sanitize executable code. Prefer the full
renderer for revision checks, pending preparation, diagnostics and source/response correlation.

## Integration and verification

`deno task build:vscode` rebuilds and copies the JS into the extension's `media/outputClient.js`.
Its hand-written `media/turnRenderer.js` only maps notebook MIME/items to this API, injects native
Markdown and forwards messages/retry commands. No extension-host source imports this workspace
module. `build:media` invokes the same copy task. CI verifies that committed browser assets match
their sources. Tests cover ESM exports, no external module imports, headless import/disposal, and
direct browser use without VS Code renderer services. The real browser/kernel suite also exercises
SSR, progressive ClientOnly state, abort during preparation and adapter/history compatibility.

This browser module needs no Deno permissions at runtime. Its build reads source/dependencies and
writes local dist/; build:vscode additionally writes the one copied media artifact. No new runtime
filesystem, environment, process or network permission is introduced. Source-level Deno tests allow
NODE_ENV for Vue's Node entry; the bundled browser artifact has no environment permission
dependency.

## Declarative Vue forms

`createFormRenderer({sendMessage})` is the shared normal-Vue form implementation used by the
notebook's `formRenderer.js`. Call `render(outputId, {version:1,modelId,state}, element)`, pass host
state/closed messages to `receive` and release views with `disposeOutput(id)` or terminal
`dispose()`. The host owns transport subscriptions. `receive` awaits Vue DOM updates; use the
existing `vueUpdatesComplete` barrier for local user edits.

The renderer has no family/component registry, injected source or JSX runtime. Local drafts remain
stable across unrelated server updates, and several views share backend state without sharing local
edits. Fields/actions stay disabled until the host confirms live state. A snapshot carrying
`snapshot:true` never sends a mount, accepts live messages or emits actions. Closing a view sends
only an attachment-unmount notification; it does not destroy the shared model. Model/transport
closure makes retained content readonly. Up to 128 views and 128 fields/options per form are
accepted. The VS Code host translates the form contract to the existing authenticated session comm
endpoint; generic action-result RPC is not supplied by this API.
