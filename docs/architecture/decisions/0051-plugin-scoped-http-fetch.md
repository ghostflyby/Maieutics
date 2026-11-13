# ADR 0051: Plugin-scoped HTTP and Vue Fetch

Status: Accepted; transport foundation implemented

Date: 2026-11-12

## Decision

HTTP is the primary transport for new plugin component data and actions. Reuse ADR 0021's
host-mounted `Deno.ServeHandler` roots and transferred Request/Response bodies. Do not build a new
generic RPC message bus around form comms. Existing forms retain their current comm contract while
the component integration is developed.

The standalone output ESM exports `createPluginHttpClient(baseUrl, options?)` and `useFetch()`.
The host creates a client with a server-issued mount URL; a synchronous Vue setup obtains its bound
Fetch function through injection. `mountVueIsland(element, code, progressive?, client?)` installs
that client before mounting. The component compiler recognizes only the named `useFetch` import
from the virtual `@maieutics/output-client` module, including import aliases. The module is linked
to the same bundled Vue runtime; arbitrary imports and transport construction are not compiler
capabilities.

Fetch accepts a plugin-relative string path and standard RequestInit and returns the native
Response. JSON, text, FormData, Blob, bytes and streams keep HTTP semantics. No implicit JSON/base64
encoding, whole-body buffering, cache, default deadline, retry or reconnect is added. Browser-native
upload restrictions still apply; a streaming response does not imply that every browser supports
streaming uploads over every HTTP version. Callers choose content types and decoders, check status,
and consume or cancel bodies. Functions, reactive proxies and object identity never cross HTTP.

## Authority and lifecycle

Each live HTTP registration receives an independent random access token. The authenticated
host-registry mount descriptor gains optional `accessToken`; the kernel publishes page URLs only
for approved plugins with live scoped mounts. Older descriptors remain readable but cannot cause
a host-wide credential to be exposed as a component URL. The host entrance remains an internal
administrative capability. Changing the plugin ID in a scoped URL does not authorize another mount.
Root-relative redirects preserve the presented credential instead of upgrading it to the host token.

The host binds HTTP collection ports to the sending worker's actual plugin and specifier. Provider
keys cannot claim or remove another worker's registrations. Stopping/reloading a worker withdraws
its mounts and closes its collection ports synchronously; a new registration uses a fresh token.
Approval revocation follows the same worker-stop path. These tokens authorize a plugin root, not
an individual notebook session; explicit session/view/generation context and scoped business-model
attachment remain subsequent work.

HTTP admission reserves one root per plugin before waking the providing worker. The reservation
belongs to the registration, including an initial or later `undefined` value; `undefined` removes
the live mount and its URL but does not release root ownership. A second worker (or a second
registration in the same worker) is rejected at its `provide()` call. Only `unprovide`, stream
completion/failure, or worker withdrawal releases the reservation. Admission policy uses these
reservations, not just live mounts. Worker identity is derived by the host; mapping it for policy
evaluation is not a separate authentication gate. Unexpected change-stream failures log a fixed
diagnostic and release only the affected registration. Service changes use the existing
per-element collection codec, so replacing a function handler does not attempt structured cloning.

A published HTTP root is an active actor reference and pins its worker against idle reclamation.
Explicit stop/reload/revocation still withdraws it. Idle maintenance has an awaitable completion
boundary so tests can advance its supplied clock and assert completed stops without sleeping.

The client validates paths within its mount and forces `credentials:omit`, `redirect:error` and
`referrerPolicy:no-referrer`. Server-side token validation supplies isolation independently of the
helper. Scoped URLs support credentialless CORS, including opaque origins and preflight, only after
token/mount validation. No origin receives the host token through CORS. HTML keeps its existing
restrictive CSP; hosts must explicitly permit the intended connection or inject a native streaming
Fetch adapter. This change does not enable arbitrary scripts or relax plugin-page isolation.

Each component owns an abort scope, combined with caller and host-client scopes. Unmount aborts
that component's requests and active response bodies without stopping another view. Host disposal
aborts all requests in that client and rejects subsequent calls. Fetch resolving at headers does
not release the cancellation scope. The caller owns returned bodies; no response is automatically
read or replayed. Partial business operations are not rolled back by transport cancellation.

SSR receives no plugin HTTP authority. It can acquire an unavailable Fetch interface so setup and
client-only mounted hooks compile, but calling it rejects with `plugin_fetch_unavailable`. Browser
components without host injection also fail explicitly. Importing the helper alone neither starts
I/O nor grants model-authored or restored content plugin access. SSR prefetch/hydration caching is
not implemented; it will require explicit read policy and matching snapshot revisions.

`ServeHandlerInfo.completed` is projected through callable then/catch/finally methods: a native
Promise cannot be structured-cloned. Its host-side rejection is observed even when the worker does
not wait. `request.signal` and transferred response cancellation retain ADR 0021's semantics.

Host shutdown attempts storage stop, synchronous disposal and HTTP completion even when an earlier
stage fails. A single failure preserves its original identity; multiple failures are returned in an
`AggregateError` in stage order. HTTP teardown cannot replace the storage failure in a `finally`.
Fault tests use separately owned injected infrastructure, without modifying shared prototypes.

## Verification and next steps

Coverage includes real HTTP and two plugin workers without network grants, cross-plugin rejection,
scoped redirects, CORS preflight, worker-stop revocation, binary echo, gated incremental responses,
response-body cancellation reaching the worker, and worker observation of delivery completion.
Vue tests verify setup injection, missing authority, per-view disposal and terminal host disposal.
The actual VS Code browser test exercises native Fetch, CORS, binary, incremental reads and Vue
unmount using the committed ESM. Compiler tests verify explicit linking and denied SSR I/O. Kernel
tests cover scoped discovery and compatibility with older mount descriptors.

The 2026-11-13 review adds real-worker A → undefined → rejected B → restored A → unprovide → B
coverage, and drives the actual kernel approval revoke/re-approve path against the HTTP listener:
the original scoped URL answers 404 after revoke, re-approval supplies a fresh usable URL, and the
old URL stays revoked. Shutdown coverage includes success, each individual failure, combined
failures and repeated disposal. The admission and combined-failure regressions fail on unfixed code.

Next: approved reusable Vue component delivery, binding each component to its verified plugin mount
in the notebook adapter, session/model projections over HTTP, and explicit SSE/NDJSON subscription
helpers. No catalog, automatic notebook mount injection, generic business-model synchronization,
automatic reconnection or migration of existing forms is claimed by this foundation.

Review validation on macOS arm64 (2026-11-13): 697 Deno tests, 20 Node scenarios, 1,679 .NET cases
(1,671 passed, eight existing platform/external-service skips) and 13 NativeAOT process smokes pass.
The approval revoke/re-approve HTTP test passes against the real kernel manager and Deno host.
Release build, osx-arm64 publish and type/lint/format checks pass. The 26 actual VS Code cases,
offline SSR and committed browser/SSR artifact rebuild checks passed in the 2026-11-12 foundation;
this review changes no browser or SSR artifact sources and does not repeat those checks. Other
platforms are covered by PR CI rather than claimed as local results.
