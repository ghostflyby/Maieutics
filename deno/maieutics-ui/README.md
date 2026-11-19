# Standalone conversation UI prototype

Stage 1 of the [conversation UI design](../../docs/standalone-conversation-ui-design.md): a reusable
Vue conversation component and a framework-neutral state core, exercised with a lazy deterministic
10,000-turn/100,000-entry source. This is a fixture-backed prototype. It does not connect to a live
kernel, call a model, compile authored Vue, or implement the proposed backend projection routes.

## Run the prototype

From this directory:

```sh
deno task demo
```

Open the loopback URL printed on readiness. The application initially loads the latest index page
and only visible output bodies. Scrolling into an unloaded range requests another bounded page.
Scrollbar thumb jumps and movement within an already mounted unloaded range use actual viewport
intersection to request the target page. Moving to a different target cancels obsolete page work;
loading does not require a wheel, touch, or keyboard event. While dragging, the viewport freezes its
coordinate system and defers height corrections; page/body loads and eviction cannot move the held
thumb. Releasing the thumb restores measured heights around the same logical entry and offset. Tools
remain summaries until expanded. “并排查看” opens a second view with its own composer and scroll
position. Sending input creates a fixture reply; the footer controls append a fragment or seal that
output. No Agent run is submitted.

## Host integration

History and current input are separate exported Vue components in separate files:
`ConversationHistory` owns virtualization, history reads and copy; `ConversationComposer` owns only
draft editing and submission. Both accept one caller-owned `ConversationViewState`. The shell may
place them in different DOM containers or mount either alone. Use one history and one composer per
view attachment; additional independent readers/editors attach their own view IDs.

The source components use the pinned Vue 3.5.13 runtime. The self-contained ESM offers separate
mount adapters so a host does not need to share Vue implementation objects:

```ts
import { ConversationRuntime, mountConversationComposer, mountConversationHistory } from "./mod.ts";
import type { ConversationSource } from "./mod.ts";

export async function openConversation(
  historyElement: HTMLElement,
  inputElement: HTMLElement,
  source: ConversationSource,
) {
  const runtime = new ConversationRuntime("session-id", source);
  await runtime.loadPage();
  const view = runtime.attachView("main");
  const history = mountConversationHistory(historyElement, {
    view,
    host: { writeText: (text) => navigator.clipboard.writeText(text) },
  });
  const input = mountConversationComposer(inputElement, {
    view,
    onSubmit(submission) {
      // The host submits this draft through its explicitly addressed backend adapter.
      // After acceptance: runtime.confirmSubmission(submission.viewId, submission.revision).
      // Pending acceptance never clears newer typing or another view's draft.
      console.log("Submission intent", submission.viewId);
    },
  });
  return async () => {
    input.unmount();
    history.unmount();
    view.dispose();
    await runtime.dispose();
  };
}
```

`deno task build` produces `dist/conversation.js`, a self-contained ESM with no external runtime
imports. The separate mount handles accept new plain bindings through `update()` and support
terminal, idempotent `unmount()`. Unmounting history cancels its requests, releases
visible/selection pins and stops its listeners; input and draft stay alive. Unmounting input
preserves the draft and history. Replacing the supplied view detaches the component from its old
state without disposing that caller-owned attachment. `view.dispose()` closes both mounted
components; runtime disposal also closes all its views. The optional history `onReady` callback
means viewport initialization, not completion of every lazy content read.

`ConversationView` is an optional composition component used by `mountConversation()`. It creates
one view attachment, places the two independent components together, forwards events and disposes
its own attachment on unmount/session replacement. The shell still owns the runtime. All mount
adapters use the bundle's own Vue runtime; do not pass its component definitions into a separately
bundled Vue application.

Include `dist/styles.css` or equivalent component styles and give the mount target a definite height
for history (or the combined view). Input has its own natural height; scrolling stays inside the
history viewport.

The `ConversationSource` port exposes paged metadata and exact-revision text. Its safe integer
ordinals are stable navigation coordinates inside one epoch, not identities or a finalized wire
schema. A backend adapter must map its cursor/range contract explicitly. `readPage` returns a
consistent current snapshot, or the requested retained revision for copy. Disappeared historical
content fails explicitly. `runtime.apply()` consumes atomic semantic changes in epoch/revision
order; raw frontend frames need an adapter. Each batch has at most one operation per entry.

The core exposes readonly projection access and narrow per-entry subscriptions. Index and body
caches have independent budgets; unloaded ranges aggregate geometry without creating one browser
record per historical entry. Source requests have bounded concurrency/queues, caller cancellation,
and joined runtime disposal. Source implementations must honor their supplied AbortSignal. The
runtime fences late replies even when a source ignores cancellation, and waits for its owned work to
finish before completing disposal.

Logical cross-block selection uses stable text points and exact revisions. Ordinary copy reads the
text projection independently of mounted DOM; missing snapshots and the 8 MiB selection limit
produce explicit failure rather than partial text. The prototype exposes passive text only, so its
text projection matches displayed strings. Markdown/source mapping, interactive form checkpoints,
iframe/native surfaces, and rich SSR are later design stages.

## Validation

```sh
deno task check
deno lint
deno fmt --check
deno task test
deno task build
deno task test:browser
```

The pure core suite controls page/delta races, stale revisions, bounded cache admission, source
cancellation, late replies, independent drafts, and copying across evicted rows. Browser conformance
uses an independent Chromium profile and loopback server with signal-driven DOM assertions. It
checks real variable-height virtualization, lazy output/tool reads, live updates, ordinary copy,
held-scroll position/extent through page replacement, session replacement, sibling disposal,
separate history/input roots, independent unmount/remount and shared-view closure through the
separately bundled ESM. Set `MAIEUTICS_UI_BROWSER` to select a Chromium executable; CI reuses the
pinned `MAIEUTICS_ACTOR_BROWSER` browser. Browser results establish behavior and capacity bounds,
not a device-independent timing benchmark.

## Permissions and packaging

Browser modules start no network, filesystem, process, or clipboard work merely by importing.
Mounting uses the injected source and explicit clipboard capability. Text is rendered as inert text;
the prototype never selects executable output or needs `unsafe-eval`/remote assets.

Build reads local sources and cached dependencies and writes `dist/`. The fixture host permits only
loopback networking and reads the known files under `dist/`; Ctrl+C closes its owned listener.
Browser conformance reads source/browser paths, writes a temporary bundle/profile, binds an
ephemeral loopback port, and starts owned Deno/browser children. Browser environment inheritance is
allowlisted, logs are bounded, and teardown joins the process, socket server, streams, and profile
cleanup under one total shutdown budget.

This UI workspace member is not a REPL/plugin child and is not embedded by `DenoRuntimeModule`. Its
assets are built separately; the existing executable resource globs and VS Code output-client
artifacts are unchanged. Dependencies add TanStack Vue Virtual 3.13.52 and Virtual Core 3.19.11,
sharing the existing Vue 3.5.13 dependency. Future backend wire/capability/persistence work must add
its own protocol fixtures and product integration coverage before live-kernel support is claimed.
