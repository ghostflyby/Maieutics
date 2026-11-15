import { assert, assertEquals } from "@std/assert";
import { createActorConnection, messageTransport } from "./browser.ts";
import { waitWithSignal } from "./core/lifetime.ts";

Deno.test("plain JSON void RPC and callbacks preserve pending and future calls", async () => {
  const json = (value: unknown) => JSON.parse(JSON.stringify(value));
  const a = messageTransport({ send: (frame) => b.deliver(json(frame)) });
  const b = messageTransport({ send: (frame) => a.deliver(json(frame)) });
  a.onClose(b.close);
  b.onClose(a.close);
  const entered = Promise.withResolvers<void>();
  const finish = Promise.withResolvers<number>();
  const api = {
    async pending() {
      entered.resolve();
      return await finish.promise;
    },
    empty(): void {},
    nothing: () => null,
    invoke: (callback: () => Promise<void>) => callback(),
    echo: (value: number) => value,
  };
  const server = createActorConnection(a, { actors: { api } });
  const client = createActorConnection<{ api: typeof api }>(b);
  try {
    await client.ready;
    const actor = client.actor("api");
    const pending = actor.pending();
    void pending.catch(() => {});
    await waitWithSignal(entered.promise, AbortSignal.timeout(5000));
    assertEquals(
      await actor.empty(),
      undefined,
      "JSON omission of an undefined value is a successful void result",
    );
    assertEquals(
      await actor.invoke(() => {}),
      undefined,
      "Callback responses share the void result rule",
    );
    assertEquals(await actor.nothing(), null, "Null and omitted undefined remain distinct");
    assertEquals(await actor.echo(42), 42, "Void replies must preserve future calls");
    finish.resolve(7);
    assertEquals(await pending, 7, "Void replies must preserve existing pending calls");
    assert(!a.closed && !b.closed);
  } finally {
    finish.resolve(0);
    client.close();
    server.close();
    await Promise.all([client.completion, server.completion]);
  }
});
