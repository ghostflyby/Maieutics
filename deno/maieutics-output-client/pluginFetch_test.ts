import { assertEquals, assertRejects, assertThrows } from "@std/assert";
import { createRenderer, defineComponent } from "@vue/runtime-dom";
import { createPluginHttpClient, useFetch } from "./pluginFetch.ts";
import type { PluginFetch } from "./types.d.ts";

Deno.test("plugin HTTP preserves native response, binary body and caller options without ambient authority", async () => {
  const calls: { url: string; init: RequestInit }[] = [];
  const response = new Response(new Uint8Array([0, 255, 128]), { status: 422 });
  const client = createPluginHttpClient("http://localhost:1234/scoped/plugins/a/", {
    fetch(input, init) {
      calls.push({ url: String(input), init: init ?? {} });
      return Promise.resolve(response);
    },
  });
  const body = new Uint8Array([255, 0, 42]);
  try {
    assertEquals(
      await client.fetch("/api/file?q=1", {
        method: "POST",
        body,
        headers: { "content-type": "application/octet-stream" },
        redirect: "follow",
        credentials: "include",
      }),
      response,
    );
    assertEquals(calls.length, 1);
    assertEquals(calls[0].url, "http://localhost:1234/scoped/plugins/a/api/file?q=1");
    assertEquals(calls[0].init.body, body);
    assertEquals(calls[0].init.method, "POST");
    assertEquals(calls[0].init.redirect, "error");
    assertEquals(calls[0].init.credentials, "omit");
    assertEquals(calls[0].init.referrerPolicy, "no-referrer");
    assertEquals(response.status, 422);
    assertEquals(new Uint8Array(await response.arrayBuffer()), new Uint8Array([0, 255, 128]));
  } finally {
    client.dispose();
  }
});

Deno.test("plugin HTTP rejects path escape before sending and disposed scopes never send again", async () => {
  let sent = 0;
  const client = createPluginHttpClient("https://local.invalid/token/plugins/a/", {
    fetch() {
      sent++;
      return Promise.resolve(new Response());
    },
  });
  for (
    const path of [
      "../b",
      "/../b",
      "//evil.invalid/",
      "https://evil.invalid/",
      "a\\b",
      "%2e%2e/b",
      "x#secret",
    ]
  ) {
    await assertRejects(() => client.fetch(path), TypeError, "invalid_plugin_http_path");
  }
  assertEquals(sent, 0);
  client.dispose();
  client.dispose();
  await assertRejects(() => client.fetch("/api"), DOMException);
  assertEquals(sent, 0);
  assertThrows(() => createPluginHttpClient("file:///tmp/"), TypeError);
});

Deno.test("plugin HTTP combines caller cancellation with owner lifetime after response headers", async () => {
  const signals: AbortSignal[] = [];
  const client = createPluginHttpClient("http://localhost:1234/token/plugins/a/", {
    fetch(_input, init) {
      if (init?.signal) signals.push(init.signal);
      return Promise.resolve(new Response("body"));
    },
  });
  const caller = new AbortController();
  await client.fetch("/first", { signal: caller.signal });
  await client.fetch("/second");
  caller.abort();
  assertEquals(signals.map((signal) => signal.aborted), [true, false]);
  client.dispose();
  assertEquals(signals.map((signal) => signal.aborted), [true, true]);
});

// A real Vue scope without DOM: exercise setup injection and unmount, not a mocked composable.
function appFor(setup: () => void) {
  const node = () => ({});
  return createRenderer({
    patchProp() {},
    insert() {},
    remove() {},
    createElement: node,
    createText: node,
    createComment: node,
    setText() {},
    setElementText() {},
    parentNode: () => null,
    nextSibling: () => null,
  }).createApp(defineComponent({
    setup() {
      setup();
      return () => null;
    },
  }));
}

Deno.test("useFetch requires an authorized Vue setup and unmount cancels only its own requests", async () => {
  assertThrows(() => useFetch(), Error, "plugin_fetch_requires_setup");
  const unauthorized = appFor(() =>
    assertThrows(() => useFetch(), Error, "plugin_fetch_unavailable")
  );
  unauthorized.mount({});
  unauthorized.unmount();
  const signals: AbortSignal[] = [];
  const client = createPluginHttpClient("http://localhost:1234/token/plugins/a/", {
    fetch(_input, init) {
      if (init?.signal) signals.push(init.signal);
      return Promise.resolve(new Response());
    },
  });
  const fetchers: PluginFetch[] = [];
  const apps = [appFor(() => fetchers.push(useFetch())), appFor(() => fetchers.push(useFetch()))];
  try {
    for (const app of apps) {
      client.install(app);
      app.mount({});
    }
    await fetchers[0]("/one");
    await fetchers[1]("/two");
    apps[0].unmount();
    assertEquals(signals.map((signal) => signal.aborted), [true, false]);
    await assertRejects(() => fetchers[0]("/late"), DOMException);
    client.dispose();
    assertEquals(signals.map((signal) => signal.aborted), [true, true]);
  } finally {
    apps[1].unmount();
    client.dispose();
  }
});
