import { assert, assertEquals, assertRejects } from "@std/assert";
import { FormActorClient } from "./formActorClient.ts";
import { FormActorHost } from "./formActorHost.ts";
import {
  FORM_ACTOR_FRAME_BYTES,
  formFrameSize,
  FormPosts,
  frame,
  record,
} from "./formActorProtocol.ts";

class Inbox<T> {
  readonly #values: T[] = [];
  readonly #waiters: Array<(value: T) => void> = [];
  push(value: T): void {
    const waiter = this.#waiters.shift();
    if (waiter) waiter(value);
    else this.#values.push(value);
  }
  next(): Promise<T> {
    if (this.#values.length) return Promise.resolve(this.#values.shift() as T);
    return new Promise((resolve) => this.#waiters.push(resolve));
  }
}
async function budget<T>(operation: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      operation,
      new Promise<never>((_, reject) => {
        timer = setTimeout(
          () => reject(new Error("Form Actor protocol event did not arrive")),
          3000,
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
interface Owner {
  session: string | undefined;
  visible: boolean;
}
function fixture() {
  const clients = new Map<Owner, FormActorClient>();
  const wire: Array<{ owner: Owner; direction: string; value: unknown }> = [];
  const updates = new Map<Owner, Inbox<Record<string, unknown>>>();
  const events = new Inbox<
    { owner: Owner; session: string | undefined; message: Record<string, unknown> }
  >();
  const releases = new Inbox<Owner>();
  const logs: string[] = [];
  let blockMount: ((owner: Owner) => Promise<void>) | undefined;
  let blockRelease: ((owner: Owner) => Promise<void>) | undefined;
  const host = new FormActorHost<Owner>({
    session: (owner) => owner.session,
    visible: (owner) => owner.visible,
    handle: async (owner, message) => {
      assert(record(message));
      events.push({ owner, session: owner.session, message });
      if (message.type === "mount") {
        await blockMount?.(owner);
        host.post(owner, {
          source: "maieutics-form",
          type: "state",
          modelId: message.modelId,
          state: { title: owner.session, fields: [], values: {} },
        });
      }
    },
    release: async (owner) => {
      releases.push(owner);
      await blockRelease?.(owner);
    },
    post: (owner, message) => {
      wire.push({ owner, direction: "down", value: structuredClone(message) });
      queueMicrotask(() => clients.get(owner)?.receive(structuredClone(message)));
      return Promise.resolve(true);
    },
    log: (message) => logs.push(message),
  });
  const add = (session = "session-a") => {
    const owner: Owner = { session, visible: true };
    const inbox = new Inbox<Record<string, unknown>>();
    updates.set(owner, inbox);
    const client = new FormActorClient({
      postMessage: (message) => {
        wire.push({ owner, direction: "up", value: structuredClone(message) });
        queueMicrotask(() => host.receive(owner, structuredClone(message)));
        return Promise.resolve(true);
      },
      receive: (message) => {
        assert(record(message));
        inbox.push(message);
        return Promise.resolve();
      },
      log: (message) => logs.push(message),
    });
    clients.set(owner, client);
    return { owner, client, updates: inbox };
  };
  return {
    host,
    add,
    events,
    releases,
    wire,
    logs,
    blockMount(handler: typeof blockMount) {
      blockMount = handler;
    },
    blockRelease(handler: typeof blockRelease) {
      blockRelease = handler;
    },
    async dispose() {
      const results = await Promise.allSettled([
        ...[...clients.values()].map((client) => client.dispose()),
        host.dispose(),
      ]);
      const errors = results.filter((r) => r.status === "rejected");
      assertEquals(errors, [], "Every browser/host generation must finish its owned cleanup");
    },
  };
}
const mount = (client: FormActorClient, modelId = "model", viewId = "view") =>
  client.send({ source: "maieutics-form", type: "mount", modelId, viewId });

Deno.test("form Actor construction and terminal disposal start no I/O", async () => {
  const f = fixture();
  f.add();
  assertEquals(f.wire, []);
  await f.dispose();
  assertEquals(f.wire, []);
});

Deno.test("form Actors use correlated RPC and isolate overlapping identities by host editor", async () => {
  const f = fixture();
  try {
    const a = f.add(), b = f.add("session-b");
    mount(a.client);
    mount(b.client);
    assertEquals((await budget(a.updates.next())).state, {
      title: "session-a",
      fields: [],
      values: {},
    });
    assertEquals((await budget(b.updates.next())).state, {
      title: "session-b",
      fields: [],
      values: {},
    });
    const first = await budget(f.events.next()), second = await budget(f.events.next());
    assertEquals(new Set([first.session, second.session]), new Set(["session-a", "session-b"]));
    a.client.send({
      source: "maieutics-form",
      type: "event",
      modelId: "model",
      viewId: "view",
      name: "submit",
      payload: { values: { text: "draft", count: 2.5 } },
    });
    const action = await budget(f.events.next());
    assertEquals(action.owner, a.owner);
    assertEquals(action.message.payload, { values: { text: "draft", count: 2.5 } });
    assert(f.wire.every(({ value }) => record(value) && value.source === "maieutics-form-actor"));
    assert(
      f.wire.some(({ value }) =>
        record(value) && record(value.value) && value.value.type === "actor:hello"
      ),
    );
  } finally {
    await f.dispose();
  }
});

Deno.test("last unmount joins its attachment and opens a fresh generation for a new form", async () => {
  const f = fixture();
  try {
    const a = f.add();
    mount(a.client);
    await budget(a.updates.next());
    await budget(f.events.next());
    a.client.send({ source: "maieutics-form", type: "unmount", modelId: "model", viewId: "view" });
    assertEquals((await budget(f.events.next())).message.type, "unmount");
    assertEquals(await budget(f.releases.next()), a.owner);
    mount(a.client, "next", "next-view");
    assertEquals((await budget(a.updates.next())).modelId, "next");
    assertEquals((await budget(f.events.next())).message.modelId, "next");
    const offers = f.wire.filter(({ direction, value }) =>
      direction === "down" && record(value) && value.type === "offer"
    ).map(({ value }) => value as Record<string, unknown>);
    assertEquals(offers.map((v) => v.epoch), [1, 2]);
    assert(offers[0].connectionId !== offers[1].connectionId);
  } finally {
    await f.dispose();
  }
});

for (const nextModel of ["model", "replacement-model"]) {
  Deno.test(`a retiring view cannot erase an immediate ${nextModel} remount at the same output id`, async () => {
    const f = fixture();
    try {
      const a = f.add();
      mount(a.client);
      await budget(f.events.next());
      await budget(a.updates.next());
      a.client.send({
        source: "maieutics-form",
        type: "unmount",
        modelId: "model",
        viewId: "view",
      });
      mount(a.client, nextModel);
      assertEquals((await budget(f.events.next())).message.type, "unmount");
      assertEquals((await budget(f.events.next())).message.type, "mount");
      assertEquals((await budget(a.updates.next())).modelId, nextModel);
      a.client.send({
        source: "maieutics-form",
        type: "event",
        modelId: nextModel,
        viewId: "view",
        name: "cancel",
      });
      assertEquals(
        (await budget(f.events.next())).message.name,
        "cancel",
        "The replacement attachment must keep its action capability",
      );
    } finally {
      await f.dispose();
    }
  });
}

Deno.test("hidden form Actors release immediately and restore mounts without replaying actions", async () => {
  const f = fixture();
  try {
    const a = f.add(), b = f.add("session-b");
    mount(a.client);
    mount(b.client);
    await budget(a.updates.next());
    await budget(b.updates.next());
    await budget(f.events.next());
    await budget(f.events.next());
    a.client.send({
      source: "maieutics-form",
      type: "event",
      modelId: "model",
      viewId: "view",
      name: "cancel",
    });
    assertEquals((await budget(f.events.next())).message.name, "cancel");
    a.owner.visible = false;
    f.host.updateVisible([b.owner]);
    assertEquals(await budget(f.releases.next()), a.owner);
    assertEquals((await budget(a.updates.next())).type, "closed");
    f.host.post(b.owner, {
      source: "maieutics-form",
      type: "state",
      modelId: "model",
      state: { title: "still live" },
    });
    assertEquals((await budget(b.updates.next())).state, { title: "still live" });
    a.owner.visible = true;
    f.host.updateVisible([a.owner, b.owner]);
    assertEquals((await budget(f.events.next())).message.type, "mount");
    assertEquals((await budget(a.updates.next())).type, "state");
    a.client.send({
      source: "maieutics-form",
      type: "event",
      modelId: "model",
      viewId: "view",
      name: "cancel",
    });
    assertEquals((await budget(f.events.next())).message.name, "cancel");
  } finally {
    await f.dispose();
  }
});

Deno.test("session repin revokes old capabilities before any stale Actor frame can dispatch", async () => {
  const f = fixture();
  try {
    const a = f.add();
    mount(a.client);
    await budget(a.updates.next());
    await budget(f.events.next());
    const old = f.wire.filter(({ direction, value }) =>
      direction === "up" && record(value) && value.type === "frame"
    );
    a.owner.session = "session-b";
    await f.host.releaseWhere((owner, session) => owner === a.owner && session !== owner.session);
    assertEquals((await budget(a.updates.next())).type, "closed");
    assertEquals(a.client.hasView("view"), false);
    for (const message of old) f.host.receive(a.owner, message.value);
    mount(a.client, "new-model", "new-view");
    const next = await budget(f.events.next());
    assertEquals(next.session, "session-b");
    assertEquals(next.message.modelId, "new-model");
    assertEquals((await budget(a.updates.next())).modelId, "new-model");
  } finally {
    await f.dispose();
  }
});

Deno.test("early hidden mounts await host visibility without sending a form RPC", async () => {
  const f = fixture();
  try {
    const a = f.add();
    a.owner.visible = false;
    mount(a.client);
    // The next request's post completion is ordered after the queued host admission check.
    await Promise.resolve();
    assertEquals(f.wire.length, 1);
    a.owner.visible = true;
    f.host.updateVisible([a.owner]);
    assertEquals((await budget(f.events.next())).message.type, "mount");
    await budget(a.updates.next());
  } finally {
    await f.dispose();
  }
});

Deno.test("retired comm cleanup cannot block mounting a replacement Actor generation", async () => {
  const f = fixture();
  const release = Promise.withResolvers<void>();
  try {
    const a = f.add();
    mount(a.client);
    await budget(f.events.next());
    await budget(a.updates.next());
    f.blockRelease(() => release.promise);
    a.owner.visible = false;
    f.host.updateVisible([]);
    await budget(f.releases.next());
    await budget(a.updates.next());
    a.owner.visible = true;
    f.host.updateVisible([a.owner]);
    assertEquals((await budget(f.events.next())).message.type, "mount");
    assertEquals((await budget(a.updates.next())).type, "state");
  } finally {
    release.resolve();
    await f.dispose();
  }
});

Deno.test("untrusted legacy messages and unknown Actor envelopes cannot open a form capability", async () => {
  const f = fixture();
  try {
    const a = f.add();
    f.host.receive(a.owner, {
      source: "maieutics-form",
      type: "mount",
      modelId: "other",
      viewId: "view",
      sessionId: "borrowed",
    });
    f.host.receive(
      a.owner,
      frame("frame", { epoch: 1, connectionId: "forged", value: { type: "actor:hello" } }),
    );
    f.host.receive(a.owner, { source: "maieutics-form-actor", version: 99, type: "request" });
    assertEquals(f.wire.length, 0);
    a.owner.session = undefined;
    mount(a.client);
    assertEquals((await budget(a.updates.next())).type, "closed");
    assertEquals(
      a.client.hasView("view"),
      false,
      "Unpinned attachments cannot gain later authority",
    );
    assert(f.wire.every(({ value }) => !record(value) || value.type !== "offer"));
  } finally {
    await f.dispose();
  }
});

Deno.test("an asynchronous delivery rejection is owned and reported by browser disposal", async () => {
  const logs: string[] = [];
  const client = new FormActorClient({
    postMessage: () => Promise.reject(new Error("delivery failed")),
    receive: () => Promise.resolve(),
    log: (message) => logs.push(message),
  });
  mount(client);
  await assertRejects(() => client.dispose(), AggregateError);
  assert(logs.some((v) => v.includes("request failed")));
});

Deno.test("repinning a hidden editor invalidates dormant views before restoring visibility", async () => {
  const f = fixture();
  try {
    const a = f.add();
    mount(a.client);
    await budget(f.events.next());
    await budget(a.updates.next());
    a.owner.visible = false;
    f.host.updateVisible([]);
    await budget(f.releases.next());
    await budget(a.updates.next());
    a.owner.session = "new-session";
    a.owner.visible = true;
    f.host.updateVisible([a.owner]);
    await Promise.resolve();
    assertEquals(a.client.hasView("view"), false);
    mount(a.client, "fresh-model", "fresh-view");
    const event = await budget(f.events.next());
    assertEquals(event.message.modelId, "fresh-model");
    assertEquals(event.session, "new-session");
    await budget(a.updates.next());
  } finally {
    await f.dispose();
  }
});

Deno.test("disposal cancels an in-flight mount before joining Actor dispatch", async () => {
  const f = fixture();
  const pending = Promise.withResolvers<void>();
  const releasing = Promise.withResolvers<void>();
  try {
    const a = f.add();
    f.blockMount(() => pending.promise);
    f.blockRelease(() => {
      pending.resolve();
      releasing.resolve();
      return Promise.resolve();
    });
    mount(a.client);
    await budget(f.events.next());
    const disposing = f.host.dispose();
    await budget(releasing.promise);
    await budget(disposing);
    assertEquals((await budget(a.updates.next())).type, "closed");
  } finally {
    pending.resolve();
    await f.dispose();
  }
});

Deno.test("old downlinks cannot revive views after a visibility reconnect", async () => {
  const f = fixture();
  try {
    const a = f.add();
    mount(a.client);
    await budget(f.events.next());
    await budget(a.updates.next());
    const oldFrames = f.wire.filter(({ direction }) => direction === "down");
    a.owner.visible = false;
    f.host.updateVisible([]);
    await budget(f.releases.next());
    await budget(a.updates.next());
    a.owner.visible = true;
    f.host.updateVisible([a.owner]);
    await budget(f.events.next());
    await budget(a.updates.next());
    for (const { value } of oldFrames) a.client.receive(value);
    f.host.post(a.owner, {
      source: "maieutics-form",
      type: "state",
      modelId: "model",
      state: { title: "new generation" },
    });
    assertEquals((await budget(a.updates.next())).state, { title: "new generation" });
  } finally {
    await f.dispose();
  }
});

Deno.test("all 128 admitted form attachments can mount through one Actor connection", async () => {
  const f = fixture();
  try {
    const a = f.add();
    for (let i = 0; i < 128; i++) mount(a.client, `model-${i}`, `view-${i}`);
    for (let i = 0; i < 128; i++) {
      assertEquals((await budget(f.events.next())).message.viewId, `view-${i}`);
      assertEquals((await budget(a.updates.next())).modelId, `model-${i}`);
    }
    assertEquals(
      f.logs,
      [],
      "An admitted attachment burst must not exhaust control-frame overhead",
    );
  } finally {
    await f.dispose();
  }
});

Deno.test("host cleanup errors reach disposal even when no Actor handshake completed", async () => {
  const sent: Record<string, unknown>[] = [];
  const owner = {};
  const failure = new Error("comm cleanup failed");
  const host = new FormActorHost({
    session: () => "session",
    visible: () => true,
    handle: () => Promise.resolve(),
    release: () => {
      throw failure;
    },
    post: (_owner, message) => {
      assert(record(message));
      sent.push(message);
    },
    log: () => {},
  });
  host.receive(owner, frame("request"));
  host.receive(
    owner,
    frame("release", { epoch: sent[0].epoch, connectionId: sent[0].connectionId }),
  );
  const error = await assertRejects(() => host.dispose(), AggregateError);
  assertEquals(error.errors, [failure]);
  assertEquals(sent.at(-1)?.type, "closed");
});

Deno.test("host disposal waits for its asynchronous post completion", async () => {
  const posted = Promise.withResolvers<boolean>();
  const released = Promise.withResolvers<void>();
  const host = new FormActorHost({
    session: () => "session",
    visible: () => true,
    handle: () => Promise.resolve(),
    release: () => {
      released.resolve();
      return Promise.resolve();
    },
    post: () => posted.promise,
    log: () => {},
  });
  host.receive({}, frame("request"));
  let settled = false;
  const disposing = host.dispose().then(() => {
    settled = true;
  });
  await budget(released.promise);
  assertEquals(settled, false, "Actor disposal cannot leave a postMessage promise unobserved");
  posted.resolve(true);
  await budget(disposing);
});

Deno.test("native form message admission bounds binary, strings and nested value graphs", async () => {
  assert(formFrameSize(new Uint8Array(100)) >= 100);
  assertEquals(formFrameSize(new Uint8Array(FORM_ACTOR_FRAME_BYTES + 1)), Number.MAX_SAFE_INTEGER);
  assertEquals(formFrameSize("x".repeat(FORM_ACTOR_FRAME_BYTES)), Number.MAX_SAFE_INTEGER);
  assertEquals(
    formFrameSize(new Map([["large", "x".repeat(FORM_ACTOR_FRAME_BYTES)]])),
    Number.MAX_SAFE_INTEGER,
  );
  let deep: unknown = null;
  for (let i = 0; i < 65; i++) deep = { deep };
  assertEquals(formFrameSize(deep), Number.MAX_SAFE_INTEGER);
  const posts = new FormPosts();
  let posted = false;
  let failure: unknown;
  posts.send({ large: "x".repeat(FORM_ACTOR_FRAME_BYTES) }, () => {
    posted = true;
  }, (error) => failure = error);
  assertEquals(
    posted,
    false,
    "An oversized form frame must fail before crossing the host boundary",
  );
  assert(record(failure) && failure.code === "capacity");
  await assertRejects(() => posts.join(), AggregateError);
});

Deno.test("the post byte budget fails explicitly and joins already accepted deliveries", async () => {
  const posts = new FormPosts();
  const delivered = Promise.withResolvers<void>();
  let accepted = 0;
  const failures: unknown[] = [];
  for (let i = 0; i < 8; i++) {
    posts.send("x".repeat(300_000), () => {
      accepted++;
      return delivered.promise;
    }, (error) => failures.push(error));
  }
  assertEquals(accepted, 6);
  assertEquals(failures.length, 2);
  let joined = false;
  const joining = assertRejects(() => posts.join(), AggregateError).then(() => {
    joined = true;
  });
  assertEquals(joined, false);
  delivered.resolve();
  await joining;
});

Deno.test("editor admission counts active and retiring offers and frees the exact released slot", async () => {
  const messages: Array<{ owner: object; value: Record<string, unknown> }> = [];
  const owners = Array.from({ length: 129 }, () => ({}));
  const host = new FormActorHost({
    session: () => "session",
    visible: () => true,
    handle: () => Promise.resolve(),
    release: () => Promise.resolve(),
    post: (owner, value) => {
      assert(record(value));
      messages.push({ owner, value });
    },
    log: () => {},
  });
  try {
    for (const owner of owners) host.receive(owner, frame("request"));
    assertEquals(messages.filter(({ value }) => value.type === "offer").length, 128);
    assertEquals(messages.at(-1)?.value.code, "form_scope_limit");
    await host.releaseWhere((owner) => owner === owners[0]);
    host.receive(owners[128], frame("request"));
    assertEquals(messages.at(-1)?.owner, owners[128]);
    assertEquals(messages.at(-1)?.value.type, "offer");
  } finally {
    await host.dispose();
  }
});

Deno.test("a malformed initial Actor frame releases its offered editor scope", async () => {
  const messages: Record<string, unknown>[] = [];
  const owner = {};
  let released = 0;
  const host = new FormActorHost({
    session: () => "session",
    visible: () => true,
    handle: () => Promise.resolve(),
    release: () => {
      released++;
      return Promise.resolve();
    },
    post: (_owner, message) => {
      assert(record(message));
      messages.push(message);
    },
    log: () => {},
  });
  host.receive(owner, frame("request"));
  host.receive(
    owner,
    frame("frame", {
      epoch: messages[0].epoch,
      connectionId: messages[0].connectionId,
      value: null,
    }),
  );
  await host.dispose();
  assertEquals(released, 1);
  assertEquals(messages.at(-1)?.code, "protocol");
});
