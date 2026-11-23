import { assert, assertEquals, assertRejects, assertStrictEquals, assertThrows } from "@std/assert";

type OutputClient = typeof import("../mod.ts");
type RenderRequest = Extract<import("../mod.ts").OutputMessage, { type: "render" }>;

function element(): HTMLDivElement {
  const node = document.createElement("div");
  document.body.append(node);
  return node;
}

function find<T extends Element>(root: ParentNode, selector: string): T {
  const node = root.querySelector<T>(selector);
  assert(node, `No mounted control matches ${selector}`);
  return node;
}

function formRoot(node: HTMLElement): ShadowRoot {
  assert(node.shadowRoot, "Form did not attach a shadow root");
  return node.shadowRoot;
}

function renderedRoot(container: HTMLElement, selector: string): ShadowRoot {
  for (const element of container.querySelectorAll("*")) {
    if (element.shadowRoot?.querySelector(selector)) return element.shadowRoot;
  }
  throw new Error(`The renderer did not produce ${selector}`);
}

async function assertHostStylesheet(root: ShadowRoot, selector: string): Promise<void> {
  const sheet = find<HTMLLinkElement>(root, 'link[rel="stylesheet"]');
  if (!sheet.sheet) {
    await new Promise<void>((resolve, reject) => {
      sheet.onload = () => resolve();
      sheet.onerror = () => reject(new Error("The supplied output stylesheet failed to load"));
    });
  }
  assertEquals(getComputedStyle(find(root, selector)).color, "rgb(10, 40, 90)");
}

async function renderedOutput(api: OutputClient): Promise<void> {
  const requests: RenderRequest[] = [];
  const renderer = api.createOutputRenderer({
    stylesheetUrl: "/fixture-output.css",
    sendMessage(message) {
      if (message.type === "render") requests.push(message);
    },
  });
  const native = element();
  const template = element();
  const vue = element();
  const reply = async (request: RenderRequest, html: string, component?: object) => {
    await renderer.receive({
      source: "maieutics-output",
      type: "rendered",
      documentId: request.documentId,
      requestId: request.requestId,
      html,
      css: "",
      ...(component ? { vue: component } : {}),
    });
  };
  try {
    const raw = "<script>foreign content</script>";
    const text = `# Native Markdown\n\n| Key | Value |\n| --- | --- |\n| A | B |\n\n${raw}`;
    await renderer.render({ id: "message", kind: "assistant", value: { text } }, native);
    const fallback = renderedRoot(native, "h1");
    assertEquals(find(fallback, "h1").textContent, "Native Markdown");
    assertEquals(find(fallback, "td").textContent, "A");
    await assertHostStylesheet(fallback, "h1");
    assert(!fallback.querySelector("script"), "Markdown created an authored script element");
    assert(fallback.textContent?.includes(raw), "Markdown discarded the literal authored content");
    const markdown = requests.shift();
    assert(markdown);
    assertEquals(markdown.text, text);
    await reply(markdown, '<pre class="shiki"><code>SSR response</code></pre>');
    const highlighted = renderedRoot(native, ".shiki");
    assertEquals(find(highlighted, "code").textContent, "SSR response");
    assert(!highlighted.querySelector("h1"), "SSR left the native fallback attached");
    await assertHostStylesheet(highlighted, ".shiki");

    await renderer.render({
      id: "template",
      kind: "template",
      value: { version: 1, source: "{{ value }}", bindings: [] },
    }, template);
    const templateRequest = requests.shift();
    assert(templateRequest?.template, "Data template was sent as ordinary Markdown");
    await reply(templateRequest, '<p class="template-value">Resolved value</p>');
    assertEquals(
      find(renderedRoot(template, ".template-value"), "p").textContent,
      "Resolved value",
    );

    await renderer.render({
      id: "vue",
      kind: "vue",
      value: { version: 1, format: "sfc", source: "<template><button>0</button></template>" },
    }, vue);
    const vueRequest = requests.shift();
    assert(vueRequest?.vue, "Vue output was sent as ordinary Markdown");
    await reply(vueRequest, '<button class="counter">0</button>', {
      version: 1,
      scopeId: `data-v-${vueRequest.documentId}`,
      code:
        'const {h,ref}=Vue;return {setup(){const count=ref(0);return ()=>h("button",{class:"counter",onClick:()=>count.value++},String(count.value))}}',
    });
    const counter = find<HTMLButtonElement>(renderedRoot(vue, ".counter"), ".counter");
    counter.click();
    await api.vueUpdatesComplete();
    assertEquals(counter.textContent, "1", "The rendered Vue component did not hydrate");
  } finally {
    renderer.dispose();
    native.remove();
    template.remove();
    vue.remove();
  }
}

