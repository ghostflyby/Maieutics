# ADR 0056: Readonly Vue state capabilities across Actors

Status: Accepted

## Context

Extension-point contributions already bridge Vue values through initial values and change streams,
reconstructing a shallowRef in the defining worker. Arbitrary Actor services do not have an
equivalent state capability. Copying Vue implementation objects cannot preserve dependency tracking
between isolates. Polling service methods loses reactive composition, while remote assignments
cannot express asynchronous acknowledgement or business validation.

## Decision

The SDK exports `shareRef(source)` and `ReactiveRef<T>`. The source is a Vue Ref or ComputedRef. The
result is a real readonly computed ref containing detached data snapshots, with a reactive `status`
and an idempotent `[Symbol.dispose]()`. Explicit sharing establishes a lifetime and data boundary;
ordinary refs and reactive objects are not automatically exported. No Vue code or dependency graph
is serialized. The owning worker remains responsible for its business state.

Register `reactiveRefCodec` on both ends of raw Actor connections. Plugin workers, the plugin host
and its HTTP collection registry register it together in lockstep. The lower-level Actor runtime
remains independent of Vue and the SDK. The TypeScript exports are additive; the base Actor protocol
remains version 2. The ordered codec-tag list is itself a wire compatibility contract: both peers
must register exactly the same tags in the same order. Adding this codec to an existing connection
is wire-breaking for an older tree-external consumer even if no shared Ref is sent. Such consumers
must update both peers together or keep their old registration list; there is no capability
negotiation. In-tree plugin host/worker deployment pins SDK imports and updates in lockstep.

### Data and observation

One synchronous Vue watcher per local shared capability reads the source and its nested reactive
data. Snapshots detach proxies, preserve graph cycles/shared identity and use the existing bounded
binary graph encoder (ADR 0055). Values include undefined, native binary and supported native data
containers; functions, nested refs, services, accessors and arbitrary class instances require an
explicit data projection. Own enumerable symbol-keyed properties are rejected with `snapshot_failed`
instead of disappearing; non-enumerable properties are not part of the data projection. Snapshots
use native Uint8Array bytes, never JSON or base64. No additional OS, environment or network
permissions are required.

The source watcher validates snapshots before publication. Initial failures throw
`ReactiveRefError`; later failures close that capability with a typed status and retain the last
valid value. Native values whose internal mutation Vue does not observe, such as typed-array bytes,
require source replacement or another explicit reactive invalidation. Readonly Vue wrappers protect
supported object/container mutations; native mutable snapshots never share storage with the owner.

A source snapshot starts at revision zero and advances on observation. Value and status derive from
one local state slot so synchronous watchers cannot restore a disposed capability to live state.
Undefined is a ready value, distinct from a closed subscription. Several fields requiring coherent
publication should be projected into one ref. There is no multi-ref transaction or post-action
publication barrier: an awaited Actor action can complete before its mirror receives the update.

### State protocol, version one

Tag: `maieutics/reactive-ref/1`.

Each encoded handle carries a stable capability UUID, revision, initial binary snapshot and a peer
MessagePort or Mux token. Installing the channel and capturing the initial snapshot happen
synchronously, before the first asynchronous handoff. The channel reads the current retained state
when a pull arrives, including changes made during that handoff.

```text
consumer -> producer  { type: "pull", revision }
producer -> consumer  { type: "state", revision, snapshot: Uint8Array }
producer -> consumer  { type: "closed", code, message }
consumer -> producer  { type: "release" }
```

The consumer automatically keeps one pull outstanding. A pull grants at most one state frame with a
strictly newer revision. The producer retains its latest snapshot rather than a queue of changes;
slow subscribers may skip intermediate revisions. A second concurrent pull or a revision ahead of
the source is a protocol failure. Terminal delivery does not need state credit. Use existing
AsyncIterable/event streams when every transition must be retained; their queue semantics do not
change.

