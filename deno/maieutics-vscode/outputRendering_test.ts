import { assert, assertEquals } from "@std/assert";
import { OutputRenderingBridge, type RenderedMarkdown } from "./outputRendering.ts";

const request = (revision: number, text = String(revision)) => ({
  source: "maieutics-output",
  type: "render",
  documentId: "doc",
  requestId: `r${revision}`,
  text,
});
const snapshot = (text: string): RenderedMarkdown => ({
  version: 1,
  documentId: "doc",
  html: text,
  css: "",
});

Deno.test("native fallback failures are diagnosed without copying untrusted error text", async () => {
  const logs: string[] = [];
  const bridge = new OutputRenderingBridge({
    render: () => Promise.resolve(undefined),
    post: () => Promise.resolve(),
    log: (message) => logs.push(message),
  });
  try {
    await bridge.handle({
      source: "maieutics-output",
      type: "applied",
      documentId: "doc",
      engine: "native",
      native: { error: "private source in an untrusted error" },
    });
    await bridge.handle({
      source: "maieutics-output",
      type: "applied",
      documentId: "doc",
      engine: "native",
      native: { headings: 1, tables: 1 },
    });
    assertEquals(logs, ["Native Markdown rendering failed; displaying plain source."]);
  } finally {
    await bridge.dispose();
  }
});

Deno.test("render bridge coalesces intermediate updates and never publishes stale source", async () => {
  const first = Promise.withResolvers<RenderedMarkdown>();
  const started = Promise.withResolvers<void>();
  const calls: string[] = [];
  const messages: unknown[] = [];
  const bridge = new OutputRenderingBridge({
    render: async (_id, text) => {
      calls.push(text);
      if (text === "1") {
        started.resolve();
        return await first.promise;
      }
      return snapshot(text);
    },
    post: (message) => {
      messages.push(message);
      return Promise.resolve();
    },
    log: () => {},
  });
  try {
    const one = bridge.handle(request(1));
    await started.promise;
    const two = bridge.handle(request(2));
    const three = bridge.handle(request(3));
    first.resolve(snapshot("obsolete"));
    await Promise.all([one, two, three]);
    assertEquals(calls, ["1", "3"]);
    assertEquals(messages, [{
      source: "maieutics-output",
      type: "rendered",
      documentId: "doc",
      requestId: "r3",
      html: "3",
      css: "",
    }]);
  } finally {
    first.resolve(snapshot("cleanup"));
    await bridge.dispose();
  }
});

Deno.test("render bridge cancellation observes its active operation and emits no late result", async () => {
  const started = Promise.withResolvers<void>();
  let cancelled = false;
  const messages: unknown[] = [];
  const bridge = new OutputRenderingBridge({
    render: (_id, _text, signal) =>
      new Promise((_resolve, reject) => {
        signal.addEventListener("abort", () => {
          cancelled = true;
          reject(signal.reason);
        }, { once: true });
        started.resolve();
      }),
    post: (message) => {
      messages.push(message);
      return Promise.resolve();
    },
    log: () => {},
  });
  const pending = bridge.handle(request(1));
  await started.promise;
  await bridge.dispose();
  await pending;
  assert(cancelled);
  assertEquals(messages, []);
});

Deno.test("append-only streaming commits a completed prefix before the newest snapshot finishes", async () => {
  const first = Promise.withResolvers<RenderedMarkdown>();
  const second = Promise.withResolvers<RenderedMarkdown>();
  const started = Promise.withResolvers<void>();
  const continued = Promise.withResolvers<void>();
  const messages: unknown[] = [];
  const prefix = "# Visible Markdown\n\n<script setup>\nconst count =";
  const latest = prefix + " ref(0)\n</script>\n\n## More Markdown";
  const bridge = new OutputRenderingBridge({
    render: async (_id, text) => {
      if (text === prefix) {
        started.resolve();
        return await first.promise;
      }
      continued.resolve();
      return await second.promise;
    },
    post: (message) => {
      messages.push(message);
      return Promise.resolve();
    },
    log: () => {},
  });
  try {
    const one = bridge.handle({ ...request(1, prefix), streaming: true });
    await started.promise;
    const two = bridge.handle({ ...request(2, latest), streaming: true });
    first.resolve(snapshot("<h1>Visible Markdown</h1>"));
    await continued.promise;
    assertEquals(messages, [{
      source: "maieutics-output",
      type: "rendered",
      documentId: "doc",
      requestId: "r1",
      streaming: true,
      renderedSource: prefix,
      html: "<h1>Visible Markdown</h1>",
      css: "",
    }]);
    second.resolve(snapshot("<h2>More Markdown</h2>"));
    await Promise.all([one, two]);
    assertEquals(messages.length, 2);
  } finally {
    first.resolve(snapshot("cleanup"));
    second.resolve(snapshot("cleanup"));
    await bridge.dispose();
  }
});