async function streamingRevisions(api: OutputClient): Promise<void> {
  const container = element();
  const requests: RenderRequest[] = [];
  const renderer = api.createOutputRenderer({
    sendMessage(message) {
      if (message.type === "render") requests.push(message);
    },
  });
  const paint = async (text: string, streaming = true) => {
    await renderer.render({
      id: "stream",
      kind: streaming ? "turn" : "assistant",
      value: { text, streaming },
    }, container);
    const request = requests.pop();
    assert(request && request.text === text, "Rendering did not dispatch the addressed source");
    return request;
  };
  const reply = (request: RenderRequest, result: string, extra = {}) =>
    renderer.receive({
      ...request,
      type: "rendered",
      html: `<p>${result}</p>`,
      css: "",
      renderedSource: request.text,
      ...extra,
    });
  const committed = () => find<HTMLElement>(container, '[data-render-engine="ssr"]');
  const content = (node: HTMLElement) => (node.shadowRoot ?? node).textContent;
  try {
    const first = await paint("Prefix");
    const second = await paint("Prefix grows");
    assert(first.streaming && !first.assistant && !first.vue, "Open streams use passive SSR");
    assertEquals(first.documentId, second.documentId, "Appends retain the document identity");
    assert(first.requestId !== second.requestId, "Appends advance the request identity");
    await reply(first, "First prefix");
    const boundary = committed();
    assert(content(boundary)?.includes("First prefix"), "An append-only ancestor can commit");
    await reply(second, "Second prefix");
    assertStrictEquals(committed(), boundary);
    await reply(first, "Regressed prefix");
    assert(content(boundary)?.includes("Second prefix"), "An older reply regressed the prefix");

    const third = await paint("Prefix grows again");
    const probe = document.documentElement.dataset;
    try {
      await reply(third, "Unsafe preview", {
        vue: {
          version: 1,
          code: 'document.documentElement.dataset.passiveFactoryRan = "yes"; throw Error("probe");',
          scopeId: `data-v-${third.documentId}`,
        },
      });
      assertEquals(probe.passiveFactoryRan, undefined, "A passive stream executed a Vue factory");
      assert(content(boundary)?.includes("Second prefix"), "A passive stream accepted a Vue reply");
    } finally {
      delete probe.passiveFactoryRan;
    }
    await paint("Rewrite");
    await paint("Prefix grows again after rewrite");
    const rewritten = committed();
    const before = content(rewritten);
    await reply(third, "Old epoch");
    assertEquals(content(rewritten), before, "A rewrite invalidates the earlier prefix epoch");

    const sealed = await paint("Sealed answer", false);
    await reply(third, "Late passive preview");
    assert(
      !container.querySelector('[data-render-engine="ssr"]') ||
        !content(committed())?.includes("Late passive preview"),
      "A late passive reply replaced the sealed answer",
    );
    await reply(sealed, "Sealed snapshot");
    assert(content(committed())?.includes("Sealed snapshot"), "The sealed revision did not commit");
  } finally {
    renderer.dispose();
    container.remove();
  }
}

