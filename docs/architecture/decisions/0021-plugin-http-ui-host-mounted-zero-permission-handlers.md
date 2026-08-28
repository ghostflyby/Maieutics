# ADR 0021: Plugin HTTP UI — Host-Mounted Zero-Permission Fetch Handlers

Status: Draft

Date: 2026-08-28

## Context

Plugins need to serve browser-facing UI — settings pages, dashboards, rendered
tools — without gaining network capabilities. The capability model from the
design exploration stands: a plugin must never hold a socket; the only real
HTTP listener is a host-controlled endpoint, and a plugin receives exactly the
requests routed to its namespace. Path prefixes are a routing namespace, not a
browser security principal; browser-side isolation is a separate layer
(sandboxed iframes plus a host-controlled CSP), because Same-Origin Policy
keys on scheme+host+port, not path.

The codebase already has every ingredient:

- ADR 0018 decision 8: the plugin host is a trusted orchestrator launched with
  full Deno permissions (`--allow-net` included); each plugin worker is
  narrowed via its own `deno.permissions` worker options. The host is the
  permission ceiling; plugin workers are zero-permission.
- ADR 0020: plugins and the REPL share one orchestration tree over
  `@ghostflyby/worker-actor`; cross-worker actor references travel through
  reference-acquire, after which the workers talk directly and the host exits
  the data path.
- worker-actor 0.5.0 added transferable RPC values (codec-driven `move`
  policies and `ctx.transfer`); 0.6.0 ships a verified example moving fetch
  `Request`/`Response` across workers by natively transferring the body
  stream. Transferable streams are supported since Deno 2.6.
- The REPL already uses a blocking SharedArrayBuffer + `Atomics.wait` mailbox
  for synchronous worker verdicts (`deno/maieutics-deno-repl/input_mailbox.ts`).

Probes run against the exact target topology (real `Deno.serve()` in the host
role, plugin worker at `deno.permissions: "none"`, mount projection,
`httpCodec` on both sides) validated body transfer, streaming responses,
cancellation propagation into the worker's producer stream, concurrent
requests, live `request.signal` projection, and admission-hook blocking
verdicts. Evidence is listed under Verification evidence.

## Decision

### 1. The plugin host process owns the only listener; the kernel owns its address

The host runs one `Deno.serve()` on loopback. The kernel allocates the address
and generates a per-host-instance entrance token, both passed through the
plugin host's allowlisted environment (extending `AllowedEnvironmentNames`,
following the `MAIEUTICS_REPL_IPC` pattern); the host reports the bound
address and its mount registry back over the control bus, in the style of
`host.repl.spawned` reporting. Browser → host → plugin worker directly. The
kernel and the control bus are never in the request data path: binary bodies
remain native binary streams end to end (invariant 26), and streaming
responses are not bounded by any RPC timeout.

### 2. One mount per plugin; the plugin routes internally

External shape: `/<entrance-token>/plugins/<pluginId>/…`. The host strips the
mount with strict path-boundary matching and projects the URL onto the
plugin's virtual root (for example `http://plugin.invalid/…`), so the plugin
sees itself as `/` and never learns its external prefix. Relative URLs are the
mount-portable contract; root-relative `Location` headers are reverse-
projected by the host. The host parses no request or response bodies. There is
exactly one root handler per plugin: no manifest field, no entrypoint field,
no per-export mounts. Internal path routing is the plugin's business.

### 3. Declaration is `provide()` into an SDK-owned service extension point; the types are Deno's

`@maieutics/plugin-sdk/http` (SDK subpath export, matching `./interop` and
`./reactive` conventions) exports a single contract identity:

```ts
export const http = defineServiceExtensionPoint<Deno.ServeHandler>("http");
```

A plugin declares its root by providing into the collection:

```ts
import { provide, signal } from "@maieutics/plugin-sdk";
import { http } from "@maieutics/plugin-sdk/http";

provide(http, signal(async (request, info) => {
  // request: Request, info: Deno.ServeHandlerInfo — same shape as a
  // Deno.serve handler; route on new URL(request.url).pathname internally.
}));
```

The element type is `Deno.ServeHandler` and the handler info is Deno's
`Deno.ServeHandlerInfo` (`remoteAddr`, `completed`) — no custom SPI types. The
contract is service-kind, so the provided handler converts to a remote actor
reference; the host (the aggregator for this contract) resolves it once per
mount and then performs direct worker-actor calls per request. Updating the
handler is a signal value change; `unprovide` withdraws the mount; a worker
that stops has its contribution withdrawn automatically by the existing
provider-dead path.

### 4. Wire codec: moved Request/Response with transferred bodies and a live projected signal

An SDK-internal `httpCodec` is registered on both sides (host `spawn` codecs,
SDK `serveWorker` codecs). It splits fetch objects into their minimal
primitives (URL/method/status/headers + body) and rebuilds standard objects on
the receiving side. On messageport transports (the only path today: plugin
workers are in-process workers of the host) the body stream is natively
transferred — zero copy, pull-based backpressure preserved; on framed
transports the body degrades to chunked relay through the iterable codec,
which is the expected behavior, not a regression.

