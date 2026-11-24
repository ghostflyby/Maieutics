import { createActorConnection, fromMessagePort, spawn, webSocketTransport } from "../browser.ts";
import { jsonWireCodec, testWireCodec } from "./browser_wire_codec.ts";
import type { WireCodec } from "../browser.ts";
import type * as WorkerModule from "./browser_worker.ts";

export interface BrowserServices {
  service: {
    empty(): void;
    invokeEmpty(callback: () => Promise<void>): Promise<void>;
    nothing(): null;
    graph(value: unknown): unknown;
    increment(): number;
    numbers(): AsyncIterable<number>;
    produced(): number;
    released(): Promise<void>;
    apply(
      callback: (bytes: Uint8Array) => Promise<Uint8Array>,
      bytes: Uint8Array,
    ): Promise<Uint8Array>;
    askUi(): Promise<string>;
    cancel(signal: AbortSignal, started: () => Promise<void>): Promise<boolean>;
    wait(started: () => Promise<void>): Promise<void>;
    fail(): never;
  };
}
function assert(value: unknown, message: string): asserts value {
  if (!value) throw new Error(message);
}
async function open(endpoint: string, codec: WireCodec = testWireCodec) {
  const socket = new WebSocket(endpoint);
  await new Promise<void>((resolve, reject) => {
    socket.onopen = () => resolve();
    socket.onerror = () => reject(new Error("Browser WebSocket failed to open"));
  });
  socket.onopen = null;
  socket.onerror = null;
  const transport = webSocketTransport(socket, { codec });
  const connection = createActorConnection<BrowserServices>(transport, {
    actors: { ui: { describe: () => "browser UI" } },
  });
  await connection.ready;
  return { socket, connection };
}