async function pendingRender(api: OutputClient): Promise<void> {
  const container = element();
  const requests: RenderRequest[] = [];
  let hold:
    | { entered: PromiseWithResolvers<void>; release: PromiseWithResolvers<void> }
    | undefined;
  const renderer = api.createOutputRenderer({
    sendMessage(message) {
      if (message.type === "render") requests.push(message);
    },
    async renderMarkdown(text, node) {
      const active = hold;
      if (active) {
        hold = undefined;
        active.entered.resolve();
        await active.release.promise;
      }
      node.textContent = text;
    },
  });
  const paint = (text: string) =>
    renderer.render({ id: "replacement", kind: "turn", value: { text } }, container);
  const request = () => {
    const latest = requests.at(-1);
    assert(latest, "No SSR request has been dispatched");
    return latest;
  };
  const reply = (request: RenderRequest, text: string) =>
    renderer.receive({ ...request, type: "rendered", html: `<p>${text}</p>`, css: "" });
  const committed = () => container.querySelector<HTMLElement>('[data-render-engine="ssr"]');
  const gate = () =>
    hold = { entered: Promise.withResolvers<void>(), release: Promise.withResolvers<void>() };
  let release: (() => void) | undefined;
  try {
    await paint("obsolete source");
    const obsolete = request();
    await paint("source");
    await reply(obsolete, "Obsolete response");
    assertEquals(committed(), null, "A stale response replaced the current fallback");
    await reply(request(), "Committed output");
    const boundary = committed();
    assert(boundary, "The initial SSR response did not commit");
    const paragraph = find(boundary.shadowRoot ?? boundary, "p");
    const submitted = requests.length;
    await paint("source");
    assertEquals(requests.length, submitted, "The same source was submitted twice");

    const slow = gate();
    release = () => slow.release.resolve();
    const olderPaint = paint("slow native preparation");
    await slow.entered.promise;
    assert(paragraph.isConnected, "Pending native preparation removed committed content");
    await paint("latest source");
    assertStrictEquals(committed(), boundary, "Waiting for SSR replaced the committed boundary");
    const latest = request();
    await renderer.receive({ ...latest, type: "unavailable" });
    assertEquals(committed(), null, "An unavailable replacement retained stale SSR");
    assertEquals(
      find(container, "[data-markdown-index]").textContent,
      "latest source",
      "The newest fallback must be fully prepared before it is revealed",
    );
    await paint("newest source");
    await reply(request(), "Newest output");
    const newest = committed();
    assert(newest, "The replacement SSR response did not commit");
    release();
    await olderPaint;
    assertStrictEquals(committed(), newest, "Late native preparation replaced a newer revision");

    await paint("about to be disposed");
    const disposed = request();
    renderer.disposeOutput("replacement");
    await reply(disposed, "Late disposed result");
    assertStrictEquals(committed(), newest, "Disposal left a live SSR reply handler");
    assert(
      !(newest.shadowRoot ?? newest).textContent?.includes("Late disposed result"),
      "A disposed reply updated the retained boundary",
    );

    const pending = gate();
    release = () => pending.release.resolve();
    const count = requests.length;
    const disposedPaint = paint("disposed during native preparation");
    await pending.entered.promise;
    renderer.disposeOutput("replacement");
    release();
    await disposedPaint;
    assertEquals(requests.length, count, "Disposed preparation still dispatched an SSR request");
  } finally {
    release?.();
    renderer.dispose();
    container.remove();
  }
}

async function failedMarkdownPreparation(api: OutputClient): Promise<void> {
  const container = element();
  const sent: import("../mod.ts").OutputMessage[] = [];
  const renderer = api.createOutputRenderer({
    sendMessage(message) {
      sent.push(message);
    },
    renderMarkdown(_text, node) {
      node.attachShadow({ mode: "open" });
      throw new TypeError("private native renderer details");
    },
  });
  try {
    const text = "# Still readable";
    await renderer.render({ id: "native-failure", kind: "turn", value: { text } }, container);
    const request = sent.find((message) => message.type === "render");
    assert(request, "A native fallback failure prevented SSR dispatch");
    await renderer.receive({ ...request, type: "unavailable" });
    const fallback = find<HTMLElement>(container, "[data-markdown-index]");
    assertEquals(fallback.shadowRoot?.textContent, text, "Native failure left the source hidden");
    const applied = sent.findLast((message) => message.type === "applied");
    assertEquals(applied?.native?.error, "TypeError", "The diagnostic must report a category");
    assert(!JSON.stringify(sent).includes("private native renderer details"));
  } finally {
    renderer.dispose();
    container.remove();
  }
}

