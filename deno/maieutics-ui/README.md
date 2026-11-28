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

## DOM and external themes

The `localize(message, ...args)` binding is available on both independent mounts and on the combined
mount. The component reads English message sources from `locales/en.json` and formats positional
placeholders by default. Hosts can supply translations, including the range labels, status messages
and accessibility text. `locales/zh-CN.json` contains the matching Chinese catalog.

History exposes a simple `.mui-history-heading` without explanatory demo copy. Hosts can visually
hide this heading while keeping it available for assistive navigation. The independent composer
supports Ctrl/Cmd+Enter to submit; Enter inserts a newline, and IME composition or candidate
confirmation never submits. The host still acknowledges a captured submission before its draft is
cleared.

Load the component stylesheet once for the document. It contains the required virtual-list layout
and the default theme, with no page/body/button reset for unrelated host content:

```html
<link rel="stylesheet" href="/conversation/styles.css">
```

Put history and input mount containers under a common themed ancestor, even when they are separate
Vue applications. One host stylesheet can then theme every descendant view and editor:

```css
.workspace[data-theme="night"] {
  --mui-color-scheme: dark;
  --mui-text: #e3e9e3;
  --mui-surface: #202923;
  --mui-border: #3c4a40;
  --mui-muted: #adb9af;
  --mui-accent: #a8c6a2;
  --mui-accent-text: #1b2a1e;
  --mui-hover: #35433a;
  --mui-focus: #b9d6a8;
  --mui-input-surface: #2b382d;
  --mui-activity-surface: #28332c;
  --mui-activity-border: #435147;
  --mui-code-surface: #18201b;
  --mui-error: #efb69d;
  --mui-editor-surface: #18201b;
  --mui-editor-border: #53664e;
  --mui-shadow: #0005;
  --mui-font-family: system-ui, sans-serif;
  --mui-body-font-size: 14px;
  --mui-block-inset: 20px;
  --mui-control-radius: 4px;
}

.workspace .mui-submit {
  border-radius: 0;
}
```

Changing the ancestor's theme attribute updates existing views without remounting or losing drafts,
focus or editor selections. Typography/spacing changes are measured by the virtualizer. The demo
supplies a complete dark palette and one theme toggle shared by its side-by-side views.

The stylesheet is in `@layer maieutics`; ordinary unlayered host CSS overrides it without
`!important`, regardless of stylesheet load order. No component inserts a style tag or hardcodes
colors/fonts in its render function. The primary theme hooks are:

| Group         | Custom properties                                                                                                                 |
| ------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| Text          | `--mui-text`, `--mui-muted`, `--mui-error`                                                                                        |
| Surfaces      | `--mui-surface`, `--mui-input-surface`, `--mui-activity-surface`, `--mui-code-surface`, `--mui-editor-surface`                    |
| Controls      | `--mui-accent`, `--mui-accent-text`, `--mui-border`, `--mui-activity-border`, `--mui-editor-border`, `--mui-hover`, `--mui-focus` |
| Typography    | `--mui-font-family`, `--mui-font-family-mono`, `--mui-font-size`, `--mui-body-font-size`, `--mui-body-line-height`                |
| Shape/spacing | `--mui-radius`, `--mui-control-radius`, `--mui-block-inset`, `--mui-shadow`, `--mui-color-scheme`                                 |

Stable `.mui-*` classes also support direct host styling. History uses a named, keyboard-focusable
scroll region and an ordered list. Each loaded `li.mui-slot` contains one measured
`article.mui-row.mui-block`, with a `header`/`h3`, body and logical position/count metadata. Gap
items describe their unloaded ordinal range and contain an explicit load/retry button. Historical
replay is not a live announcement; status messages use their own polite region. The input is a named
form with a directly contained `.mui-editor`, `.mui-composer-hint` and `.mui-submit`.

The old `.mui-row > .mui-block` and `.mui-composer-actions` wrappers are removed; selectors should
target the article itself and the direct editor/hint/submit hooks. The optional composition is
`ConversationView → ConversationHistory → OutputBlockHost`, with `ConversationComposer` as a
sibling. Binding-owned effect scopes replace the internal Session component layers. Rebinding cleans
up the previous scope before creating the next, after a complete runtime/viewId prop update.

Only measured geometry remains inline as `--mui-virtual-extent`, `--mui-virtual-size` and
`--mui-virtual-offset`. These are runtime layout data, distinct from theme properties. A replacement
stylesheet must still use them for spacer height, item height and item position. Component CSS is
shipped as `dist/styles.css`; the optional demo page additionally loads `dist/demo.css`, which is
not required by an embedding host.

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
An overflowed pending reconciliation journal discards its retained changes and stops serializing
subsequent frames for that request; ordered stream reduction continues normally. Concurrent first
pages atomically choose the first successful snapshot as their baseline. A second page older than
that baseline fetches fresh state; a newer one waits for the stream. Hosts need not serialize
initial `loadPage` calls. Waits expose request-local `PageLoadProgress` and
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
finish before completing disposal. Internal projection clearing restores the complete empty
baseline, including epoch, revision and total as well as slots/rows. Runtime disposal uses that
reset after joining its requests.