`Request.signal` is propagated by projection, not transfer: `AbortSignal` is
neither transferable nor faithfully cloneable, and `new Request()` always
mints a fresh never-aborting signal, so the codec nests the source signal
through the built-in abort-signal codec (a MessageChannel projection that
rebuilds a real `AbortSignal`) and shadows it onto the rebuilt Request as an
own `signal` property. The plugin reads `request.signal` and gets a live
signal; no extra handler parameter exists. The projection source is
`req.signal` taken as-is (current legacy semantics: aborts at request end —
client abandonment or successful delivery). This is deliberate: legacy
semantics guarantee the projection channel is torn down for every request
(the abort-signal codec's GC-based release does not apply to `AbortSignal`),
and it avoids `--unstable-no-legacy-abort`. The precise client-disconnect
verdict is `info.completed` rejecting with `Deno.errors.Interrupted`.

### 5. Cancellation contract: three legs, no default timeouts

1. Request side: the projected `request.signal` is load-bearing. An aborted
   upload surfaces as a body-read error same-realm (`BadResource`), but across
   the transfer the reader hangs instead — verified — so a handler awaiting
   the next body chunk must race it with `request.signal`.
2. Response side: client disconnect cancels the response body stream, which
   propagates natively through the transferred stream into the worker's
   producer (`ReadableStream.cancel()` runs in the plugin).
3. Delivery verdict: `info.completed` — resolves when the response (including
   body) is fully delivered, rejects with `Deno.errors.Interrupted` when the
   client disconnects mid-delivery.

There are no default request timeouts (invariant 8: completion follows
protocol state). Worker death fails in-flight calls and errors transferred
streams; the host maps them to 502/503.

### 6. Entrance security: loopback, path token, Host validation

The listener binds loopback only. The primary gate is the per-host-instance
high-entropy entrance token in the mount path, issued by the kernel and
exposed to the user through kernel-rendered links. The token is proof-of-
possession per request: it survives drive-by cross-origin requests, DNS
rebinding, and opaque-origin sandboxed iframes (a path token does not depend
on ambient credential attachment). Mandatory `Host` header validation against
the bound address closes rebinding independently. Plugin responses are sent
with `Referrer-Policy: no-referrer` so the token never leaks via Referer.

A cookie-based gate was evaluated and rejected as the primary mechanism:
cookies are ambient credentials and do not stop drive-by state-changing
requests; opaque-origin sandboxed iframes (the planned embedding, decision 7)
do not attach cookies on cross-origin fetch unless `SameSite=None` is used,
which reintroduces CSRF exposure; and all plugins share one origin, so cookie
scoping cannot separate plugins. Cookies may supplement UX later; they are
never the entrance control.

### 7. Browser isolation: sandboxed iframe plus host-controlled CSP

Plugin pages are embedded in `<iframe sandbox="allow-scripts">` (no
`allow-same-origin` → opaque origin), and the host injects a baseline CSP on
`text/html` responses (`default-src 'self'; connect-src 'none'; img-src
'self' data:';` final policy tuning deferred). This keeps two independent
boundaries: the plugin worker's zero Deno permissions and the browser page's
opaque origin. `Deno.upgradeWebSocket` is unsupported inside the SPI — the
upgrade needs the real socket — so server-push uses SSE over the transferred
response stream.

### 8. Error and header-hygiene contract

A handler throw maps to `500` with an empty body; the error detail
(`RemoteError`) is logged host-side only. The host strips hop-by-hop headers
(`Connection`, `Transfer-Encoding`, …), replaces `Host` to match the virtual
projection, injects CSP and `Referrer-Policy`, and reverse-projects
`Location`. It never scans or rewrites bodies.

### 9. Admission hooks: synchronous contract-level verdicts on `provide()`

Extension point aggregators may install one synchronous admission hook per
contract:

```ts
setAdmissionHook(ep, (context) => {
  // context: extensionPoint, providerSpecifier (source plugin),
  // providerModule (providing ES module URL via the existing load hook),
  // existingProviders (live contributor specifiers)
});
```

Verdict convention: return `void` to accept; return a `string` to reject with
a reason (`AdmissionRejected`); throw to reject with that error (`name` +
`message` cross isolates; stacks do not). Hooks must be synchronous and fast —
no I/O, no long loops, and no calls into the providing worker (its thread is
parked; a callback would deadlock to the timeout). This is consistent with
ADR 0020's one-way call direction.

On remote `provide()`, the SDK performs one blocking handshake before
aggregating: a small per-attempt SharedArrayBuffer mailbox is posted to the
aggregator together with the admission context; the provider parks on
`Atomics.wait` (bounded; on timeout it fails closed with `AdmissionTimeout`)
and the verdict is thrown in place at the `provide()` call site — the
developer sees a normal local validation failure at their own stack frame
instead of an asynchronous unhandled rejection and a half-registered module.
This is the same mechanism as the REPL input mailbox. After acceptance the
ordinary asynchronous aggregation proceeds unchanged. Local (same-process)
provides invoke the hook directly.

For the `http` contract the host installs the hook that enforces one root per
plugin (duplicate `providerSpecifier` prefix in `existingProviders` → reject):
the second providing worker of the same plugin fails at its own `provide()`
call site while the first mount stays intact. There is no `provideHttp`
helper and no per-contract provide function; the local worker-level guard of
earlier drafts is subsumed by the hook. Hook rejection replaces
aggregation-failure states for this contract.

## Consequences

- `@ghostflyby/worker-actor` moves 0.4.0 → 0.6.0 (additive: codec interface
  unchanged; new transfer policy surface). Pins to update:
  `deno/deno.json`, `deno/maieutics-plugin-sdk/deno.json`, and the host's
  materialized config (`PluginHostModule`). Deno floor is ≥ 2.6 for
  transferable streams; `--unstable-worker-options` is already required and
  already passed by `PluginHostProcess`.
- Plugin workers keep `deno.permissions` unchanged — HTTP service needs no
  Deno grant. The capability is enforced by the admission hook and the mount
  table (invariant 23: the kind is enforced by its owning layer). Whether the
  http admission hook additionally consults the layered permission overlay
  (ADR 0018) is an open item.
- The host gains its first inbound listener. Its address, token, and registry
  reporting need kernel wiring (env var, control-bus message family,
  `AllowedEnvironmentNames` extension) and a `Maieutics.Jupyter` surfacing
  decision for links shown to users.
- Hot reload inherits mount lifecycle for free (provider withdrawal), but the
  host needs a bounded drain before worker replacement so in-flight requests
  complete or fail deterministically.
- Long-running verification items: per-request signal-projection channel
  teardown under sustained load (the probe observed a lingering handle after
  main completed), Mux-transport SAB passthrough, and Windows parity.

## Alternatives considered

- Manifest or entrypoint HTTP declarations — rejected: routing decided by
  configuration invites per-mount config surface; one root plus internal
  routing keeps the declaration in code and the registry derived.
- Per-export binding-name mounts (multiple handlers per plugin) — rejected:
  URL stability under growth requires uniform prefixes, and binding names
  added a second identity dimension; one root subsumes it.
- Cookie-based entrance auth — rejected as primary (decision 6).
- Kernel-fronted proxy or UDS listener — deferred: a thin stream proxy behind
  the kernel's single entrance is a clean upgrade path (browsers cannot reach
  a UDS directly), but it puts the kernel back into the data path hop; not
  needed while the entrance token + Host validation stand.
