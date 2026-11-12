import { assert, assertEquals, assertNotEquals, assertRejects } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";

const sdk = new URL("../maieutics-plugin-sdk/entry.ts", import.meta.url).href;
const worker = new URL("./worker_entry.ts", import.meta.url).href;

function config(root: string, id: string): PluginConfig {
  const dir = `${root}/${id}`;
  Deno.mkdirSync(dir);
  const entry = `${dir}/main.ts`;
  Deno.writeTextFileSync(
    entry,
    `
    import { provide, shallowRef, http } from ${JSON.stringify(sdk)};
    let release = () => {};
    const gate = new Promise(resolve => { release = resolve; });
    let cancelResolved = () => {};
    const cancelled = new Promise(resolve => { cancelResolved = resolve; });
    let deliveryResolved = () => {};
    const delivered = new Promise(resolve => { deliveryResolved = resolve; });
    provide(http, shallowRef(async (request, info) => {
      const path = new URL(request.url).pathname;
      if (path === '/echo') return new Response(request.body, { headers: {'content-type':'application/octet-stream'} });
      if (path === '/release') { release(); return new Response('released'); }
      if (path === '/redirect') return new Response(null, {status:302,headers:{location:'/echo'}});
      if (path === '/stream') {
        let first = true;
        return new Response(new ReadableStream({
          async pull(controller) {
            if (first) { first = false; controller.enqueue(new Uint8Array([0,255])); return; }
            await gate; controller.enqueue(new Uint8Array([128])); controller.close();
          },
          cancel() { cancelResolved(true); release(); }
        }));
      }
      if (path === '/cancelled') return Response.json(await cancelled);
      if (path === '/delivery') {
        void info.completed.then(() => deliveryResolved('delivered'), () => deliveryResolved('interrupted'));
        return new Response('delivery');
      }
      if (path === '/delivered') return Response.json(await delivered);
      return Response.json({plugin:${JSON.stringify(id)}, path, method:request.method});
    }));
  `,
  );
  return {
    id,
    rootDir: dir,
    permissions: { read: [dir] },
    workers: [{
      exportName: "./main",
      entryUrl: new URL(`file://${entry}`).href,
      specifier: `@maieutics/${id}/main`,
    }],
  };
}

async function fixture() {
  const root = Deno.makeTempDirSync({ prefix: "maieutics-http-" });
  const host = new PluginHost({
    sdkUrl: sdk,
    workerEntryUrl: worker,
    plugins: [config(root, "alpha"), config(root, "beta")],
  });
  const gateway = host.httpGateway();
  const ready = Promise.withResolvers<void>();
  const unsubscribe = gateway.onMountsChanged(() => {
    if (gateway.snapshots().length === 2) ready.resolve();
  });
  const budget = AbortSignal.timeout(10_000);
  const expired = () => ready.reject(new Error("HTTP provider registration did not complete"));
  budget.addEventListener("abort", expired, { once: true });
  try {
    await host.startAll();
    await ready.promise;
    const address = await gateway.startRouter({ token: "host-only-token" });
    const base = `http://127.0.0.1:${address.port}`;
    const url = (id: string) => {
      const mount = gateway.snapshots().find((mount) => mount.pluginId === id);
      if (!mount) throw new Error("missing HTTP mount");
      return `${base}/${mount.accessToken}/plugins/${id}/`;
    };
    return {
      host,
      gateway,
      base,
      url,
      async dispose() {
        await gateway.stopRouter();
        host.dispose();
        Deno.removeSync(root, { recursive: true });
      },
    };
  } catch (error) {
    await gateway.stopRouter();
    host.dispose();
    Deno.removeSync(root, { recursive: true });
    throw error;
  } finally {
    unsubscribe();
    budget.removeEventListener("abort", expired);
  }
}

