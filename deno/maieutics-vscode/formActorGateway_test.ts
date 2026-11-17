/// <reference lib="deno.ns" />
/// <reference lib="es2024.promise" />
/// <reference lib="dom" />
import { assert, assertEquals, assertRejects } from "@std/assert";
import { FormActorGateway } from "./formActorGateway.ts";
import { FormActorHost } from "../maieutics-output-client/formActorHost.ts";
import { FormActorClient } from "../maieutics-output-client/formActorClient.ts";
import { Server } from "node:http";

async function budget<T>(task: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      task,
      new Promise<never>((_, reject) => {
        timer = setTimeout(
          () => reject(new Error("Binary Actor protocol event did not arrive")),
          5000,
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
function inbox<T>() {
  const values: T[] = [], waiters: Array<(value: T) => void> = [];
  return {
    push(value: T) {
      const waiter = waiters.shift();
      if (waiter) waiter(value);
      else values.push(value);
    },
    next(): Promise<T> {
      return values.length
        ? Promise.resolve(values.shift() as T)
        : new Promise((resolve) => waiters.push(resolve));
    },
  };
}
async function denied(endpoint: string): Promise<void> {
  const socket = new WebSocket(endpoint);
  const closed = new Promise<void>((resolve) =>
    socket.addEventListener("close", () => resolve(), { once: true })
  );
  const verdict = await budget(
    new Promise<string>((resolve) => {
      socket.addEventListener("open", () => resolve("opened"), { once: true });
      socket.addEventListener("error", () => resolve("denied"), { once: true });
    }),
  );
  if (verdict === "opened") socket.close();
  await budget(closed);
  assertEquals(verdict, "denied", "A guessed, consumed or revoked Actor capability cannot connect");
}

Deno.test("binary Actor gateway authenticates a one-use capability and revokes its physical connection", async () => {
  const lifetime = new AbortController();
  const accepted = Promise.withResolvers<WebSocket>();
  const gateway = new FormActorGateway({ log: () => {} });
  try {
    const endpoint = await gateway.listen(lifetime.signal, accepted.resolve);
    const wrong = new URL(endpoint);
    wrong.searchParams.set("actorToken", "0".repeat(64));
    await denied(wrong.href);
    const socket = new WebSocket(endpoint);
    const closed = new Promise<void>((resolve) =>
      socket.addEventListener("close", () => resolve(), { once: true })
    );
    await budget(
      new Promise<void>((resolve, reject) => {
        socket.addEventListener("open", () => resolve(), { once: true });
        socket.addEventListener("error", reject, { once: true });
      }),
    );
    const server = await budget(accepted.promise);
    const serverClosed = new Promise<void>((resolve) =>
      server.addEventListener("close", () => resolve(), { once: true })
    );
    await denied(endpoint);
    lifetime.abort();
    await budget(Promise.all([closed, serverClosed]));
    assertEquals(server.readyState, 3, "Revocation must close the accepted server endpoint");
    await denied(endpoint);
  } finally {
    lifetime.abort();
    await gateway.dispose();
  }
});

Deno.test("Actor graphs cross actual WebSockets while the notebook bootstrap is forced through JSON", async () => {
  const owner = {};
  const updates = inbox<unknown>(), events = inbox<Record<string, unknown>>();
  const control: Record<string, unknown>[] = [];
  const frames: unknown[] = [];
  const gateway = new FormActorGateway({ log: () => {} });
  const json = (message: unknown) => JSON.parse(JSON.stringify(message));
  const host = new FormActorHost({
    session: () => "session",
    visible: () => true,
    listen: async (_owner, signal, accept) =>
      await gateway.listen(signal, (socket) => {
        const send = socket.send.bind(socket);
        socket.send = (message) => {
          frames.push(message);
          send(message);
        };
        socket.addEventListener("message", (event) => frames.push(event.data));
        accept(socket);
      }),
    handle: (_owner, message) => {
      const value = message as Record<string, unknown>;
      events.push(value);
      if (value.type === "mount") {
        const state: Record<string, unknown> = { bytes: new Uint8Array([0, 255, 128]) };
        state.self = state;
        state.alias = state.bytes;
        state.map = new Map([[state, state.bytes]]);
        host.post(owner, { source: "maieutics-form", type: "state", modelId: "model", state });
      }
      return Promise.resolve();
    },
    release: () => Promise.resolve(),
    post: (_owner, message) => {
      control.push(json(message));
      queueMicrotask(() => client.receive(json(message)));
      return Promise.resolve(true);
    },
    log: () => {},
  });
  const client = new FormActorClient({
    postMessage: (message) => {
      control.push(json(message));
      queueMicrotask(() => host.receive(owner, json(message)));
      return Promise.resolve(true);
    },
    receive: (message) => {
      updates.push(message);
      return Promise.resolve();
    },
    log: () => {},
  });
  try {
    client.send({ source: "maieutics-form", type: "mount", modelId: "model", viewId: "view" });
    assertEquals((await budget(events.next())).type, "mount");
    const update = await budget(updates.next()) as { state: Record<string, unknown> };
    assert(update.state.self === update.state);
    assert(update.state.alias === update.state.bytes);
    assertEquals(update.state.bytes, new Uint8Array([0, 255, 128]));
    assert((update.state.map as Map<unknown, unknown>).get(update.state) === update.state.bytes);
    const values: Record<string, unknown> = { bytes: new Uint8Array([7, 0, 255]) };
    values.self = values;
    client.send({
      source: "maieutics-form",
      type: "event",
      modelId: "model",
      viewId: "view",
      name: "submit",
      payload: { values },
    });
    const action = await budget(events.next()) as { payload: { values: Record<string, unknown> } };
    assert(action.payload.values.self === action.payload.values);
    assertEquals(action.payload.values.bytes, new Uint8Array([7, 0, 255]));
    assert(
      frames.length > 0 &&
        frames.every((value) => value instanceof Uint8Array || value instanceof ArrayBuffer),
      "Every wire data frame must remain binary",
    );
    assert(
      control.every((message) => message.type !== "frame"),
      "Actor data must never pass through notebook JSON messaging",
    );
  } finally {
    const results = await Promise.allSettled([client.dispose(), host.dispose(), gateway.dispose()]);
    assertEquals(
      results.filter((r) => r.status === "rejected"),
      [],
      "All binary channel owners must finish cleanup",
    );
  }
});

Deno.test("gateway disposal cancels pending external-URI resolution and joins the listener", async () => {
  const started = Promise.withResolvers<void>(), external = Promise.withResolvers<string>();
  const lifetime = new AbortController();
  const gateway = new FormActorGateway({
    externalUri: () => {
      started.resolve();
      return external.promise;
    },
    log: () => {},
  });
  const offered = gateway.listen(lifetime.signal, () => {});
  void offered.catch(() => {});
  try {
    await budget(started.promise);
    const disposing = gateway.dispose();
    await assertRejects(() => budget(offered));
    await budget(disposing);
  } finally {
    lifetime.abort();
    external.resolve("http://127.0.0.1/");
    await gateway.dispose();
  }
});

Deno.test("binary gateway preserves external routing prefixes and query parameters", async () => {
  const lifetime = new AbortController();
  const gateway = new FormActorGateway({
    externalUri: (address) => {
      const url = new URL(address);
      url.pathname = "/forwarded/";
      url.searchParams.set("tunnel", "test");
      return Promise.resolve(url.href);
    },
    log: () => {},
  });
  try {
    const endpoint = await gateway.listen(lifetime.signal, () => {});
    const url = new URL(endpoint);
    assert(url.pathname.startsWith("/forwarded/actor/"));
    assertEquals(url.searchParams.get("tunnel"), "test");
    const socket = new WebSocket(endpoint);
    const closed = new Promise<void>((resolve) =>
      socket.addEventListener("close", () => resolve(), { once: true })
    );
    await budget(
      new Promise<void>((resolve, reject) => {
        socket.onopen = () => resolve();
        socket.onerror = reject;
      }),
    );
    lifetime.abort();
    await budget(closed);
  } finally {
    lifetime.abort();
    await gateway.dispose();
  }
});

Deno.test("throwing admission and log hooks still close the binary peer and report their failures", async () => {
  const lifetime = new AbortController();
  const admission = new Error("admission failed"), logging = new Error("log failed");
  const gateway = new FormActorGateway({
    log: () => {
      throw logging;
    },
  });
  try {
    const endpoint = await gateway.listen(lifetime.signal, () => {
      throw admission;
    });
    const socket = new WebSocket(endpoint);
    socket.onerror = () => {};
    await budget(
      new Promise<void>((resolve) =>
        socket.addEventListener("close", () => resolve(), { once: true })
      ),
    );
    assertEquals(socket.readyState, 3);
    const error = await assertRejects(() => gateway.dispose(), AggregateError);
    assertEquals(error.errors, [admission, logging]);
  } finally {
    lifetime.abort();
    await gateway.dispose().catch(() => {});
  }
});

Deno.test("failed endpoint publication closes the listening socket before reporting startup failure", async () => {
  const lifetime = new AbortController();
  const failed = new Error("port forwarding failed");
  let address = "";
  const gateway = new FormActorGateway({
    externalUri: (url) => {
      address = url;
      return Promise.reject(failed);
    },
    log: () => {},
  });
  try {
    const error = await assertRejects(() => gateway.listen(lifetime.signal, () => {}));
    assert(error === failed, "Startup must preserve its original failure");
    await assertRejects(() => fetch(address), TypeError);
  } finally {
    lifetime.abort();
    await gateway.dispose();
  }
});

Deno.test("gateway server errors are owned between listening and the startup continuation", async () => {
  const failure = new Error("listener handoff sentinel");
  const injected = Promise.withResolvers<unknown>();
  const original = Server.prototype.listen;
  Server.prototype.listen = function (this: Server, ...args: unknown[]) {
    this.once("listening", () => {
      queueMicrotask(() => {
        try {
          this.emit("error", failure);
          injected.resolve(undefined);
        } catch (error) {
          injected.resolve(error);
        }
      });
    });
    return Reflect.apply(original, this, args);
  } as typeof original;
  const lifetime = new AbortController();
  const gateway = new FormActorGateway({ log: () => {} });
  const offered = gateway.listen(lifetime.signal, () => {});
  void offered.catch(() => {});
  try {
    assertEquals(await budget(injected.promise), undefined, "The error event must retain an owner");
    await assertRejects(() => budget(offered));
    const cleanup = await assertRejects(() => gateway.dispose(), AggregateError);
    assertEquals(cleanup.errors, [failure]);
  } finally {
    Server.prototype.listen = original;
    lifetime.abort();
    await offered.catch(() => {});
    await gateway.dispose().catch(() => {});
  }
});

Deno.test("gateway bind errors preserve the startup cause and finish cleanup before rejecting", async () => {
  const failure = new Error("bind failure sentinel");
  const original = Server.prototype.listen;
  Server.prototype.listen = function (this: Server) {
    queueMicrotask(() => this.emit("error", failure));
    return this;
  } as typeof original;
  const lifetime = new AbortController();
  const gateway = new FormActorGateway({ log: () => {} });
  try {
    assertEquals(await assertRejects(() => gateway.listen(lifetime.signal, () => {})), failure);
    await gateway.dispose();
  } finally {
    Server.prototype.listen = original;
    lifetime.abort();
    await gateway.dispose();
  }
});