- Per-plugin real listeners via `net` grants — the escape hatch the design
  exists to remove; not offered.
- Asynchronous admission verdicts — rejected: rejections would surface as
  unhandled rejections and leave modules half-registered; in-place synchronous
  throwing keeps the call stack honest.
- Excluding `request.signal` from the SPI (streams only) — rejected after
  probing: the rebuilt Request's signal is dead by construction, the projected
  signal is verified end to end, and it is the only working request-side
  cancellation channel once the body stream has been transferred.

## Verification evidence

Probes (local, outside this repository; to be folded into integration tests):

- `worker-actor/examples/http_probe/` — real `Deno.serve()` host + worker at
  `permissions: "none"` + mount projection + httpCodec: body echo,
  incremental streaming, client-cancel propagation into the worker's
  producer, concurrent requests, `content-length` + stream body
  reconstruction, explicit and projected `AbortSignal` delivery, upload-abort
  body hang (documented behavior), post-run handle liveness (open item).
- `deno-serve-facts` probe — `ServeHandlerInfo` fields (`remoteAddr`,
  `completed`), legacy `request.signal` abort semantics, same-realm
  `BadResource` on aborted upload, `new Request()` signal non-inheritance.
- `/tmp/admission_probe/` — admission mailbox: synchronous accept, in-place
  rejection with cross-isolate error name/message at the calling frame,
  bounded-timeout rejection, module evaluation continuing after throws.

## Out of scope this ADR

- WebSocket tunneling through the mount (SSE only for push).
- Final CSP policy and per-plugin CSP tuning.
- Entrance token rotation and revocation.
- Whether the admission hook consults the ADR 0018 permission overlay.
- Kernel↔host message naming for address/registry reporting.
- Notebook-frontend embedding details (Origin allowlists).

## References

- ADR 0016 — script plugins and extension points
- ADR 0018 — declarative permission store; decision 8 (plugin host trust model)
- ADR 0020 — REPL under the extension host; actor boundary and one-way calls
- `@ghostflyby/worker-actor` 0.6.0 — `core/transfer.ts`, `TRANSPORT.md`,
  `examples/request_stream/` (Request/Response movement)
- `deno/maieutics-deno-repl/input_mailbox.ts` — blocking SAB mailbox precedent
- `deno/maieutics-plugin-sdk/reactive.ts` — `provide`/`unprovide`,
  `CURRENT_MODULE` load hook, `currentWorkerSpecifier`