async function snapshotInteractions(api: OutputClient): Promise<void> {
  const container = element();
  let request: RenderRequest | undefined;
  const renderer = api.createOutputRenderer({
    sendMessage(message) {
      if (message.type === "render") request = message;
    },
  });
  const paint = async (text: string) => {
    await renderer.render({ id: "toc", kind: "assistant", value: { text } }, container);
    assert(request && request.text === text);
    const { documentId } = request;
    await renderer.receive({
      ...request,
      type: "rendered",
      html: `<a href="#${documentId}-heading">Section</a>
        <details><summary>Section</summary><h2 id="${documentId}-heading" tabindex="-1">Heading</h2></details>
        <fieldset class="vp-code-group"><input type="radio" name="${documentId}-code-group" id="${documentId}-first" checked>
        <label for="${documentId}-first">first.ts</label>
        <input type="radio" name="${documentId}-code-group" id="${documentId}-second">
        <label for="${documentId}-second">second.ts</label></fieldset>`,
      css: "",
    });
    return renderedRoot(container, "h2");
  };
  try {
    const root = await paint("First source");
    const heading = find<HTMLElement>(root, "h2");
    const anchor = find<HTMLAnchorElement>(root, "a");
    let scrolls = 0;
    heading.scrollIntoView = () => scrolls++;
    anchor.click();
    assertEquals(scrolls, 1, "A scoped anchor did not scroll its heading");
    assertStrictEquals(root.activeElement, heading, "A scoped anchor did not focus its heading");
    assert(
      find<HTMLDetailsElement>(root, "details").open,
      "A scoped anchor left its heading hidden",
    );

    find<HTMLLabelElement>(root, "label:last-child").click();
    assert(find<HTMLInputElement>(root, "input[id$=second]").checked);
    const updated = await paint("Revised source");
    anchor.click();
    assertEquals(scrolls, 1, "An obsolete SSR region still handled link clicks");
    assert(
      find<HTMLInputElement>(updated, "input[id$=second]").checked,
      "Replacing SSR reset the selected code example",
    );
    const currentHeading = find<HTMLElement>(updated, "h2");
    currentHeading.scrollIntoView = () => scrolls++;
    renderer.disposeOutput("toc");
    find<HTMLAnchorElement>(updated, "a").click();
    assertEquals(scrolls, 1, "A disposed SSR region still handled link clicks");
  } finally {
    renderer.dispose();
    container.remove();
  }
}

async function toolDisclosure(api: OutputClient): Promise<void> {
  const first = element();
  const second = element();
  const renderer = api.createOutputRenderer({ sendMessage() {} });
  const snapshot = (id: string, result: string) => ({
    id,
    kind: "turn" as const,
    value: {
      nodeId: "entry:0",
      timeline: [{ kind: "tool", index: 0 }],
      tools: [{ tool: "read_file", status: "ok", result: { value: result } }],
    },
  });
  try {
    await renderer.render(snapshot("first", "initial contents"), first);
    const detail = find<HTMLDetailsElement>(first, "details");
    const opened = new Promise<void>((resolve) =>
      detail.addEventListener("toggle", () => resolve(), { once: true })
    );
    detail.open = true;
    await opened;
    await renderer.render(snapshot("first", "updated contents"), first);
    await renderer.render(snapshot("second", "other contents"), second);
    assert(find<HTMLDetailsElement>(first, "details").open, "A repaint reset the user's expansion");
    assert(
      !find<HTMLDetailsElement>(second, "details").open,
      "A separate output inherited expansion",
    );
  } finally {
    renderer.dispose();
    first.remove();
    second.remove();
  }
}

