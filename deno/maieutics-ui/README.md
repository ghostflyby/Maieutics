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
output. No Agent run is submitted. Persisted `pagehide`/`pageshow` transitions preserve the demo and
its runtime for back/forward-cache restoration. A final non-persisted `pagehide` unmounts the
application and disposes owned work.

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

History emits `anchorRestored` (mount binding `onAnchorRestored`) after a queued correction from
either a projection update or gesture release. `reason` distinguishes those paths. Every event has
the logical ordinal and pixel offset; `entryId` is present for a loaded entry and absent for an
unloaded range. No event is emitted when a correction is cancelled, superseded, or unnecessary at
the live end. Subsequent body measurements remain separate. Hosts/tests can observe that
acknowledgement without guessing the ordering of Vue update queues.

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
order; raw frontend frames need an adapter. Each batch has at most one operation per entry. An
additional page ahead of the reducer waits for the independently delivered stream watermark; it
never advances the global revision or total. Waiting remains request-owned and cancellable,
including when stream updates change only an entry body. A stale snapshot or reconciliation overflow
gets one fresh-snapshot retry; persistent inconsistency fails explicitly after that bounded retry.
Concurrent first pages atomically choose the first successful snapshot as their baseline. A second
page older than that baseline fetches fresh state; a newer one waits for the stream. Hosts need not
serialize initial `loadPage` calls. Waits expose request-local `PageLoadProgress` and
`statistics.waitingPages`, and fail with `stream_wait_timeout` after the configurable
`pageWaitMilliseconds` budget (15 seconds by default). The UI shows synchronization state and can
cancel/retry an active range immediately, as well as retry after timeout. Page admission fails
atomically with a capacity result if eviction would retain none of the requested records, preventing
automatic zero-progress range loops. Page publication is the success boundary: cancellation before
that synchronous commit prevents all projection publication, even if the source ignores
cancellation. Cancellation from a publication listener happens after commit and the request remains
successful. Notification follows the commit without an asynchronous gap; a final task cancellation
check cannot reverse that completed result.

The core exposes readonly projection access and narrow per-entry subscriptions. Index and body
caches have independent budgets; unloaded ranges aggregate geometry without creating one browser
record per historical entry. Source requests have bounded concurrency/queues, caller cancellation,
and joined runtime disposal. Source implementations must honor their supplied AbortSignal. The
runtime fences late replies even when a source ignores cancellation, and waits for its owned work to
finish before completing disposal.

The shared text cache retains its hard byte limit even with multiple pinned views. Cache admission
is optional for a successful exact text read: when active pins leave no room, the mounted caller
receives the full bounded source text without adding it to the cache. Visible blocks therefore stay
readable at the 200,000-character limit, including CJK text; callers own those responses within
their bounded mounted windows. Copy also receives exact text independently of cache retention and
keeps its own selection byte limit.

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
deno task check:evidence
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

Review regressions separately control HTTP/stream ordering, zero-progress capacity, a non-frozen
history anchor across measured predecessor eviction, actual live-end viewport geometry, cross-bundle
error codes, and persisted demo page transitions. Removing either structural anchor correction or
live-follow scrolling makes its dedicated geometry assertion fail. Clipboard waits have individual
deadlines; lazy detail assertions count the addressed entry's reads rather than global traffic. The
anchor case awaits its explicit correction acknowledgement before checking geometry. Controlled
held-page fixtures observe cancellation, including an injected early-failure cleanup scenario.
Recorded mutation commands, measured input hashes, outcomes and normalized failure assertions live
in [`test/evidence/scroll-mutations.json`](test/evidence/scroll-mutations.json).

The version 3 record explicitly describes a content-addressed **worktree snapshot**, not a Git
revision. Reproduce against files matching `source.files_sha256`; no parent commit is claimed to
contain those bytes. Absolute paths, ephemeral ports, stack traces and local toolchain/platform
metadata are excluded. Assertions identify the violated mechanism rather than a machine-specific
pixel delta.

`deno task check:evidence` recomputes the input inventory/hashes, then executes all declared
mutations and the unmodified browser suite. The negative runs must fail at their intended
assertions; a surviving mutant or an unrelated startup failure rejects the gate. The restored
scenario names and count must equal the actual executed output. Matching JSON/hashes alone cannot
satisfy this check. CI runs this complete gate without a redundant standalone browser run. The
declared mutations also zero the gesture offsets and projection ordinal/offset, proving that the
acknowledgement assertions check the coordinates of loaded and unloaded anchors.

All regular files under this module are hashed, without an extension whitelist, including hidden
fixtures, documentation and binary inputs. The only exclusions are root `.git/`, `dist/`,
`node_modules/`, generated `test/evidence/` records and `.DS_Store` files. Symlink inputs fail
closed. The workspace config/lock are also inputs. Changed, added or removed inputs reject stale
records. `deno task record:evidence` executes the same browser runs and writes a new record only
after they pass and the input hashes still match. Temporary import-map overlays apply mutations
without editing workspace files. Updating hashes alone is no longer a sufficient refresh.

## Permissions and packaging

Browser modules start no network, filesystem, process, or clipboard work merely by importing.
Mounting uses the injected source and explicit clipboard capability. Text is rendered as inert text;
the prototype never selects executable output or needs `unsafe-eval`/remote assets.

Build reads local sources and cached dependencies and writes `dist/`. The fixture host permits only
loopback networking and reads the known files under `dist/`; Ctrl+C closes its owned listener.
Evidence checking starts the same owned loopback browser runner for each case and writes temporary
bundles/profile/import maps. Recording additionally writes the generated evidence JSON. Inventory
unit tests own isolated temporary fixtures. Browser conformance reads source/browser paths, writes a
temporary bundle/profile, binds an ephemeral loopback port, and starts owned Deno/browser children.
Browser environment inheritance is allowlisted, logs are bounded, and teardown joins the process,
socket server, streams, and profile cleanup under one total shutdown budget.

This UI workspace member is not a REPL/plugin child and is not embedded by `DenoRuntimeModule`. Its
assets are built separately; the existing executable resource globs and VS Code output-client
artifacts are unchanged. Dependencies add TanStack Vue Virtual 3.13.52 and Virtual Core 3.19.11,
sharing the existing Vue 3.5.13 dependency. Future backend wire/capability/persistence work must add
its own protocol fixtures and product integration coverage before live-kernel support is claimed.
