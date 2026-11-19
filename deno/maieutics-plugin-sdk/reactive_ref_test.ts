import { assert, assertEquals, assertInstanceOf, assertThrows } from "@std/assert";
import { type ReactiveRef, reactiveRefCodec, ReactiveRefError, shareRef } from "./reactive_ref.ts";
import {
  computed,
  effectScope,
  isReadonly,
  isRef,
  ref,
  shallowRef,
  watch,
} from "./vue_reactivity.ts";
import { PayloadCodecRegistry } from "./actor-runtime/core/codec.ts";
import { createBinaryGraphCodec } from "./actor-runtime/core/binary_graph.ts";
import { messageTransport, type Transport } from "./actor-runtime/core/transport.ts";
import type { Channel } from "./actor-runtime/core/channel.ts";
import { createActorConnection } from "./actor-runtime/core/connection.ts";
import { iterableCodec } from "./actor-runtime/core/codecs/iterable.ts";

function until<T>(source: Readonly<{ value: T }>, test: (value: T) => boolean): Promise<void> {
  if (test(source.value)) return Promise.resolve();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      stop();
      reject(new Error("Reactive Ref did not deliver the expected state within the test budget"));
    }, 5000);
    const stop = watch(() => source.value, (value) => {
      if (test(value)) {
        stop();
        clearTimeout(timer);
        resolve();
      }
    });
  });
}

function registry() {
  return new PayloadCodecRegistry().register(reactiveRefCodec).register(iterableCodec);
}
function transfer<T>(
  value: ReactiveRef<T>,
  from: PayloadCodecRegistry,
  to: PayloadCodecRegistry,
) {
  const ports: Transferable[] = [];
  const encoded = from.encode(value, ports);
  return to.decode(structuredClone(encoded, { transfer: ports })) as ReactiveRef<T>;
}
async function cleanup(...registries: PayloadCodecRegistry[]) {
  for (const registry of registries) registry.failAll();
  await Promise.all(registries.map((registry) => registry.join()));
}

Deno.test("shared Ref has a ready undefined value and observes nested Vue state as detached snapshots", () => {
  const source = ref<{ count: number; names: Map<string, number> } | undefined>(undefined);
  const scope = effectScope();
  const shared = scope.run(() => shareRef(source));
  assert(shared);
  try {
    assert(isRef(shared));
    assert(isReadonly(shared));
    assertEquals(shared.value, undefined);
    assertEquals(shared.status.value, { kind: "live", revision: 0 });
    source.value = { count: 0, names: new Map([["a", 1]]) };
    const old = shared.value;
    source.value.count++;
    source.value.names.set("b", 2);
    assertEquals(old?.count, 0, "previous snapshots must not change behind a queued publication");
    assertEquals(shared.value?.count, 1);
    assertEquals(shared.value?.names.get("b"), 2);
    assertEquals(shared.status.value.revision, 3);
  } finally {
    scope.stop();
  }
  assertEquals(shared.status.value.kind, "closed");
});

Deno.test("snapshot failures are typed, close only that Ref, and preserve the last valid value", () => {
  const source = shallowRef<unknown>({ count: 1 });
  const shared = shareRef(source);
  source.value = () => "not data";
  assertEquals(shared.value, { count: 1 });
  const status = shared.status.value;
  assertEquals(status.kind, "closed");
  if (status.kind === "closed") assertInstanceOf(status.error, ReactiveRefError);
  assertThrows(() => shareRef(shallowRef(Symbol("bad"))), ReactiveRefError);
  assertThrows(() => shareRef(1 as never), TypeError);
  shared[Symbol.dispose]();
});

Deno.test("Actor Ref mirrors preserve registry identity, computed tracking, coalescing and explicit release", async () => {
  const owner = registry(), peer = registry();
  const count = ref(0);
  const shared = shareRef(computed(() => ({ count: count.value, doubled: count.value * 2 })));
  try {
    const remote = await transfer(shared, owner, peer);
    assert(remote === await transfer(shared, owner, peer));
    const doubled = computed(() => remote.value.doubled);
    count.value = 1;
    count.value = 2;
    count.value = 3;
    await until(doubled, (value) => value === 6);
    assertEquals(remote.value.count, 3);
    remote[Symbol.dispose]();
    assertEquals(remote.status.value.kind, "closed");
    assertEquals(shared.status.value.kind, "live", "releasing a mirror must not stop its owner");
    count.value = 4;
    assertEquals(remote.value.count, 3);
    // A fresh transfer after release must establish a live subscription, not reuse a dead mirror.
    const next = await transfer(shared, owner, peer);
    assert(next !== remote);
    assertEquals(next.value.count, 4);
  } finally {
    shared[Symbol.dispose]();
    await cleanup(owner, peer);
  }
});