Deno.test("unsupported rendering keeps fallback visible and malformed requests never reach the client", async () => {
  const messages: unknown[] = [];
  let calls = 0;
  const bridge = new OutputRenderingBridge({
    render: () => {
      calls++;
      return Promise.resolve(undefined);
    },
    post: (message) => {
      messages.push(message);
      return Promise.resolve();
    },
    log: () => {},
  });
  try {
    await bridge.handle({ ...request(1), text: null });
    await bridge.handle(request(2));
    assertEquals(calls, 1);
    assertEquals(messages, [{
      source: "maieutics-output",
      type: "unavailable",
      documentId: "doc",
      requestId: "r2",
    }]);
  } finally {
    await bridge.dispose();
  }
});

Deno.test("streaming progress is invalidated by rewriting, sealing or explicit cancellation", async () => {
  for (
    const replacement of [
      { ...request(2, "rewritten"), streaming: true },
      { ...request(2, "prefix sealed"), assistant: true },
      { source: "maieutics-output", type: "cancel", documentId: "doc" },
    ]
  ) {
    const first = Promise.withResolvers<RenderedMarkdown>();
    const started = Promise.withResolvers<void>();
    const messages: { requestId?: string }[] = [];
    const bridge = new OutputRenderingBridge({
      render: async (_id, text) => {
        if (text === "prefix") {
          started.resolve();
          return await first.promise;
        }
        return snapshot(text);
      },
      post: (message) => {
        messages.push(message as { requestId?: string });
        return Promise.resolve();
      },
      log: () => {},
    });
    try {
      const one = bridge.handle({ ...request(1, "prefix"), streaming: true });
      await started.promise;
      const two = bridge.handle(replacement);
      first.resolve(snapshot("obsolete"));
      await Promise.all([one, two]);
      assert(!messages.some((message) => message.requestId === "r1"));
    } finally {
      first.resolve(snapshot("cleanup"));
      await bridge.dispose();
    }
  }
});

Deno.test("streaming requests cannot select template or executable evaluation", async () => {
  let calls = 0;
  const bridge = new OutputRenderingBridge({
    render: () => {
      calls++;
      return Promise.resolve(snapshot("unexpected"));
    },
    post: () => Promise.resolve(),
    log: () => {},
  });
  try {
    for (
      const mode of [
        { assistant: true },
        { vue: { version: 1, format: "markdown" } },
        { template: { version: 1, bindings: [] } },
      ]
    ) await bridge.handle({ ...request(1), ...mode, streaming: true });
    await bridge.handle({ ...request(1), streaming: "true" });
    assertEquals(calls, 0);
  } finally {
    await bridge.dispose();
  }
});

Deno.test("template binding updates keep the source identity and discard an older binding snapshot", async () => {
  const first = Promise.withResolvers<import("./outputRendering.ts").RenderedMarkdown>();
  const started = Promise.withResolvers<void>();
  const received: unknown[] = [];
  const posted: unknown[] = [];
  let calls = 0;
  const bridge = new OutputRenderingBridge({
    render: async (documentId, _source, _signal, template) => {
      received.push(template);
      if (++calls === 1) {
        started.resolve();
        return await first.promise;
      }
      return { version: 1, documentId, html: "ready", css: "" };
    },
    post: (message) => {
      posted.push(message);
      return Promise.resolve();
    },
    log: () => {},
  });
  try {
    const base = {
      source: "maieutics-output",
      type: "render",
      documentId: "doc",
      text: "{{ report }}",
    };
    const pending = bridge.handle({
      ...base,
      requestId: "pending",
      template: { version: 1, bindings: [{ name: "report", state: "pending" }] },
    });
    await started.promise;
    const ready = bridge.handle({
      ...base,
      requestId: "ready",
      template: { version: 1, bindings: [{ name: "report", state: "ready", value: 42 }] },
    });
    first.resolve({ version: 1, documentId: "doc", html: "obsolete pending", css: "" });
    await Promise.all([pending, ready]);
    assertEquals(received.length, 2);
    assertEquals(posted, [{
      source: "maieutics-output",
      type: "rendered",
      documentId: "doc",
      requestId: "ready",
      html: "ready",
      css: "",
    }]);
  } finally {
    await bridge.dispose();
  }
});

