import { assert, assertEquals, assertRejects, assertThrows } from "@std/assert";
import { PayloadCodecRegistry } from "./core/codec.ts";
import { waitWithSignal } from "./core/lifetime.ts";
import { spawn, spawnProcess } from "./spawn.ts";
import { createMux } from "./core/transport.ts";
import { PROTOCOL_VERSION } from "./core/protocol.ts";
import { kill as processKill } from "node:process";
import { throwingCleanupCodec } from "./test_fixtures/lifecycle/codec.ts";
import type { rpc } from "./test_fixtures/lifecycle/worker.ts";

function worker(cleanup = false) {
  const url = new URL("./test_fixtures/lifecycle/worker.ts", import.meta.url);
  if (cleanup) url.searchParams.set("cleanup", "1");
  const source = new Worker(url.href, { type: "module" });
  const terminate = source.terminate.bind(source);
  let terminated = false;
  source.terminate = () => {
    terminated = true;
    terminate();
  };
  return {
    source,
    get terminated() {
      return terminated;
    },
  };
}

for (const cleanup of [false, true]) {
  Deno.test(`Worker send failure kills owner and settles calls (throwing cleanup: ${cleanup})`, async () => {
    const owned = worker(cleanup);
    const started = Promise.withResolvers<void>();
    let died = false;
    try {
      const actor = await spawn<typeof rpc>(owned.source, {
        codecs: cleanup ? [throwingCleanupCodec] : [],
        onDeath: () => {
          died = true;
        },
      });
      const pending = actor.pending(() => {
        started.resolve();
      });
      void pending.catch(() => {});
      await waitWithSignal(started.promise, AbortSignal.timeout(5000));
      await assertRejects(() => actor.echo(Symbol("not cloneable")));
      assert(died, "A failed Worker send must synchronously kill its Actor owner");
      assert(owned.terminated, "Owner failure must terminate the real Worker");
      await assertRejects(
        () => waitWithSignal(pending, AbortSignal.timeout(5000)),
        Error,
        "send failed",
      );
      await assertRejects(() => actor.echo("future call"));
      if (cleanup) await assertRejects(() => actor.dispose(), AggregateError, "cleanup");
    } finally {
      owned.source.terminate();
    }
  });
}

Deno.test("throwing codec cleanup is reported by join after all cleanup runs", async () => {
  const registry = new PayloadCodecRegistry().register(throwingCleanupCodec);
  let closed = false;
  const { wrapPort } = await import("./core/channel.ts");
  const ports = new MessageChannel();
  registry.registerChannel(wrapPort(ports.port1, {
    onClosed: () => {
      closed = true;
    },
  }));
  try {
    registry.failAll();
    assert(closed, "A throwing codec must not prevent channel closure");
    await assertRejects(() => registry.join(), AggregateError, "cleanup");
    registry.failAll();
  } finally {
    ports.port1.close();
    ports.port2.close();
  }
});

Deno.test("dispose with throwing codec cleanup returns a rejection after Worker termination", async () => {
  const owned = worker(true);
  try {
    const actor = await spawn<typeof rpc>(owned.source, { codecs: [throwingCleanupCodec] });
    const disposal = actor.dispose();
    assert(owned.terminated, "Codec cleanup errors must not skip Worker termination");
    await assertRejects(() => disposal, AggregateError, "cleanup");
    assertEquals(actor.dispose(), disposal, "Concurrent disposal must share its completion");
    await assertRejects(() => actor.echo("after dispose"));
  } finally {
    owned.source.terminate();
  }
});

Deno.test("process cleanup failure is reported after the child has exited", async () => {
  const actor = await spawnProcess<{ pid(): number }>(
    new URL("./test_fixtures/lifecycle/process.ts", import.meta.url).href,
    { codecs: [throwingCleanupCodec] },
  );
  const pid = await actor.pid();
  await assertRejects(() => actor.dispose(), AggregateError, "cleanup");
  assertThrows(
    () => processKill(pid, 0),
    Error,
    undefined,
    "Dispose must join child exit even when codec cleanup throws",
  );
});

for (const location of ["callback", "worker response"]) {
  Deno.test(`uncloneable ${location} result closes owner and rejects the pending call`, async () => {
    const owned = worker();
    try {
      const actor = await spawn<typeof rpc>(owned.source);
      const call = location === "callback"
        ? actor.invoke(() => Symbol("not cloneable"))
        : actor.uncloneableResult();
      await assertRejects(
        () => waitWithSignal(call, AbortSignal.timeout(5000)),
        Error,
        "send failed",
      );
      assert(owned.terminated, "Failed RPC result delivery must terminate the owning Worker");
      await actor.dispose();
    } finally {
      owned.source.terminate();
    }
  });
}

Deno.test("handshake rejection survives a throwing codec cleanup and terminates Worker", async () => {
  const owned = worker();
  try {
    await assertRejects(
      () => spawn(owned.source, { codecs: [throwingCleanupCodec] }),
      AggregateError,
      "startup and cleanup failed",
    );
    assert(
      owned.terminated,
      "Handshake failure must terminate Worker despite throwing onRegistryFail",
    );
  } finally {
    owned.source.terminate();
  }
});

Deno.test("a buffered incompatible handshake fails the initialized owner", async () => {
  const transport = createMux(() => {});
  transport.deliver({ type: "handshake", version: PROTOCOL_VERSION, kind: "framed", codecs: [] });
  try {
    await assertRejects(() => spawn(transport, { signal: null }), Error, "Codec mismatch");
    assert(transport.closed, "Buffered handshake rejection must close its transport");
  } finally {
    transport.close();
  }
});

Deno.test("plugin diagnostic objects on the shared Worker do not kill its Actor", async () => {
  const owned = worker();
  try {
    const actor = await spawn<typeof rpc>(owned.source);
    await actor.diagnostic();
    assertEquals(
      await actor.echo(42),
      42,
      "The strict-successor response must survive a diagnostic object",
    );
    assert(!owned.terminated);
    await actor.dispose();
  } finally {
    owned.source.terminate();
  }
});