async function forms(api: OutputClient): Promise<void> {
  const sent: import("../mod.ts").FormMessage[] = [];
  const client = api.createFormRenderer({ sendMessage: (message) => sent.push(message) });
  const first = element();
  const second = element();
  const frozen = element();
  const state = {
    title: "Original",
    fields: [
      { name: "note", type: "text", required: true },
      { name: "amount", type: "number" },
      { name: "enabled", type: "boolean" },
      { name: "choice", type: "choice", choices: ["a", "b"] },
      { name: "secret", type: "password" },
    ],
    values: { note: "initial", amount: 1, enabled: false, choice: "a", secret: "" },
  };
  const output = { version: 1, modelId: "model", state };
  const publish = (title: string) =>
    client.receive({
      source: "maieutics-form",
      type: "state",
      modelId: "model",
      state: {
        ...state,
        title,
      },
    });
  try {
    client.render("first", output, first);
    const root = formRoot(first);
    const controls = find<HTMLFieldSetElement>(root, "fieldset");
    assert(controls.disabled, "A form allows input before receiving model state");
    await publish("Updated");
    assert(!controls.disabled, "Live model state did not enable its form");
    const note = find<HTMLInputElement>(root, '[name="note"]');
    note.focus();
    for (
      const [name, value] of Object.entries({
        note: "typed",
        amount: "2.5",
        enabled: true,
        choice: "b",
        secret: "value",
      })
    ) {
      const field = find<HTMLInputElement | HTMLSelectElement>(root, `[name="${name}"]`);
      if (field instanceof HTMLInputElement && typeof value === "boolean") field.checked = value;
      else field.value = String(value);
      field.dispatchEvent(new Event(field instanceof HTMLSelectElement ? "change" : "input"));
    }
    await api.vueUpdatesComplete();
    await publish("Another revision");
    client.render("first", output, first);
    assertStrictEquals(find(root, '[name="note"]'), note, "State update remounted the editor");
    assertStrictEquals(root.activeElement, note);
    find<HTMLButtonElement>(root, ".submit").click();
    assertEquals(sent.at(-1)?.payload?.values, {
      note: "typed",
      amount: 2.5,
      enabled: true,
      choice: "b",
      secret: "value",
    });

    client.render("second", output, second);
    await publish("Shared state");
    assertEquals(find(formRoot(second), ".title").textContent, "Shared state");
    assertEquals(find(root, ".title").textContent, "Shared state");
    const detachedCancel = find<HTMLButtonElement>(root, ".cancel");
    const beforeDispose = sent.length;
    client.disposeOutput("first");
    detachedCancel.click();
    assertEquals(sent.slice(beforeDispose).map(({ type }) => type), ["unmount"]);
    find<HTMLButtonElement>(formRoot(second), ".cancel").click();
    assertEquals(sent.at(-1)?.name, "cancel");
    assertEquals(sent.at(-1)?.viewId, "second");

    await client.receive({ source: "maieutics-form", type: "closed", modelId: "model" });
    assert(find<HTMLFieldSetElement>(formRoot(second), "fieldset").disabled);
    const beforeSnapshot = sent.length;
    client.render("frozen", { ...output, snapshot: true }, frozen);
    await publish("Live update after opening a snapshot");
    assert(find<HTMLFieldSetElement>(formRoot(frozen), "fieldset").disabled);
    assertEquals(find(formRoot(frozen), ".title").textContent, "Original");
    assertEquals(sent.length, beforeSnapshot);

    client.dispose();
    client.dispose();
    assertThrows(() => client.render("late", output, first), Error);
  } finally {
    client.dispose();
    first.remove();
    second.remove();
    frozen.remove();
  }

  const cleanup = api.createFormRenderer({
    sendMessage(message) {
      if (message.type === "unmount") throw new Error("host closed");
    },
  });
  const a = element(), b = element();
  try {
    cleanup.render("a", output, a);
    cleanup.render("b", output, b);
    const failure = assertThrows(() => cleanup.dispose(), AggregateError);
    assertEquals(failure.errors.map((error: Error) => error.message), [
      "host closed",
      "host closed",
    ]);
    assertEquals([a, b].map((node) => formRoot(node).querySelector("form")), [null, null]);
  } finally {
    cleanup.dispose();
    a.remove();
    b.remove();
  }
}

