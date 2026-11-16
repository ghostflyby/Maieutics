# ADR 0053: Browser-capable Actor connections

Status: Accepted

## Context

ADR 0052 makes the Actor runtime product-owned. Its common transport module still imported
`node:v8`, its network decoder swallowed errors, pending channel closure was incomplete, and
stream producers enumerated without consumer demand. Browser participation also needs an
explicit connection owner instead of the Worker-global registry.

## Decision

Expose `@maieutics/plugin-sdk/actor/browser` for browser bundles. Keep shared MessagePort, Mux,
RPC, codec, and connection primitives free of static host adapter dependencies. Move V8 byte
framing and the existing host WebSocket encoding into `core/transport_v8.ts`; preserve their host
entry exports. Worker creation lives in `spawn_worker.ts`; Deno process creation lives in the
host-only `spawn_process.ts`, with `spawn.ts` retaining the host facade. The browser module graph
must contain neither process creation nor Node adapters, without requiring tree-shaking.
The browser entry supports native Workers and MessageChannel as well as network
connections. Publication and embedded-resource ownership stay with the SDK.

`webSocketTransport` adopts an already OPEN WebSocket and owns its message/error/close listeners
and disposal. The host provides a `WireCodec`, including its supported value set and binary
representation. The library does not infer an encoding or turn binary into JSON/base64. Decode
receives the transport cancellation signal; closing stops the owned wait and observes late
decoder rejection. Existing host-only V8 transport remains available separately.

`createActorConnection` is a symmetric, explicitly owned runtime with a per-connection registry.
Each peer advertises only its provided named plain-object method surfaces. The hello frame is
`{type:"actor:hello",version,kind,codecs,actors}`; unknown fields are tolerated. Version, transport
kind, and ordered codec tags must match. Calls use existing correlated request/response envelopes
on named channels. Unknown future controls are tolerated; malformed known frames fail the owner.
Successful RPC replies may omit `value`; the receiver interprets omission as `undefined`, while
explicit `null` stays distinct. Plain JSON codecs therefore support void RPC and callback replies
without a private undefined marker. Other payload values follow the selected codec's value contract.
Inherited methods and unadvertised surfaces are not callable. This entry does not install a
Worker-global registry or enable the product's specifier-based acquire router.

Each connection exposes `ready`, a terminal `closed` cause, its cancellation `signal`, and
`completion` joining cooperative RPC dispatch and codec cleanup. Explicit reference disposal
releases its channel. Disconnect fails calls once, aborts dependents, and closes established,
unclaimed, and not-yet-acknowledged channels. Queued sends on unresolved tokens survive until
binding, and closure before binding remains terminal. Reconnection creates a fresh connection
and fresh references; no action replay occurs. Async methods may interleave; business owners
retain their existing serialization gates.

Runtime protocol **2** changes streaming to demand credits: `start` or `next` authorizes one
producer iteration, and `release` requests cleanup. Streams remain lazy, have one consumer, and
admit at most 128 outstanding pulls. Version 1 peers fail the handshake. This is the internal
Actor protocol, not a frontend REST/event or persisted notebook format change.

Transport budgets bound channel count, a shared inbound mailbox, asynchronous decoding count and
bytes, pending calls, and WebSocket frame/buffer bytes. Decode or send failure and admission
overflow terminate the owner with a typed `ActorTransportError`; messages are not silently lost
under pressure. Result decoding rejects the correlated call before propagating the fatal error.
Generator cleanup has an observed completion and remains joinable through the codec registry.
`failAll()` requests every codec/channel cleanup once and records failures without throwing;
`join()` reports the aggregate after cleanup. Worker/process handles expose `completion`, and
repeated `dispose()` shares that completion. Actor-side runtime handles expose cleanup completion
too. Send failure closes the owner, rejects pending/future calls, and terminates an owned Worker
or child regardless of cleanup failures. A Worker runtime sends a terminal `__actor-failed`
control frame with its typed transport cause before closing itself, so result-send failures also
settle the owner's pending calls (Worker `close()` alone does not notify the parent). Worker endpoints use their actual structured-clone
surface; only MessagePorts are closed via `close()`, while Worker termination belongs to the owner.

AbortSignal channels use a terminal `release` acknowledgment after aborted status or abort delivery.
Both endpoints unregister on teardown, including pre-aborted signals. A nonterminal signal may
outlive its RPC and remains a live capability. `releaseAbortSignal` explicitly retires capabilities
at scope end without aborting the original source. It releases one decoded endpoint or all exported
endpoints of an original signal. Explicitly retired remote signals abort with
`ActorTransportError("closed")`, not the platform's default `AbortError`; transport failure keeps
its terminal cause and source abort keeps the transmitted source reason. Live capability admission remains
bounded, and owners must release signals that will never abort. RPC completion does not imply release. Codec channels validate
frame records and known payload shapes; malformed frames and dispatch failures carry typed
protocol causes to the connection owner. Decode failure is established before cleanup hooks run.
Transport/channel closure does not throw observer errors from event handlers; their completion
joins owned channel cleanup and reports bounded, flat aggregates deduplicated by error identity.
Late close observers are also protected: subsequent reads of `completion` include their failures,
while a previously returned, settled promise remains a snapshot. Unknown future control types remain
tolerated. Only `spawn(new Worker(...))` ignores diagnostic messages without a string `type`,
because the plugin and Actor share the Worker global. `spawn(transport)` is a dedicated endpoint,
including MessagePort, process IPC and Mux, and rejects typeless frames. Malformed known Actor
frames fail their owner on every source.

Pool factories run synchronously, so construction failure reaches the caller and cancels members
already starting. When construction has already acquired members, `ActorPoolConstructionError`
carries the construction cause and a joinable `completion` that aggregates cleanup failures.
Replacement attempts run as owned cancellable tasks to avoid synchronous recursion; disposal
cancels scheduled attempts and joins startup and retired-member cleanup. Replacement factory failures remain observable through the member-death callback
and rejection of calls when the pool has no live member.

## Verification and remaining integration

Conformance runs the same sources in real headless Chromium against a local Deno WebSocket peer:
typed binary RPC, browser-callable surfaces, callbacks, slow-consumer streaming, cancellation,
remote errors, disconnect isolation, MessageChannel, and browser Workers. The runner checks that
browser module graph and bundles contain no host dependencies and owns its process, profile,
sockets, and server. CI installs Chrome for Testing at a pinned version and checks its version;
runner cleanup has one total budget with SIGTERM-to-SIGKILL escalation. The CI-only browser
sandbox flag applies only to owned loopback fixtures, not product child permissions.
Focused tests control startup cancellation, decode failure, admission saturation, pending channel
binding/closure, RPC failure correlation, and cleanup errors through explicit completion signals.

This does not grant browser code kernel authority. The frontend host must authenticate admission,
bind capabilities to session/view/connection generations, enforce approval revocation, and design
product reference routing before notebook renderers attach. ADR 0051's plugin HTTP surface remains
in effect; this change adds no product network endpoint and no reverse REPL execution capability.