Deno.test("forwarded Ref mirrors retain identity and propagate upstream closure without killing other refs", async () => {
  const a = registry(), b = registry(), c = registry();
  const source = ref(1), other = shareRef(ref(10));
  const shared = shareRef(source);
  try {
    const middle = await transfer(shared, a, b);
    const end = await transfer(middle, b, c);
    const independent = await transfer(other, a, c);
    source.value = 2;
    await until(end, (value) => value === 2);
    middle[Symbol.dispose]();
    await until(end.status, (status) => status.kind === "closed");
    assertEquals(independent.status.value.kind, "live");
    assertEquals(shared.status.value.kind, "live");
  } finally {
    shared[Symbol.dispose]();
    other[Symbol.dispose]();
    await cleanup(a, b, c);
  }
});

Deno.test("registry failure closes mirrors and settles pending state pulls", async () => {
  const owner = registry(), peer = registry();
  const shared = shareRef(ref(1));
  try {
    const remote = await transfer(shared, owner, peer);
    peer.failAll();
    assertEquals(remote.status.value.kind, "closed");
  } finally {
    shared[Symbol.dispose]();
    await cleanup(owner, peer);
  }
});

Deno.test("binary Actor connection transports live Ref snapshots, native graphs and business actions", async () => {
  const wire = createBinaryGraphCodec();
  const left = messageTransport({
    send: (value) => queueMicrotask(() => right.deliver(wire.encode(value))),
    decode: (value) => wire.decode(value as Uint8Array),
  });
  const right = messageTransport({
    send: (value) => queueMicrotask(() => left.deliver(wire.encode(value))),
    decode: (value) => wire.decode(value as Uint8Array),
  });
  const source = ref(0);
  const shared = shareRef(computed(() => {
    const data: { count: number; bytes: Uint8Array; self?: unknown; missing: undefined } = {
      count: source.value,
      bytes: new Uint8Array([0, 255]),
      missing: undefined,
    };
    data.self = data;
    return data;
  }));
  const api = { state: () => shared, increase: () => ++source.value };
  const server = createActorConnection(right, {
    actors: { counter: api },
    codecs: [reactiveRefCodec],
  });
  const client = createActorConnection<{ counter: typeof api }>(left, {
    codecs: [reactiveRefCodec],
  });
  try {
    await Promise.all([server.ready, client.ready]);
    const actor = client.actor("counter");
    const remote = await actor.state();
    assertEquals(remote.value.bytes, new Uint8Array([0, 255]));
    assert(remote.value.self === remote.value);
    assertEquals(await actor.increase(), 1);
    await until(remote, (value) => value.count === 1);
    assertEquals(remote.status.value.kind, "live");
    server.close();
    await until(remote.status, (status) => status.kind === "closed");
  } finally {
    shared[Symbol.dispose]();
    client.close();
    server.close();
    await Promise.all([server.completion, client.completion]);
  }
});

Deno.test("a slow state subscriber retains only the latest revision and consumes one state per pull", async () => {
  const owner = registry();
  const count = ref(0), shared = shareRef(count);
  const transfers: Transferable[] = [];
  const placeholder = structuredClone(owner.encode(shared, transfers), { transfer: transfers }) as {
    port: MessagePort;
  };
  const frames: unknown[] = [];
  const next = Promise.withResolvers<Record<string, unknown>>();
  placeholder.port.onmessage = (event) => {
    frames.push(event.data);
    next.resolve(event.data);
  };
  try {
    // Exceeds the event-stream queue budget without granting any state-delivery credit.
    for (let i = 1; i <= 1500; i++) count.value = i;
    placeholder.port.postMessage({ type: "pull", revision: 0 });
    const state = await next.promise;
    assertEquals(state.type, "state");
    assertEquals(state.revision, 1500);
    const closed = Promise.withResolvers<void>();
    placeholder.port.onmessage = (event) => {
      frames.push(event.data);
      assertEquals(event.data.type, "closed");
      closed.resolve();
    };
    shared[Symbol.dispose]();
    await closed.promise;
    assertEquals(frames.length, 2, "one pull must authorize exactly one state frame");
  } finally {
    shared[Symbol.dispose]();
    placeholder.port.close();
    await cleanup(owner);
  }
});