Deno.test("progressive bridge publishes prefixes and switches to compile-only after first Vue delivery", async () => {
  const first = Promise.withResolvers<RenderedMarkdown>();
  const started = Promise.withResolvers<void>();
  const options: unknown[] = [];
  const posted: Record<string, unknown>[] = [];
  const vue = {
    version: 1 as const,
    code: "initial",
    scopeId: "data-v-doc",
    setupKey: "a".repeat(64),
    renderCode: "render",
  };
  const bridge = new OutputRenderingBridge({
    render: async (_id, text, _signal, _template, _vue, _assistant, progressive) => {
      options.push(progressive);
      if (text === "first") {
        started.resolve();
        return await first.promise;
      }
      return { ...snapshot(""), renderedSource: text, vue: { ...vue, code: "" } };
    },
    post: (message) => {
      posted.push(message as Record<string, unknown>);
      return Promise.resolve();
    },
    log: () => {},
  });
  const live = (n: number, text: string, streaming = true) => ({
    ...request(n, text),
    assistant: true,
    progressive: true,
    ...(streaming ? { streaming: true } : {}),
  });
  try {
    const initial = bridge.handle(live(1, "first"));
    await started.promise;
    const later = bridge.handle(live(2, "first second"));
    first.resolve({ ...snapshot("SSR"), renderedSource: "first", vue });
    await Promise.all([initial, later]);
    assertEquals(options, [{ compileOnly: false, streaming: true }, {
      compileOnly: true,
      streaming: true,
    }]);
    assertEquals(posted.map((x) => x.renderedSource), ["first", "first second"]);
    await bridge.handle(live(3, "first second sealed", false));
    assertEquals(options.at(-1), { compileOnly: true, streaming: false });
    await bridge.handle(live(4, "rewritten"));
    assertEquals(options.at(-1), { compileOnly: false, streaming: true });
    await bridge.handle({ source: "maieutics-output", type: "cancel", documentId: "doc" });
    await bridge.handle(live(5, "rewritten again"));
    assertEquals(options.at(-1), { compileOnly: false, streaming: true });
  } finally {
    first.resolve({ ...snapshot(""), vue });
    await bridge.dispose();
  }
});

Deno.test("a rewrite invalidates pending Vue initialization even if source returns to an old prefix", async () => {
  const first = Promise.withResolvers<RenderedMarkdown>();
  const started = Promise.withResolvers<void>();
  const modes: boolean[] = [];
  const posts: unknown[] = [];
  const bridge = new OutputRenderingBridge({
    render: async (_id, text, _signal, _template, _vue, _assistant, options) => {
      modes.push(options?.compileOnly === true);
      if (modes.length === 1) {
        started.resolve();
        return await first.promise;
      }
      return {
        ...snapshot("new"),
        renderedSource: text,
        vue: { version: 1, code: "new", scopeId: "data-v-doc" },
      };
    },
    post: (value) => {
      posts.push(value);
      return Promise.resolve();
    },
    log: () => {},
  });
  try {
    const a = bridge.handle({
      ...request(1, "original"),
      assistant: true,
      progressive: true,
      streaming: true,
    });
    await started.promise;
    const b = bridge.handle({
      ...request(2, "rewrite"),
      assistant: true,
      progressive: true,
      streaming: true,
    });
    const c = bridge.handle({
      ...request(3, "original extended"),
      assistant: true,
      progressive: true,
      streaming: true,
    });
    first.resolve({
      ...snapshot("obsolete"),
      vue: { version: 1, code: "old", scopeId: "data-v-doc" },
    });
    await Promise.all([a, b, c]);
    assertEquals(modes, [false, false]);
    assertEquals(posts.length, 1);
  } finally {
    first.resolve(snapshot(""));
    await bridge.dispose();
  }
});
