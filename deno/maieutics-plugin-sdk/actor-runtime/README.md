# Maieutics Actor Runtime

The product-owned TypeScript actor runtime, imported from `worker-actor` 0.7.0 at commit
`bbc0d235ae54455147af6ad399edf5d64dc0be95` in
[the original repository](https://github.com/ghostflyby/worker-actor). The original Apache-2.0
license and provenance are retained in `LICENSE` and `NOTICE`.

This directory is the single source for Worker, process, and transport-based actors used by the
plugin host, plugin SDK, and REPL. It depends on Web platform primitives and runtime adapters, not
on plugin discovery, kernel sessions, frontend rendering, or the Agent. Product-specific actor
references remain in `../actor_ref.ts`; permission enforcement remains in the owning host.

The sources ship inside the plugin SDK so the SDK remains independently publishable without a second
runtime package. The SDK exports `@maieutics/plugin-sdk/actor`, `/actor/codec`, `/actor/codecs`, and
`/actor/browser`. Existing bare `@ghostflyby/worker-actor` aliases resolve to these same local files
in the workspace and materialized process configs. No actor runtime is downloaded from JSR. See ADR
0052 for ownership and compatibility boundaries.

Tests and their fixtures are kept with the runtime. From the SDK directory, run:

```sh
deno task test:actor
```

The workspace test command also runs this suite. Tests and fixtures are excluded from the SDK
publication and embedded executable resources. Runtime sources, license, and provenance are
materialized beside the SDK for both the plugin host and REPL.

## Permissions

Importing the runtime starts no worker, process, or network connection. Worker creation requires the
owner's module read authority; process actors require the owner's run and environment authority;
WebSocket adapters require the owner's network authority. This move adds no product permission grant
or independent process launch path.

Browser runtime support is described below and in ADR 0053. Product frontend admission and notebook
integration remain separate work.

## Browser connections

Import `@maieutics/plugin-sdk/actor/browser` when bundling for a browser. It exports Worker actors,
Web-standard transports, codecs, and `createActorConnection`; it excludes process creation and the
V8 wire adapter. Browser artifact checks reject residual `node:` imports or `Deno` runtime access.
The complete browser module graph excludes the host-only `spawn_process.ts` and V8 adapters; Worker
creation is shared through `spawn_worker.ts`. The default host entry retains process creation and
its existing V8-based `fromWebSocket` adapter.

The host selects a `WireCodec` with synchronous `encode(value)` and optionally asynchronous
`decode(message, signal)`. Its value contract must cover the data and codec placeholders exchanged
by both peers. Successful RPC replies may omit `value`; omission means `undefined`, so plain JSON
codecs support `void` RPC and callback results without an undefined marker. Explicit `null` remains
`null`. Other payload values still follow the chosen codec's documented value contract. No browser
wire encoding is chosen implicitly. `createBinaryGraphCodec()` supplies the portable production
encoding in ADR 0055: cyclic/shared graphs, native binary spans and bounded object tables. The real
browser conformance suite uses that same codec. Hosts establish and authorize an OPEN WebSocket
before handing ownership to `webSocketTransport`.

```ts
import {
  createActorConnection,
  createBinaryGraphCodec,
  webSocketTransport,
} from "@maieutics/plugin-sdk/actor/browser";

// socket is OPEN and host-authorized; both peers explicitly select this encoding.
const wireCodec = createBinaryGraphCodec();
const connection = createActorConnection<{ counter: { increment(): number } }>(
  webSocketTransport(socket, { codec: wireCodec }),
  { actors: { ui: { describe: () => "Notebook view" } }, signal: viewLifetime },
);
await connection.ready;
const counter = connection.actor("counter");
await counter.increment();
counter[Symbol.dispose](); // release only this reference
connection.close();
await connection.completion;
```

Each connection owns its codec registry, advertised surfaces, calls, cancellation signal, and
completion. Calls target only advertised names and own methods. They do not use the Worker-global
acquire router. Asynchronous methods may interleave; business serialization belongs to the owner.
Closing is terminal: calls reject, signals abort, channels and subscriptions release, and completion
joins cooperative dispatches and generator cleanup. Reconnection creates new references; writes are
never replayed automatically. MessagePort owners must supply a lifetime signal for abrupt peer death
that the platform cannot report as a channel event. Only `spawn(new Worker(...))` tolerates untyped
plugin diagnostics on the shared Worker global. `spawn(transport)` uses a dedicated Actor protocol
endpoint and fails its owner on malformed frames, including records without `type`.

Worker/process Actor handles reserve `dispose`, `completion`, and `then` from the remote method
surface. Pool factories are evaluated synchronously: construction failures reach the caller and
cancel already-starting members. If members have already started, the thrown
`ActorPoolConstructionError` exposes the original error as `cause` and a `completion` promise; await
that promise to observe the construction failure together with any cleanup failures. A replacement
factory failure is reported through `onMemberDead`. Routed/pinned calls retain their selected
member's own failure, including death during selection; another slot's failure cannot replace it.
Calls with no selected member use a pool-wide factory failure only when no member is live.
Replacements run on owned, cancellable tasks so repeated handshake failures cannot recurse
synchronously. Disposal cancels scheduled replacements and joins startup and retired-member cleanup.
`dispose()` terminates the owner and returns the shared `completion` promise; codec cleanup failures
are reported there after other teardown stages finish. `failAll()` is terminal and idempotent: it
requests all cleanup without throwing, rejects later codec admission, and `join()` reports recorded
cleanup errors. Actor-side `serveWorker`/`serveProcess`/`serveNode` return a runtime handle with the
same observable completion. Channel/transport `close()` makes the terminal transition synchronously
without throwing observer errors; `completion` joins owned channel cleanup and reports bounded, flat
aggregates deduplicated by error identity. Distinct errors with the same message remain distinct.
Close observers are synchronous and protected even when registered late. Reading that
channel/transport's `completion` again includes late failures; a previously returned, settled
promise remains a snapshot. `messageerror` establishes a decode cause before cleanup.

AbortSignal codecs release both endpoints after terminal status. A signal retained for later use is
a live capability and consumes a channel until abort, explicit release, or owner closure; RPC return
alone does not retire it. End its owning scope with `releaseAbortSignal(signal)` from
`@maieutics/plugin-sdk/actor/codecs` (also exported by the browser entry). Releasing a decoded
signal retires that endpoint; releasing an original signal retires all its exported endpoints.
Remote signals abort on retirement with `ActorTransportError` whose `code` is `"closed"`, rather
than the platform's default `AbortError`. Transport failure instead preserves its terminal cause; a
source abort preserves the transmitted source reason. The original source signal is not aborted.
Repeated release is harmless. Hosts must release never-aborted signals when finished with them to
keep admission bounded; automatic collection is not a lifetime guarantee.

The handshake checks protocol version, transport kind, and ordered codec tags. Runtime protocol 2
adds one-credit-per-`next()` streaming; version 1 peers fail the handshake. Creating a stream does
not enumerate it, and releasing it runs the producer's cleanup. A stream has one consumer and at
most 128 outstanding pulls. Limits bound channels, shared inbound mailboxes, decoding admission,
pending RPC calls, and WebSocket bytes. Admission/decode/send failures terminate the connection with
`ActorTransportError`; a bad result rejects its correlated call before cleanup.

Run `deno task test:actor:browser` from the SDK directory for real headless Chromium conformance.
Set `MAIEUTICS_ACTOR_BROWSER` to a browser executable when it is not at a standard Chrome/Edge path.
The runner owns an isolated profile, local server, process, and verdict channel, and removes them on
completion. Browser scenarios cover binary RPC, bidirectional capabilities, callbacks, stream
backpressure, cancellation, errors, disconnect isolation, MessageChannel, and browser Workers,
including uncloneable arguments, callback results, worker results, and plain JSON void replies. CI
installs a pinned Chrome for Testing archive and checks its version. Runner cleanup shares a 10s
total budget, escalating SIGTERM to SIGKILL after 2s. `deno task check:publish` asserts the actual
SDK publish plan excludes tests and fixtures.

The codec preserves object identity, Map/Set cycles, undefined, sparse arrays, number edge cases,
BigInt, the eleven supported typed-array constructors, ArrayBuffer/DataView, Date/RegExp and native
errors. Binary view offsets and shared backing are retained. Implicit backing buffers expose only
the transmitted view spans; other bytes reconstruct as zeros, preventing pooled-slab disclosure.
Passing a backing buffer explicitly includes its full contents. Defaults bound each frame and
reconstructed buffer storage to one MiB, 16,384 objects, 65,536 items/array slots and depth 64. Use
the existing iterable codec with bounded independent chunks for larger transfers. Custom
prototypes/stateful values still require payload codecs; SharedArrayBuffer requires a snapshot. See
ADR 0055 for the exact value contract and versioned binary layout.

The runtime itself grants no product authority. ADR 0054/0055 implement notebook editor/session/
generation admission and a separate binary data channel. The specifier-based acquire router and
reverse REPL execution remain unavailable to that browser attachment.