Deno.test("a synchronous value watcher can dispose a Ref without its status returning to live", () => {
  const source = ref(0), shared = shareRef(source);
  const stop = watch(() => shared.value, () => shared[Symbol.dispose]());
  try {
    source.value = 1;
    assertEquals(shared.status.value.kind, "closed");
    source.value = 2;
    assertEquals(shared.value, 1);
  } finally {
    stop();
    shared[Symbol.dispose]();
  }
});

Deno.test("a real Actor worker accepts and returns reactive capabilities and stops them with its scope", async () => {
  const { spawn } = await import("./actor-runtime/mod.ts");
  const url = URL.createObjectURL(
    new Blob([`
    import { serveWorker } from ${
      JSON.stringify(new URL("./actor-runtime/mod.ts", import.meta.url).href)
    };
    import { shareRef, reactiveRefCodec } from ${
      JSON.stringify(new URL("./reactive_ref.ts", import.meta.url).href)
    };
    import { ref, computed, effectScope, watch } from ${
      JSON.stringify(new URL("./vue_reactivity.ts", import.meta.url).href)
    };
    const scope = effectScope();
    const count = ref(0);
    const state = scope.run(() => shareRef(computed(() => count.value * 2)));
    serveWorker({
      state: () => state,
      increase: () => ++count.value,
      stop: () => scope.stop(),
      async observe(remote) {
        if (remote.value === 42) return remote.value;
        return await new Promise(resolve => {
          const stop = watch(() => remote.value, value => {
            if (value === 42) { stop(); remote[Symbol.dispose](); resolve(value); }
          });
        });
      },
    }, { codecs: [reactiveRefCodec] });
  `], { type: "text/javascript" }),
  );
  const source = ref(0), shared = shareRef(source);
  const actor = await spawn<{
    state(): ReactiveRef<number>;
    increase(): number;
    stop(): void;
    observe(remote: ReactiveRef<number>): Promise<number>;
  }>(new Worker(url, { type: "module" }), { codecs: [reactiveRefCodec] });
  try {
    const remote = await actor.state();
    assert(remote === await actor.state());
    await actor.increase();
    await until(remote, (value) => value === 2);
    const observed = actor.observe(shared);
    source.value = 42;
    assertEquals(await observed, 42);
    await actor.stop();
    await until(remote.status, (status) => status.kind === "closed");
  } finally {
    shared[Symbol.dispose]();
    await actor.dispose();
    URL.revokeObjectURL(url);
  }
});

Deno.test("Ref endpoint admission rejects only the excess export and leaves existing mirrors live", async () => {
  const owner = registry(), peer = registry(), additionalOwner = registry();
  const source = ref(0), shared = shareRef(source);
  const refs: ReactiveRef<number>[] = [shared];
  try {
    const first = await transfer(shared, owner, peer);
    for (let i = 1; i < 1024; i++) {
      const next = shareRef(ref(i));
      refs.push(next);
      await transfer(next, owner, peer);
    }
    const excess = shareRef(ref(1024));
    refs.push(excess);
    const error = assertThrows(() => owner.encode(excess, []), ReactiveRefError);
    assertEquals(error.code, "capacity");
    const rejected = await transfer(excess, additionalOwner, peer);
    const status = rejected.status.value;
    assertEquals(status.kind, "closed");
    if (status.kind === "closed") assertEquals(status.error.code, "capacity");
    source.value = 1;
    await until(first, (value) => value === 1);
    assertEquals(first.status.value.kind, "live");
  } finally {
    for (const ref of refs) ref[Symbol.dispose]();
    await cleanup(owner, peer, additionalOwner);
  }
});

Deno.test("source snapshot budgets and data-only restrictions reject before publishing capabilities", () => {
  for (const value of [new Uint8Array(1024 * 1024 + 1), new Array(65537)]) {
    const error = assertThrows(() => shareRef(shallowRef(value)), ReactiveRefError);
    assertEquals(error.code, "capacity");
  }
  let invoked = false;
  const data = Object.defineProperty({}, "secret", {
    get() {
      invoked = true;
      return 1;
    },
    enumerable: true,
  });
  assertThrows(() => shareRef(shallowRef(data)), ReactiveRefError);
  assertEquals(invoked, false, "snapshotting must not execute arbitrary data accessors");
  assertThrows(() => shareRef(shallowRef({ nested: ref(1) })), ReactiveRefError);
});