/** Runs in a real browser without Node/Deno globals or external module imports. */
export async function runBrowserConformance(
  endpoint: string,
  workerUrl: string,
): Promise<string[]> {
  const passed: string[] = [];
  const first = await open(endpoint);
  const second = await open(endpoint);
  try {
    const json = await open(`${endpoint}/json`, jsonWireCodec);
    try {
      const api = json.connection.actor("service");
      assert(await api.empty() === undefined, "Plain JSON void RPC must stay connected");
      assert(
        await api.invokeEmpty(() => {}) === undefined,
        "Plain JSON callback void results must be accepted",
      );
      assert(await api.nothing() === null, "Explicit null must remain distinct");
      assert(await api.increment() === 1, "JSON void replies must preserve future calls");
      passed.push("plain JSON void RPC and callback replies");
    } finally {
      json.connection.close();
      await json.connection.completion;
    }
    const actor = first.connection.actor("service");
    const bytes = new Uint8Array([9, 0, 255, 1, 9]).subarray(1, 4);
    const graph: {
      self?: unknown;
      bytes: Uint8Array;
      alias?: Uint8Array;
      map?: Map<unknown, unknown>;
    } = { bytes };
    graph.self = graph;
    Object.defineProperty(graph, "__proto__", { value: { safe: true }, enumerable: true });
    graph.alias = bytes;
    graph.map = new Map([[graph, bytes]]);
    const copy = await actor.graph(graph) as typeof graph;
    assert(
      Object.getPrototypeOf(copy) === Object.prototype && Object.hasOwn(copy, "__proto__"),
      "Actor payload and wire codecs must retain prototype-named data without invoking setters",
    );
    assert(
      copy.self === copy && copy.alias === copy.bytes && copy.map?.get(copy) === copy.bytes,
      "Production graph codec must retain cycles and shared identities over binary WebSocket RPC",
    );
    assert(
      copy.bytes instanceof Uint8Array && String(copy.bytes) === "0,255,1",
      "Graph bytes remain native binary",
    );
    passed.push("cyclic binary graph RPC");
    const reversed = await actor.apply((value) => value.slice().reverse(), bytes);
    assert(
      reversed instanceof Uint8Array && String(reversed) === "1,255,0",
      "Browser callback must carry binary through its own transport",
    );
    assert(
      await actor.askUi() === "browser UI",
      "Server must call only the explicitly advertised browser UI Actor",
    );
    passed.push("bidirectional browser capabilities and callbacks");
    const stream = actor.numbers();
    assert(await actor.produced() === 0, "Stream creation must be lazy");
    const iterator = stream[Symbol.asyncIterator]();
    assert((await iterator.next()).value === 0, "First stream value");
    assert(await actor.produced() === 1, "Slow browser consumer must not prefetch without credit");
    await iterator.return?.();
    await actor.released();
    assert(await actor.produced() === 1, "Stream release must stop production");
    passed.push("pull-driven streaming and release");
    const controller = new AbortController();
    const started = Promise.withResolvers<void>();
    const waiting = actor.cancel(controller.signal, () => {
      started.resolve();
    });
    await started.promise;
    controller.abort("browser cancellation");
    assert(await waiting, "AbortSignal must cross the browser connection");
    passed.push("cooperative cancellation");
    let failed = false;
    try {
      await actor.fail();
    } catch (error) {
      failed = error instanceof Error && error.name === "RangeError";
    }
    assert(failed, "Typed remote errors must preserve the error name");
    passed.push("remote errors");
    assert(
      await actor.increment() === 1 && await second.connection.actor("service").increment() === 1,
      "Each connection owns its Actor state",
    );
    const active = Promise.withResolvers<void>();
    const unfinished = actor.wait(() => {
      active.resolve();
    });
    let rejected = false;
    const settled = unfinished.catch(() => {
      rejected = true;
    });
    await active.promise;
    first.socket.close();
    await settled;
    await first.connection.completion;
    assert(rejected, "Socket closure must reject an already dispatched call");
    assert(
      await second.connection.actor("service").increment() === 2,
      "Closing one connection must preserve the other",
    );
    passed.push("disconnect cleanup and connection isolation");
    const pair = new MessageChannel();
    const localServer = createActorConnection(fromMessagePort(pair.port1), {
      actors: { math: { add: (a: number, b: number) => a + b } },
    });
    const localClient = createActorConnection<{ math: { add(a: number, b: number): number } }>(
      fromMessagePort(pair.port2),
    );
    try {
      await localClient.ready;
      assert(
        await localClient.actor("math").add(20, 22) === 42,
        "Transferred MessagePort Actor addressing",
      );
    } finally {
      localClient.close();
      await Promise.all([localClient.completion, localServer.completion]);
    }
    passed.push("native MessageChannel transport");
    const worker = new Worker(workerUrl, { type: "module" });
    const workerActor = await spawn<typeof WorkerModule.rpc>(worker);
    try {
      assert(
        String(await workerActor.echo(bytes)) === "0,255,1",
        "Browser Worker typed binary RPC",
      );
      const values: number[] = [];
      for await (const value of workerActor.numbers()) values.push(value);
      assert(
        String(values) === "1,2",
        "Browser Worker streaming must use the same credit protocol",
      );
    } finally {
      await workerActor.dispose();
    }
    passed.push("native browser Worker actors");
    for (const mode of ["send", "callback result", "worker result"]) {
      const source = new Worker(workerUrl, { type: "module" });
      const actor = await spawn<typeof WorkerModule.rpc>(source);
      try {
        const started = Promise.withResolvers<void>();
        const pending = actor.pending(() => {
          started.resolve();
        });
        const settled = pending.then(() => false, () => true);
        await started.promise;
        const failing = mode === "send"
          ? actor.clone(Symbol("not cloneable"))
          : mode === "callback result"
          ? actor.invoke(() => Symbol("not cloneable"))
          : actor.uncloneableResult();
        assert(await failing.then(() => false, () => true), `${mode} must reject`);
        assert(await settled, `${mode} must reject other pending calls`);
        assert(
          await actor.clone("future").then(() => false, () => true),
          `${mode} must reject future calls`,
        );
        passed.push(`native Worker ${mode} failure settles its owner`);
      } finally {
        await actor.dispose();
      }
    }

    return passed;
  } finally {
    first.connection.close();
    second.connection.close();
    await Promise.all([first.connection.completion, second.connection.completion]);
  }
}