Deno.test("plugin-scoped HTTP credentials cannot address another worker and redirects retain scope", async () => {
  const f = await fixture();
  try {
    const alpha = f.url("alpha"), beta = f.url("beta");
    assertNotEquals(new URL(alpha).pathname.split("/")[1], new URL(beta).pathname.split("/")[1]);
    assertEquals(await (await fetch(`${alpha}api`)).json(), {
      plugin: "alpha",
      path: "/api",
      method: "GET",
    });
    const denied = await fetch(alpha.replace("/plugins/alpha/", "/plugins/beta/"));
    assertEquals(denied.status, 404);
    await denied.body?.cancel();
    const redirect = await fetch(`${alpha}redirect`, { redirect: "manual" });
    assertEquals(redirect.headers.get("location"), `${new URL(alpha).pathname}echo`);
    await redirect.body?.cancel();
    assert(!alpha.includes("host-only-token"));
    const cors = await fetch(`${alpha}echo`, {
      method: "OPTIONS",
      headers: {
        origin: "null",
        "access-control-request-method": "POST",
        "access-control-request-headers": "content-type,x-example",
      },
    });
    assertEquals(cors.status, 204);
    assertEquals(cors.headers.get("access-control-allow-origin"), "*");
    assertEquals(cors.headers.get("access-control-allow-headers"), "content-type,x-example");
    const wrongPreflight = await fetch(alpha.replace("/plugins/alpha/", "/plugins/beta/"), {
      method: "OPTIONS",
      headers: { origin: "null", "access-control-request-method": "POST" },
    });
    assertEquals(wrongPreflight.status, 404);
    await wrongPreflight.body?.cancel();
    await f.host.stop("alpha", "./main");
    const revoked = await fetch(`${alpha}api`);
    assertEquals(revoked.status, 404);
    await revoked.body?.cancel();
    const survivor = await fetch(`${beta}api`);
    assertEquals((await survivor.json()).plugin, "beta");
    assertEquals(await (await fetch(`${beta}delivery`)).text(), "delivery");
    assertEquals(
      await (await fetch(`${beta}delivered`, { signal: AbortSignal.timeout(5_000) })).json(),
      "delivered",
    );
  } finally {
    await f.dispose();
  }
});

Deno.test("aborting native Fetch cancels an already-open response inside the worker", async () => {
  const f = await fixture();
  const lifetime = new AbortController();
  try {
    const response = await fetch(`${f.url("alpha")}stream`, { signal: lifetime.signal });
    const reader = response.body?.getReader();
    assert(reader);
    assertEquals((await reader.read()).value, new Uint8Array([0, 255]));
    lifetime.abort();
    await assertRejects(() => reader.read(), DOMException);
    reader.releaseLock();
    const signal = AbortSignal.timeout(5_000);
    assertEquals(await (await fetch(`${f.url("alpha")}cancelled`, { signal })).json(), true);
  } finally {
    lifetime.abort();
    await f.dispose();
  }
});

Deno.test("HTTP carries binary to a worker without network permission and incrementally streams its response", async () => {
  const f = await fixture();
  const base = f.url("alpha");
  try {
    const bytes = new Uint8Array([0, 255, 128, 42]);
    const echo = await fetch(`${base}echo`, { method: "POST", body: bytes });
    assertEquals(new Uint8Array(await echo.arrayBuffer()), bytes);
    const streamed = await fetch(`${base}stream`);
    const reader = streamed.body?.getReader();
    assert(reader);
    assertEquals((await reader.read()).value, new Uint8Array([0, 255]));
    // The second chunk is gated by a separate request: first read proves no whole-body buffering.
    await (await fetch(`${base}release`, { method: "POST" })).text();
    assertEquals((await reader.read()).value, new Uint8Array([128]));
    assertEquals((await reader.read()).done, true);
    reader.releaseLock();
    await assertRejects(() => fetch(`${base}redirect`, { redirect: "error" }), TypeError);
  } finally {
    await f.dispose();
  }
});

Deno.test("HTTP worker replacement rotates only its mount credential and the old URL stays revoked", async () => {
  const f = await fixture();
  const old = f.url("alpha");
  const beta = f.url("beta");
  const changed = Promise.withResolvers<void>();
  const unsubscribe = f.gateway.onMountsChanged(() => {
    const mount = f.gateway.snapshots().find((mount) => mount.pluginId === "alpha");
    if (mount && !old.includes(mount.accessToken)) changed.resolve();
  });
  const timer = setTimeout(
    () => changed.reject(new Error("Replacement HTTP mount was not published")),
    5_000,
  );
  try {
    await f.host.reload("alpha", "./main");
    await changed.promise;
    assertNotEquals(f.url("alpha"), old);
    assertEquals(f.url("beta"), beta);
    const revoked = await fetch(`${old}api`);
    assertEquals(revoked.status, 404);
    await revoked.body?.cancel();
    assertEquals((await (await fetch(`${f.url("alpha")}api`)).json()).plugin, "alpha");
  } finally {
    clearTimeout(timer);
    unsubscribe();
    await f.dispose();
  }
});

Deno.test("active HTTP roots pin their workers across an awaited idle maintenance sweep", async () => {
  const f = await fixture();
  const url = f.url("alpha");
  try {
    const now = Date.now();
    await f.host.reclaimIdleWorkers(now);
    await f.host.reclaimIdleWorkers(now + 60_000);
    assertEquals(
      f.host.states().map((worker) => worker.state),
      ["running", "running"],
      "A published HTTP service is a live actor reference, not an idle worker",
    );
    assertEquals(f.url("alpha"), url);
    assertEquals((await (await fetch(`${url}api`)).json()).plugin, "alpha");
  } finally {
    await f.dispose();
  }
});
