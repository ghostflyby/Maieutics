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
The default host entry retains its existing V8-based `fromWebSocket` adapter.

The host selects a `WireCodec` with synchronous `encode(value)` and optionally asynchronous
`decode(message, signal)`. Its value contract must cover the data and codec placeholders exchanged
by both peers. No browser wire encoding is chosen implicitly. Binary remains binary; the conformance
fixture uses native binary attachments and is not a production encoding standard. Hosts establish
and authorize an OPEN WebSocket before handing ownership to `webSocketTransport`.

```ts
import { createActorConnection, webSocketTransport } from "@maieutics/plugin-sdk/actor/browser";

// socket is OPEN and host-authorized; wireCodec is the encoding agreed by both endpoints.
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
that the platform cannot report as a channel event.

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
backpressure, cancellation, errors, disconnect isolation, MessageChannel, and browser Workers.

This is a runtime foundation. The product's `actor_ref.ts` acquire routing, authenticated notebook
session/view/generation admission, and renderer integration still require a separate design.