Deno.test("malformed Ref pulls close their Actor owner with a typed protocol cause", async () => {
  const { createMux } = await import("./actor-runtime/core/transport.ts");
  const { connectToken } = await import("./actor-runtime/core/channel.ts");
  const { ActorTransportError } = await import("./actor-runtime/core/lifetime.ts");
  const left = createMux((frame) => right.deliver(frame));
  const right = createMux((frame) => left.deliver(frame));
  const owner = registry(), shared = shareRef(ref(0));
  const terminal = Promise.withResolvers<unknown>();
  left.onClose(terminal.resolve);
  try {
    const handle = owner.encode(shared, [], left) as { token: { __mux: "open"; ch: number } };
    const channel = connectToken(right, handle.token);
    channel.send({ type: "pull", revision: -1 });
    const error = await terminal.promise;
    assertInstanceOf(error, ActorTransportError);
    assertEquals(error.code, "protocol");
    assertEquals(
      shared.status.value.kind,
      "live",
      "protocol failure retires endpoints, not business state",
    );
  } finally {
    shared[Symbol.dispose]();
    left.close();
    right.close();
    await cleanup(owner);
  }
});

Deno.test("receiving Ref state preserves the last snapshot when a source later fails observation", async () => {
  const owner = registry(), peer = registry();
  const data = shallowRef<unknown>(1), shared = shareRef(data);
  try {
    const remote = await transfer(shared, owner, peer);
    data.value = { unsupported: () => 1 };
    await until(remote.status, (status) => status.kind === "closed");
    assertEquals(remote.value, 1);
    const status = remote.status.value;
    if (status.kind === "closed") assertEquals(status.error.code, "snapshot_failed");
  } finally {
    shared[Symbol.dispose]();
    await cleanup(owner, peer);
  }
});

for (const closeSide of ["producer", "consumer"] as const) {
  Deno.test(`native Ref endpoints follow their supervised ${closeSide} transport`, async () => {
    const { fromMessagePort } = await import("./actor-runtime/core/transport.ts");
    const { ActorTransportError } = await import("./actor-runtime/core/lifetime.ts");
    const ports = new MessageChannel();
    const producer = fromMessagePort(ports.port1), consumer = fromMessagePort(ports.port2);
    class TrackingRegistry extends PayloadCodecRegistry {
      readonly channels = new Set<Channel>();
      override registerChannel(channel: Channel, transport?: Transport): void {
        this.channels.add(channel);
        channel.onClose(() => this.channels.delete(channel));
        super.registerChannel(channel, transport);
      }
    }
    const owner = new TrackingRegistry();
    owner.register(reactiveRefCodec);
    const peer = registry();
    const source = ref(1), shared = shareRef(source);
    try {
      const transfers: Transferable[] = [];
      const encoded = owner.encode(shared, transfers, producer);
      const remote = peer.decode(
        structuredClone(encoded, { transfer: transfers }),
        consumer,
      ) as ReactiveRef<number>;
      (closeSide === "producer" ? producer : consumer).close(
        new ActorTransportError("closed", "Supervised peer exited"),
      );
      // Parent closure is the strict successor: local cleanup must already have run synchronously.
      if (closeSide === "producer") {
        assertEquals(
          owner.channels.size,
          0,
          "producer transport closure must retire its Ref endpoint",
        );
      } else {
        assertEquals(
          remote.status.value.kind,
          "closed",
          "consumer transport closure must retire its mirror",
        );
      }
      await until(remote.status, (status) => status.kind === "closed");
      assertEquals(remote.value, 1);
      assertEquals(
        shared.status.value.kind,
        "live",
        "one transport must not dispose shared owner state",
      );
      source.value = 2;
      assertEquals(remote.value, 1);
    } finally {
      shared[Symbol.dispose]();
      producer.close();
      consumer.close();
      await cleanup(owner, peer);
      await Promise.all([producer.completion, consumer.completion]);
    }
  });
}