The shared text cache retains its hard byte limit even with multiple pinned views. Cache admission
is optional for a successful exact text read: when active pins leave no room, the mounted caller
receives the full bounded source text without adding it to the cache. Visible blocks therefore stay
readable at the 200,000-character limit, including CJK text; callers own those responses within
their bounded mounted windows. Copy also receives exact text independently of cache retention and
keeps its own selection byte limit.

Logical cross-block selection uses stable text points and exact revisions. Ordinary copy reads the
text projection independently of mounted DOM; missing snapshots and the 8 MiB selection limit
produce explicit failure rather than partial text. Copy page jobs contain only `readPage`; body
resolution happens outside those jobs and schedules independent `readText` work. A copy never holds
a parent pool slot while awaiting a child request. Coverage includes uncached copies at concurrency
1 and 4, subsequent page/body requests, and four overlapping browser copy events through a single
slot with cache retention disabled. The passive text projection matches displayed strings. Hosts may
supply `ConversationHost.mountBlock(element)` to attach their existing rendering engine. Each
surface receives the entry and exact `TextContent`, including optional versioned presentation data;
it survives streamed revisions and is disposed when its virtual row leaves the viewport. Preparation
is cancelled on revision changes, and host replacement retires the old surface. Presentation data
counts against the same cache byte budget as its fallback text. The VS Code host uses this port to
reuse the existing Notebook output client for Markdown, SSR, Vue, structured tools and live forms;
the core contains no rendering engine or transport credentials. Rich renderer DOM uses native
selection, while logical source copy remains restricted to plain-text rows. Markdown/source mapping,
interactive form checkpoints and iframe/native surfaces are later design stages.

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
not a device-independent timing benchmark. Bundle entrypoints are passed to Deno as file URLs,
preserving Windows drive letters and escaped path characters. Browser discovery includes Windows
candidates; the browser CI job runs on Ubuntu.

Review regressions separately control HTTP/stream ordering, zero-progress capacity, a non-frozen
history anchor across measured predecessor eviction, actual live-end viewport geometry, cross-bundle
error codes, and persisted demo page transitions. Removing either structural anchor correction or
live-follow scrolling makes its dedicated geometry assertion fail. Clipboard waits have individual
deadlines; lazy detail assertions count the addressed entry's reads rather than global traffic. The
anchor case awaits its explicit correction acknowledgement before checking geometry. Its scoped
fixture uses distinct fixed row heights, all multiples of the virtualizer's estimate, so the chosen
37px nonzero remainder does not depend on font metrics. Other cases exercise natural variable-height
output. Verifier tests cover command and mutation-definition drift, symbolic inputs and literal
dollar sequences in replacement text. Controlled held-page fixtures observe cancellation, and finish
on scenario-owned deadlines. Recorded mutation commands, measured input hashes, outcomes and
normalized failure assertions live in
[`test/evidence/scroll-mutations.json`](test/evidence/scroll-mutations.json).

The version 3 record explicitly describes a content-addressed **worktree snapshot**, not a Git
revision. Reproduce against files matching `source.files_sha256`; no parent commit is claimed to
contain those bytes. Absolute paths, ephemeral ports, stack traces and local toolchain/platform
metadata are excluded. Assertions identify the violated mechanism rather than a machine-specific
pixel delta.

`deno task check:evidence` recomputes the input inventory/hashes, then executes all declared
mutations and the unmodified browser suite. Each mutation runs its addressed browser fixture. The
negative runs must fail at their intended assertions; a surviving mutant or an unrelated startup
failure rejects the gate. The restored scenario names and count must equal the actual executed
output. Matching JSON/hashes alone cannot satisfy this check. CI runs this complete gate without a
redundant standalone browser run. The declared mutations also zero the gesture offsets and
projection ordinal/offset, proving that the acknowledgement assertions check the coordinates of
loaded and unloaded anchors.

All regular files under this module are hashed, without an extension whitelist, including hidden
fixtures, documentation and binary inputs. The only exclusions are root `.git/`, `dist/`,
`node_modules/`, generated `test/evidence/` records and `.DS_Store` files. Symlink inputs fail
closed. The workspace config, lockfile and shared browser runner are also inputs. Changed, added or
removed inputs reject stale records. `deno task record:evidence` executes the same browser runs and
writes a new record only after they pass and the input hashes still match. Temporary import-map
overlays apply mutations without editing workspace files. Updating hashes alone is no longer a
sufficient refresh.

## Permissions and packaging

Browser modules start no network, filesystem, process, or clipboard work merely by importing.
Mounting uses the injected source and explicit clipboard capability. Text is rendered as inert text;
the prototype never selects executable output or needs `unsafe-eval`/remote assets.

Build reads local sources and cached dependencies and writes `dist/`. The fixture host permits only
loopback networking and reads the known files under `dist/`; Ctrl+C closes its owned listener. The
passive asset step reads `styles.css` and `demo/`, copying the component sheet and separate demo
sheet to `dist/styles.css` and `dist/demo.css`. Theme installation belongs to the host's normal CSS
loading; component imports/mounts perform no stylesheet I/O or automatic style injection. Evidence
checking starts the same owned loopback browser runner for each case and writes temporary
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