async function readBytes(
  reader: ReadableStreamDefaultReader<Uint8Array>,
  expected: number[],
): Promise<void> {
  const bytes: number[] = [];
  while (bytes.length < expected.length) {
    const next = await reader.read();
    assert(!next.done, "The response ended before the expected bytes arrived");
    bytes.push(...next.value);
  }
  assertEquals(bytes, expected);
}

async function nativeFetch(api: OutputClient): Promise<void> {
  const origin = document.querySelector<HTMLMetaElement>('meta[name="fixture-origin"]')?.content;
  assert(origin);
  assert(new URL(origin).origin !== location.origin, "HTTP fixture must cross origins");
  const client = api.createPluginHttpClient(`${origin}/scoped/plugins/probe/`);
  const fetchers: import("../mod.ts").PluginFetch[] = [];
  const globals = globalThis as typeof globalThis & {
    maieuticsTestFetchers?: typeof fetchers;
  };
  globals.maieuticsTestFetchers = fetchers;
  const nodes = [element(), element()];
  const mounted: Array<ReturnType<OutputClient["mountVueIsland"]> | undefined> = [];
  const code =
    'return {setup(){globalThis.maieuticsTestFetchers.push(Maieutics.useFetch());return ()=>Vue.h("div","HTTP");}};';
  try {
    for (const node of nodes) {
      node.append(document.createElement("div"));
      mounted.push(api.mountVueIsland(node, code, undefined, client));
    }
    const [fetch, sibling] = fetchers;
    assertEquals(fetchers.length, 2, "Each Vue scope must receive its native Fetch capability");
    const echo = await fetch("/echo", {
      method: "POST",
      headers: { "content-type": "application/octet-stream" },
      body: new Uint8Array([0, 255, 128]),
    });
    assert(echo instanceof Response);
    assertEquals([...new Uint8Array(await echo.arrayBuffer())], [0, 255, 128]);

    const stream = (await fetch("/stream")).body?.getReader();
    assert(stream);
    try {
      await readBytes(stream, [0, 255]);
      await (await fetch("/release", { method: "POST" })).text();
      await readBytes(stream, [128]);
      assert((await stream.read()).done, "Stream did not end after release");
    } finally {
      stream.releaseLock();
    }

    const pending = (await fetch("/pending")).body?.getReader();
    assert(pending);
    try {
      await readBytes(pending, [42]);
      mounted[0]?.();
      mounted[0] = undefined;
      await assertRejects(() => pending.read(), DOMException, "abort");
    } finally {
      pending.releaseLock();
    }
    assertEquals(await (await sibling("/cancelled")).text(), "cancelled");
    assertEquals(await (await sibling("/api")).text(), "ok");
  } finally {
    for (const dispose of mounted) dispose?.();
    client.dispose();
    delete globals.maieuticsTestFetchers;
    for (const node of nodes) node.remove();
  }
}

/** Runs against the separately bundled shared client in Chromium. */
export async function runBrowserConformance(): Promise<string[]> {
  const artifact = "/outputClient.js";
  const api = await import(artifact) as OutputClient;
  await renderedOutput(api);
  await streamingRevisions(api);
  await pendingRender(api);
  await failedMarkdownPreparation(api);
  await snapshotInteractions(api);
  await toolDisclosure(api);
  await forms(api);
  await nativeFetch(api);
  return [
    "shared Markdown is inert and template, SSR, and Vue replies replace their addressed output",
    "streaming replies preserve append order, stay passive and respect rewrites and sealing",
    "pending Markdown preparation cannot repaint a newer or disposed output",
    "a failing host Markdown adapter leaves readable source and a categorized diagnostic",
    "SSR anchors and code tabs operate inside their addressed shadow boundary",
    "tool expansion survives updates and stays local to its output",
    "shared Vue forms retain focus, drafts, and scoped actions across updates and disposal",
    "native browser HTTP streams binary across origins and unmount cancels only the owning request",
  ];
}