Deno.test("Ref scope release removes its parent transport observers", async () => {
  const { fromMessagePort } = await import("./actor-runtime/core/transport.ts");
  const ports = new MessageChannel();
  const ownerTransport = fromMessagePort(ports.port1), peerTransport = fromMessagePort(ports.port2);
  let observers = 0;
  function observed(transport: typeof ownerTransport) {
    return {
      ...transport,
      onClose(listener: (cause: unknown) => void) {
        observers++;
        const remove = transport.onClose(listener);
        let active = true;
        return () => {
          if (!active) return;
          active = false;
          observers--;
          remove();
        };
      },
    };
  }
  const owner = registry(), peer = registry(), shared = shareRef(ref(1));
  try {
    const transfers: Transferable[] = [];
    const wire = owner.encode(shared, transfers, observed(ownerTransport));
    const remote = peer.decode(
      structuredClone(wire, { transfer: transfers }),
      observed(peerTransport),
    ) as ReactiveRef<number>;
    assertEquals(observers, 2, "each native endpoint must observe its transport");
    shared[Symbol.dispose]();
    await until(remote.status, (status) => status.kind === "closed");
    assertEquals(
      observers,
      0,
      "released Ref capabilities must not remain pinned by transport listeners",
    );
  } finally {
    shared[Symbol.dispose]();
    ownerTransport.close();
    peerTransport.close();
    await cleanup(owner, peer);
  }
});

Deno.test("enumerable symbol keys fail explicitly instead of silently disappearing from Ref data", () => {
  const symbol = Symbol("field");
  for (const value of [{ [symbol]: 1 }, { nested: { [symbol]: 2 } }]) {
    const error = assertThrows(() => shareRef(shallowRef(value)), ReactiveRefError);
    assertEquals(error.code, "snapshot_failed");
  }
  const source = ref<Record<PropertyKey, unknown>>({ count: 1 });
  const shared = shareRef(source);
  try {
    source.value[symbol] = 2;
    const status = shared.status.value;
    assertEquals(status.kind, "closed");
    if (status.kind === "closed") assertEquals(status.error.code, "snapshot_failed");
    assertEquals(shared.value, { count: 1 });
  } finally {
    shared[Symbol.dispose]();
  }
});

for (const kind of ["concurrent", "ahead"] as const) {
  Deno.test(`${kind} Ref pulls fail their owning Mux with a typed protocol cause`, async () => {
    const { createMux } = await import("./actor-runtime/core/transport.ts");
    const { connectToken } = await import("./actor-runtime/core/channel.ts");
    const { ActorTransportError, waitWithSignal } = await import(
      "./actor-runtime/core/lifetime.ts"
    );
    const left = createMux((frame) => right.deliver(frame));
    const right = createMux((frame) => left.deliver(frame));
    const owner = registry(), shared = shareRef(ref(0));
    const terminal = Promise.withResolvers<unknown>();
    left.onClose(terminal.resolve);
    try {
      const handle = owner.encode(shared, [], left) as { token: { __mux: "open"; ch: number } };
      const channel = connectToken(right, handle.token);
      channel.send({ type: "pull", revision: kind === "ahead" ? 1 : 0 });
      if (kind === "concurrent") channel.send({ type: "pull", revision: 0 });
      const error = await waitWithSignal(terminal.promise, AbortSignal.timeout(5000));
      assertInstanceOf(error, ActorTransportError);
      assertEquals(error.code, "protocol");
    } finally {
      shared[Symbol.dispose]();
      left.close();
      right.close();
      await cleanup(owner);
    }
  });
}

Deno.test("readonly mirrors reject object and Map writes while native byte edits remain detached local data", async () => {
  const owner = registry(), peer = registry(), otherPeer = registry();
  const source = ref({ count: 1, names: new Map([["a", 1]]), bytes: new Uint8Array([7]) });
  const shared = shareRef(source);
  try {
    const remote = transfer(shared, owner, peer), other = transfer(shared, owner, otherPeer);
    // Runtime consumers may be JavaScript or otherwise bypass the readonly TypeScript surface.
    const local = remote.value as unknown as typeof source.value;
    local.count = 99;
    local.names.set("a", 99);
    assertEquals(remote.value.count, 1);
    assertEquals(remote.value.names.get("a"), 1);
    assertEquals(source.value.count, 1);
    assertEquals(source.value.names.get("a"), 1);
    local.bytes[0] = 99;
    assertEquals(remote.value.bytes[0], 99, "Vue does not readonly-wrap native typed arrays");
    assertEquals(source.value.bytes[0], 7, "native snapshots must not share owner storage");
    assertEquals(shared.value.bytes[0], 7);
    assertEquals(other.value.bytes[0], 7, "another mirror must not share local snapshot storage");
    source.value.count = 2;
    await until(remote, (value) => value.count === 2);
    assertEquals(
      remote.value.bytes[0],
      7,
      "the next authoritative snapshot must replace local edits",
    );
  } finally {
    shared[Symbol.dispose]();
    await cleanup(owner, peer, otherPeer);
  }
});