Snapshots retain the binary graph defaults: 1 MiB, 16,384 objects, 65,536 items and depth 64. Proxy
traversal is also bounded before encoding. Each registry admits at most 1,024 imported capabilities
and 1,024 exported endpoints. An excess export throws a typed request failure; an excess import
returns a closed capability with a typed capacity status and releases its endpoint. Returning that
terminal value avoids the Actor runtime treating RPC result decoding as a fatal protocol error.
Existing subscriptions stay live. Channel and transport budgets continue to apply independently.

### Identity, forwarding and disposal

A receiving registry caches one live mirror per capability UUID. Repeated transfers establish a
fresh endpoint, then release it if the registry already has that mirror. This keeps identity stable
without relying on a sender-side cached channel that might have been released while another RPC
result was in flight. A fresh transfer after release creates a new live mirror.

A mirror can be forwarded through another Actor connection. The intermediate isolate relays the same
identity, revision and detached snapshot under its own lifetime; it does not acquire a direct route
to the original owner. Intermediate loss closes downstream mirrors. Identity deduplication is
registry-local, not a promise of object identity across isolates or restoration on a round trip.

Disposing a source or stopping its Vue scope stops its watcher and closes all exported endpoints.
Disposing an imported mirror releases its subscription and downstream relays, without disposing the
original source or other mirrors. Explicit disposal is the deterministic ownership contract; GC is
not a release signal. Receivers explicitly own the returned mirror, optionally attaching disposal to
their own scope. Repeated acquisitions within one registry share that mirror and its lifetime; they
are not independently counted leases. Multiple consumers must use a common lifetime owner.

Each native Ref endpoint observes the encode/decode transport supplied by its RPC owner. Observed
parent transport closure retires that endpoint; producer-side closure sends a terminal Ref frame
when the dedicated port is still usable. Imported mirrors become closed, downstream relays retire,
and the registry joins channel completion. Endpoints remove parent listeners on release so a long
lived transport does not pin disposed capabilities. A shared source survives one subscriber's
connection failure. Reconnection requires a fresh acquisition; actions are never replayed. Malformed
frames use the Actor channel's protocol-failure path, and transport-send failures remain observed by
registry completion rather than escaping a source watcher into a business action.

**Peer-death detection is the transport host's responsibility.** A transferred MessagePort has no
peer-close/death notification. Binding to a parent only propagates a failure that the parent can
observe; it cannot detect an isolate silently disappearing. Raw `createActorConnection` users must
close/abort their connection from worker/process/socket supervision. `spawn(Worker)` observes Worker
error events and teardown it initiates; it cannot promise to observe a caller's independent
`worker.terminate()` or a silent worker-global `close()`. Without an observed parent failure or a
Ref terminal frame, a mirror can remain live with its last snapshot.

Current plugin dependency cascades stop declared consumers when their provider dies. Dynamically
acquired providers outside that declared closure do not yet have a provider-death lifetime bound to
each direct Ref/RPC port; those mirrors can likewise remain stale. Their consumers need explicit
invalidation/disposal until per-provider acquisition supervision is added. This change does not
claim automatic death detection for those paths.

## Adoption and limits

Ordinary plugin Actor services can return shared refs and accept them as arguments today. Mutations
use explicit awaitable service methods. Extension-point collection transport remains unchanged: its
registration/withdrawal and ordered-event behavior must not be silently replaced by coalesced state
delivery. The current form comm contract, editor authorization, HTTP-first component channel, SSR
and notebook persistence are also unchanged. They can adopt this primitive through explicit adapters
later; neither browser transport credentials nor automatic plugin component bindings are introduced
by this change.

Tests cover real workers and plugin dependency routing, MessagePort and binary Mux connections,
initial undefined, local computed tracking, nested reactive state, repeated identity, release and
reacquisition, native and Mux relay closure, supervised abrupt Worker failure, parent transport
closure/listener cleanup, snapshot failure, symbol-key rejection, invalid/concurrent/ahead pulls,
behavioral readonly/native-copy semantics, bounded admission and slow consumers.