Deno.test("binary Mux Ref relays carry updates and upstream failure without closing unrelated mirrors", async () => {
  function pair() {
    const graph = createBinaryGraphCodec();
    const left = messageTransport({
      send: (value) => queueMicrotask(() => right.deliver(graph.encode(value))),
      decode: (value) => graph.decode(value as Uint8Array),
    });
    const right = messageTransport({
      send: (value) => queueMicrotask(() => left.deliver(graph.encode(value))),
      decode: (value) => graph.decode(value as Uint8Array),
    });
    return { left, right };
  }
  const ab = pair(), bc = pair();
  const a = registry(), b = registry(), c = registry();
  const source = ref(1), shared = shareRef(source), independent = shareRef(ref(10));
  try {
    const middle = b.decode(a.encode(shared, [], ab.left), ab.right) as ReactiveRef<number>;
    const end = c.decode(b.encode(middle, [], bc.left), bc.right) as ReactiveRef<number>;
    const other = c.decode(b.encode(independent, [], bc.left), bc.right) as ReactiveRef<number>;
    assert(end === c.decode(b.encode(middle, [], bc.left), bc.right));
    source.value = 2;
    await until(end, (value) => value === 2);
    ab.right.close(new Error("Upstream socket disconnected"));
    await until(end.status, (status) => status.kind === "closed");
    assertEquals(other.status.value.kind, "live");
    assertEquals(shared.status.value.kind, "live");
  } finally {
    shared[Symbol.dispose]();
    independent[Symbol.dispose]();
    ab.left.close();
    ab.right.close();
    bc.left.close();
    bc.right.close();
    await cleanup(a, b, c);
    await Promise.all([
      ab.left.completion,
      ab.right.completion,
      bc.left.completion,
      bc.right.completion,
    ]);
  }
});

Deno.test("supervised abrupt Worker failure closes its Ref mirrors without an orderly closed frame", async () => {
  const { spawn } = await import("./actor-runtime/mod.ts");
  const { assertRejects } = await import("@std/assert");
  const { waitWithSignal } = await import("./actor-runtime/core/lifetime.ts");
  const url = URL.createObjectURL(
    new Blob([`
    import { serveWorker } from ${
      JSON.stringify(new URL("./actor-runtime/mod.ts", import.meta.url).href)
    };
    import { ref } from ${JSON.stringify(new URL("./vue_reactivity.ts", import.meta.url).href)};
    import { shareRef, reactiveRefCodec } from ${
      JSON.stringify(new URL("./reactive_ref.ts", import.meta.url).href)
    };
    const shared = shareRef(ref(1));
    serveWorker({
      state: () => shared,
      crash() {
        queueMicrotask(() => { throw new Error("abrupt-producer-failure"); });
        return new Promise(() => {});
      },
    }, { codecs: [reactiveRefCodec] });
  `], { type: "text/javascript" }),
  );
  const died = Promise.withResolvers<unknown>();
  const worker = new Worker(url, { type: "module" });
  // The test intentionally crashes this child. Keep Deno's default error bubbling out of the test
  // while leaving spawn's own error handler and termination path installed.
  worker.addEventListener("error", (event) => event.preventDefault());
  const actor = await spawn<{ state(): ReactiveRef<number>; crash(): Promise<void> }>(
    worker,
    { codecs: [reactiveRefCodec], onDeath: died.resolve },
  );
  try {
    const remote = await actor.state();
    const crashed = assertRejects(() => actor.crash());
    await waitWithSignal(died.promise, AbortSignal.timeout(5000));
    await crashed;
    assertEquals(
      remote.status.value.kind,
      "closed",
      "onDeath follows registry teardown, not a Ref terminal frame",
    );
    assertEquals(remote.value, 1);
  } finally {
    await actor.dispose();
    URL.revokeObjectURL(url);
  }
});
