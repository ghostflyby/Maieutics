import * as vscode from "vscode";
import { FrontendClient } from "../../client.ts";
import { OutputRenderingBridge } from "../../outputRendering.ts";
import { emptyNotebook } from "../../notebookFormat.ts";
import {
  bundleItems,
  MaieuticsNotebookSerializer,
  timelineNotebookOutput,
} from "../../serializer.ts";
import { TurnOutputMime } from "../../turnView.ts";

const VueDemoSource = `---
title: Counter
initial: 0
---
# {{ $frontmatter.title }}

<script setup lang="ts">
import { ref, computed, onUnmounted } from 'vue';
const count = ref<number>(0);
const isZero = computed(() => count.value === 0);
const increase = () => { count.value++; };
const decrease = () => { if (!isZero.value) count.value--; };
const reset = () => { count.value = 0; };
onUnmounted(() => { globalThis.__maieuticsVueUnmounts = (globalThis.__maieuticsVueUnmounts ?? 0) + 1; });
</script>

<p><button class="counter-btn counter-decrease" @click="decrease" :disabled="isZero">−</button><button class="counter-btn counter-increase" @click="increase">+</button><span class="counter-display">{{ count + $frontmatter.initial }}</span><button class="counter-reset" @click="reset" :disabled="isZero">Reset</button></p>

<p class="counter-hint" v-if="isZero">Not clicked</p><p class="counter-hint" v-else>Clicked {{ count }} times</p>

<style scoped>.counter-btn { color: rgb(255, 0, 0); padding: 6px 14px; }</style>

$x^2$

~~~py
print(42)
~~~`;

const CodeDemoSource = [
  "# Demo",
  "",
  "[[toc]]",
  "",
  "## Contents {#contents}",
  "",
  "> [!NOTE]",
  "> Shared alert",
  "",
  "::: tip",
  "Shared SSR",
  ":::",
  "",
  "$x^2$",
  "",
  "::: code-group",
  "~~~ts:line-numbers=7 {2} [report.ts]",
  "// [!code highlight]",
  "const total = 42;",
  "~~~",
  "",
  "~~~json [report.json]",
  '{ "total": 42 }',
  "~~~",
  ":::",
].join("\n");

/** Exercises the production extension bridge and actual notebook MIME renderer, without a model call. */
export async function testNotebookSsrAsync(
  discovery: string,
  template = false,
  vue = false,
  assistant = false,
): Promise<void> {
  const settings = vscode.workspace.getConfiguration("maieutics");
  const previous = settings.get<string>("discoveryFile");
  const channel = vscode.notebooks.createRendererMessaging("maieutics-turn-renderer");
  const source = vue || assistant
    ? VueDemoSource
    : template
    ? "Total {{ report.total.toFixed(2) }}\n\nLater independent prose"
    : CodeDemoSource;
  const requests = new Set<string>();
  let timer: ReturnType<typeof setTimeout> | undefined;
  let subscription: vscode.Disposable | undefined;
  const cancellation = new vscode.CancellationTokenSource();
  try {
    const applied = new Promise<void>((resolve, reject) => {
      timer = setTimeout(
        () => reject(new Error("Actual notebook renderer did not acknowledge SSR application")),
        45_000,
      );
      subscription = channel.onDidReceiveMessage(({ message }) => {
        if (message?.source !== "maieutics-output") return;
        if (message.type === "render" && message.text === source) requests.add(message.requestId);
        if (message.type === "applied" && requests.has(message.requestId)) {
          if (message.engine === (vue || assistant ? "vue" : "ssr")) resolve();
          else {reject(
              new Error(
                "Actual notebook kept its native fallback instead of using the live renderer",
              ),
            );}
        }
      });
    });
    await settings.update("discoveryFile", discovery, vscode.ConfigurationTarget.Workspace);
    await vscode.commands.executeCommand("maieutics.newSession");
    const notebook = vscode.window.activeNotebookEditor?.notebook;
    if (notebook === undefined) {
      throw new Error("The real extension did not open its session notebook");
    }
    const snapshot = emptyNotebook();
    snapshot.cells[0].output = template
      ? {
        repl: [{
          displayId: "template-demo",
          data: {
            "application/vnd.maieutics.template+json": {
              version: 1,
              source,
              bindings: [{ name: "report", state: "ready", value: { total: 42 } }],
            },
            "text/plain": "Total 42.00",
          },
        }],
      }
      : { text: source, timeline: [{ kind: "text", messageId: "demo", text: source }] };
    const restored = new MaieuticsNotebookSerializer().deserializeNotebook(
      new TextEncoder().encode(JSON.stringify(snapshot)),
      cancellation.token,
    );
    if (
      !template && !vue && !assistant &&
      !restored.cells[0].outputs?.some((output) =>
        output.items.some((item) =>
          item.mime === TurnOutputMime &&
          JSON.parse(new TextDecoder().decode(item.data)).timeline?.[0]?.kind === "text"
        )
      )
    ) {
      throw new Error("The production serializer bypassed the SSR-capable assistant renderer");
    }
    if (
      template &&
      !restored.cells[0].outputs?.some((output) =>
        output.items.some((item) => item.mime === "application/vnd.maieutics.template+json")
      )
    ) throw new Error("The production serializer dropped the template MIME");
    if (vue) {
      // This is the production live-display factory used by the controller, not restored code.
      restored.cells[0].outputs = [
        new vscode.NotebookCellOutput(bundleItems({
          "application/vnd.maieutics.vue+json": { version: 1, format: "markdown", source },
          "text/plain": "Counter fallback",
        })),
      ];
    }
    if (assistant) {
      restored.cells[0].outputs = [timelineNotebookOutput({
        timeline: [{ kind: "text", messageId: "counter", text: source }],
      }, true)];
    }
    const edit = new vscode.WorkspaceEdit();
    edit.set(notebook.uri, [
      vscode.NotebookEdit.replaceCells(
        new vscode.NotebookRange(0, notebook.cellCount),
        restored.cells,
      ),
    ]);
    if (!await vscode.workspace.applyEdit(edit)) {
      throw new Error("Could not insert the demo snapshot");
    }
    await applied;
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    subscription?.dispose();
    cancellation.dispose();
    await vscode.commands.executeCommand("workbench.action.revertAndCloseActiveEditor");
    await settings.update("discoveryFile", previous, vscode.ConfigurationTarget.Workspace);
  }
}

/** Real webview/CSP integration; an optional discovery file also exercises a live kernel child. */
export async function testSsrRenderingAsync(): Promise<void> {
  const extension = vscode.extensions.getExtension("ghostflyby.maieutics-vscode");
  const markdown = vscode.extensions.getExtension("vscode.markdown-language-features");
  if (!extension || !markdown) throw new Error("Renderer extensions missing");
  const discovery =
    vscode.workspace.getConfiguration("maieutics").get<string>("testRenderingDiscovery") ??
      process.env.MAIEUTICS_RENDERING_DEMO_DISCOVERY;
  const client = discovery === undefined ? undefined : FrontendClient.fromDiscovery(JSON.parse(
    new TextDecoder().decode(await vscode.workspace.fs.readFile(vscode.Uri.file(discovery))),
  ));
  console.log(`SSR DOM probe: ${client ? "live kernel" : "snapshot fixture"}`);
  const panel = vscode.window.createWebviewPanel("maieutics-ssr-test", "SSR output demo", {
    viewColumn: vscode.ViewColumn.Beside,
    preserveFocus: true,
  }, { enableScripts: true, localResourceRoots: [extension.extensionUri, markdown.extensionUri] });
  const progressiveRender = (showDouble: boolean, showSection: boolean) =>
    `return function(_ctx,_cache,$props,$setup){const {h}=Vue;return h("div",{class:"maieutics-output vp-doc"},[h("button",{class:"progressive-counter",onClick:()=>{$setup.count++}},String($setup.count)),${
      showDouble ? 'h("p",{class:"progressive-double"},String($setup.doubled)),' : ""
    }${showSection ? 'h("section",null,String($setup.doubled)),' : ""}]);};`;
  let copiedText = "";
  const bridge = new OutputRenderingBridge({
    writeClipboard: (text) => {
      copiedText = text;
      return Promise.resolve();
    },
    render: async (documentId, text, signal, template, vue, assistant, progressive) =>
      client
        ? await client.renderMarkdown(
          documentId,
          text,
          signal,
          template,
          vue,
          assistant,
          progressive,
        )
        : progressive && text.includes("__progressiveSetups")
        ? (() => {
          const accepted = text.endsWith("<section>{{")
            ? text.slice(0, text.lastIndexOf("\n\n"))
            : text;
          const renderCode = progressiveRender(
            accepted.includes("progressive-double"),
            accepted.includes("</section>"),
          );
          const initial = text.includes("ref(9)") ? 9 : 0;
          return {
            version: 1 as const,
            documentId,
            renderedSource: accepted,
            html:
              `<div class="maieutics-output vp-doc"><button class="progressive-counter">${initial}</button></div>`,
            css: "",
            vue: {
              version: 1 as const,
              scopeId: `data-v-${documentId}`,
              setupKey: (initial ? "f" : "e").repeat(64),
              renderCode,
              code: progressive.compileOnly
                ? ""
                : `const {ref,computed,onUnmounted}=Vue;return {setup(){globalThis.__progressiveSetups++;const count=ref(${initial}),doubled=computed(()=>count.value*2);onUnmounted(()=>globalThis.__progressiveUnmounts++);return {count,doubled}},render:(()=>{${
                  progressiveRender(false, false)
                }})()};`,
            },
          };
        })()
        : (vue || assistant)
        ? {
          version: 1,
          documentId,
          html:
            `<div class="maieutics-output vp-doc" data-v-${documentId}><p data-v-${documentId}><button class="counter-btn counter-decrease" disabled data-v-${documentId}>−</button><button class="counter-btn counter-increase" data-v-${documentId}>+</button><span class="counter-display" data-v-${documentId}>0</span><button class="counter-reset" disabled data-v-${documentId}>Reset</button></p><p class="counter-hint" data-v-${documentId}>Not clicked</p></div>`,
          css: `.counter-btn[data-v-${documentId}] { color: rgb(255, 0, 0); padding: 6px 14px; }`,
          vue: {
            version: 1,
            scopeId: `data-v-${documentId}`,
            code:
              `const {ref,onUnmounted,h,computed}=Vue;return {__scopeId:"data-v-${documentId}",setup(){const count=ref(0),isZero=computed(()=>count.value===0);onUnmounted(()=>{globalThis.__maieuticsVueUnmounts=(globalThis.__maieuticsVueUnmounts??0)+1});return()=>h("div",{class:"maieutics-output vp-doc"},[h("p",null,[h("button",{class:"counter-btn counter-decrease",disabled:isZero.value,onClick:()=>{if(!isZero.value)count.value--}},"−"),h("button",{class:"counter-btn counter-increase",onClick:()=>count.value++},"+"),h("span",{class:"counter-display"},String(count.value)),h("button",{class:"counter-reset",disabled:isZero.value,onClick:()=>{count.value=0}},"Reset")]),h("p",{class:"counter-hint"},isZero.value?"Not clicked":"Clicked "+count.value+" times")])}};`,
          },
        }
        : template
        ? {
          version: 1,
          documentId,
          css: ".maieutics-output {}",
          html: `<div class="maieutics-output"><span data-template-state="${
            template.bindings[0]?.state === "ready" ? "ready" : "pending"
          }">${
            template.bindings[0]?.state === "ready" ? "42.00" : "…"
          }</span><p>Later independent prose</p></div>`,
        }
        : {
          version: 1,
          documentId,
          html:
            `<div class="maieutics-output vp-doc"><nav class="table-of-contents"><a href="#${documentId}-contents">Contents</a></nav><h2 id="${documentId}-contents" tabindex="-1">Contents</h2><div class="custom-block note github-alert">Shared alert</div><div class="custom-block tip">Shared SSR</div><span class="math-inline"><svg viewBox="0 0 10 10" role="img"><path d="M0 0L1 1"/></svg></span>
          <fieldset class="vp-code-group"><legend>Code examples</legend>
          <div class="code-tab"><input type="radio" name="${documentId}-code-group-0" id="${documentId}-tab-0" checked><label for="${documentId}-tab-0">report.ts</label><div class="code-panel"><div class="code-block"><button type="button" class="code-copy" data-code-copy="// [!code highlight]&#10;const total = 42;&#10;">复制</button><pre class="shiki"><code><span class="line highlighted" data-line="7">const total = 42;</span></code></pre></div></div></div>
          <div class="code-tab"><input type="radio" name="${documentId}-code-group-0" id="${documentId}-tab-1"><label for="${documentId}-tab-1">report.json</label><div class="code-panel"><pre class="shiki"><code>{ "total": 42 }</code></pre></div></div>
          </fieldset></div>`,
          diagnostics: text.includes("unclosed-demo")
            ? [{ code: "unclosed_code_fence" as const, line: 3, column: 1 }]
            : [],
          css: `.maieutics-output { color: var(--vscode-editor-foreground); }
          .code-panel { display: none; } .code-tab > input:checked ~ .code-panel { display: block; }`,
        },
    post: async (message) =>
      await panel.webview.postMessage(
        typeof message === "object" && message !== null && "type" in message &&
          message.type === "copy-result"
          ? { ...message, copiedText }
          : message,
      ),
    log: (message) => console.log(message),
  });
  let timer: ReturnType<typeof setTimeout> | undefined;
  let subscription: vscode.Disposable | undefined;
  let stage = "initial";
  try {
    const verdict = new Promise<void>((resolve, reject) => {
      timer = setTimeout(
        () => reject(new Error(`SSR DOM probe exceeded its completion budget at ${stage}`)),
        45_000,
      );
      subscription = panel.webview.onDidReceiveMessage((message) => {
        if (typeof message?.trace === "string") {
          stage = message.trace;
          return;
        }
        if (message?.source === "maieutics-output") {
          void bridge.handle(message).catch(reject);
        } else if (message?.ok === true) resolve();
        else reject(new Error(`SSR DOM probe: ${message?.error}`));
      });
    });
    const rendererUri = panel.webview.asWebviewUri(
      vscode.Uri.joinPath(extension.extensionUri, "media", "turnRenderer.js"),
    );
    const markdownUri = panel.webview.asWebviewUri(
      vscode.Uri.joinPath(markdown.extensionUri, "notebook-out", "index.js"),
    );
    panel.webview.html = `<!doctype html><html><head>
      <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src ${panel.webview.cspSource} 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline';">
      <style id="_defaultStyles">body { font-size: 13px; }</style></head><body>
      <script type="module">
      const vscode = acquireVsCodeApi();
      const assert = (condition, message) => { if (!condition) throw new Error(message); };
      try {
        const native = await import(${JSON.stringify(markdownUri.toString())});
        const renderer = await import(${JSON.stringify(rendererUri.toString())});
        const markdown = native.activate({ workspace: { isTrusted: true } });
        let delivered = Promise.withResolvers();
        let copied = Promise.withResolvers();
        let holdDelivery = false, heldMessage, deliver;
        let deliveryHeld = Promise.withResolvers();
        const api = await renderer.activate({
          getRenderer: async () => markdown,
          postMessage: (message) => vscode.postMessage(message),
          onDidReceiveMessage: (handler) => {
            deliver = (message) => {
              if (message.type === "copy-result") copied.resolve(message);
              Promise.resolve(handler(message)).then(() => {
                if (message.type === "rendered") delivered.resolve();
              }, delivered.reject);
              if (message.type === "unavailable") delivered.reject(new Error("SSR snapshot unavailable"));
            };
            window.addEventListener("message", (event) => {
              if (holdDelivery && event.data.type === "rendered") {
                heldMessage = event.data; deliveryHeld.resolve();
              } else deliver(event.data);
            });
          }
        });
        const element = document.createElement("div"); document.body.appendChild(element);
        const source = ${JSON.stringify(CodeDemoSource)};
        const vueRuntime = await import(new URL("./vueIslands.js", ${
      JSON.stringify(rendererUri.toString())
    }).href);
        await api.renderOutputItem({ id: "ssr-demo", json: () => ({ text: source }) }, element);
        await delivered.promise;
        const node = element.querySelector('[data-render-engine="ssr"]');
        assert(node, "the actual turn renderer switches to SSR");
        const root = node.shadowRoot ?? node;
        assert(root.querySelector(".custom-block.tip")?.textContent.includes("Shared SSR"), "SSR containers reach the browser");
        assert(root.querySelector(".math-inline svg path"), "offline SVG math reaches the browser without font downloads");
        assert(root.querySelector("pre.shiki code")?.textContent.includes("const total"), "SSR code output remains visible");
        assert(root.querySelector("style")?.textContent.includes("--vscode"), "host-scoped styles are delivered through messaging");
        assert(root.querySelector('.line.highlighted[data-line="7"]'), "line metadata reaches the actual browser");
        assert(root.querySelector(".github-alert.note")?.textContent.includes("Shared alert"), "upstream alert parsing reaches the browser");
        const tocLink = root.querySelector(".table-of-contents a");
        const heading = root.querySelector("h2");
        assert(tocLink?.getAttribute("href") === "#" + heading?.id, "TOC and custom anchors share the same document scope");
        let scrolled = 0;
        heading.scrollIntoView = () => { scrolled++; };
        tocLink.click();
        assert(scrolled === 1 && root.activeElement === heading, "TOC clicks scroll and focus inside the actual Markdown shadow root");
        const copyButton = root.querySelector("button.code-copy");
        assert(copyButton,"code fences expose an accessible copy button");
        copyButton.click();
        const copyResult = await copied.promise;
        assert(copyResult.ok && copyResult.copiedText === "// [!code highlight]\\nconst total = 42;\\n", "copy uses exact source without synthetic line numbers");
        assert(copyButton.textContent === "已复制" && !copyButton.disabled, "copy completion is acknowledged by the extension host");
        const labels = root.querySelectorAll(".code-tab > label");
        const panels = root.querySelectorAll(".code-panel");
        assert(labels.length === 2 && panels.length === 2, "the shared snapshot contains both code examples");
        assert(getComputedStyle(panels[0]).display !== "none" && getComputedStyle(panels[1]).display === "none", "native code groups show only the first example initially");
        labels[1].click();
        assert(getComputedStyle(panels[0]).display === "none" && getComputedStyle(panels[1]).display !== "none", "clicking the native label switches code without document scripts");
        delivered = Promise.withResolvers();
        await api.renderOutputItem({ id: "ssr-demo", json: () => ({ text: source + "\\n\\nLater prose" }) }, element);
        await delivered.promise;
        const updatedNode = element.querySelector('[data-render-engine="ssr"]');
        const updatedRoot = updatedNode.shadowRoot ?? updatedNode;
        tocLink.click();
        assert(scrolled === 1, "replacing a source revision detaches its old anchor handler");
        const updatedPanels = updatedRoot.querySelectorAll(".code-panel");
        assert(getComputedStyle(updatedPanels[0]).display === "none" && getComputedStyle(updatedPanels[1]).display !== "none", "an SSR source update retains the user's selected code example");
        const diagnosticElement = document.createElement("div"); document.body.appendChild(diagnosticElement);
        delivered = Promise.withResolvers();
        await api.renderOutputItem({id:"diagnostic-demo",json:()=>({text:"# Source\\n\\n~~~md\\nunclosed-demo"})},diagnosticElement);
        await delivered.promise;
        const diagnosticNode = diagnosticElement.querySelector('[data-render-engine="ssr"]');
        const diagnosticRoot = diagnosticNode.shadowRoot ?? diagnosticNode;
        assert(diagnosticRoot.querySelector('[data-component-diagnostic]')?.textContent.includes("第 3 行"),"sealed fence diagnostics display original source coordinates");
        api.disposeOutputItem("diagnostic-demo");
        const templateElement = document.createElement("div"); document.body.appendChild(templateElement);
        const templateSource = "Total {{ report.total.toFixed(2) }}\\n\\nLater independent prose";
        const templateItem = (binding) => ({ id: "binding-demo", mime: "application/vnd.maieutics.template+json", json: () => ({ version: 1, source: templateSource, bindings: [binding] }) });
        delivered = Promise.withResolvers();
        await api.renderOutputItem(templateItem({ name: "report", state: "pending" }), templateElement);
        await delivered.promise;
        const pendingTemplate = templateElement.querySelector('[data-render-engine="ssr"]');
        const pendingRoot = pendingTemplate.shadowRoot ?? pendingTemplate;
        assert(pendingRoot.querySelector('[data-template-state="pending"]') && pendingRoot.textContent.includes("Later independent prose"), "a template placeholder does not hide later content");
        delivered = Promise.withResolvers();
        await api.renderOutputItem(templateItem({ name: "report", state: "ready", value: { total: 42 } }), templateElement);
        await delivered.promise;
        const readyTemplate = templateElement.querySelector('[data-render-engine="ssr"]');
        const readyRoot = readyTemplate.shadowRoot ?? readyTemplate;
        assert(readyRoot.querySelector('[data-template-state="ready"]')?.textContent === "42.00" && !readyRoot.querySelector('[data-template-state="pending"]'), "a binding-only update resolves earlier interpolation through the live kernel");
        assert(readyRoot.textContent.includes("Later independent prose"), "later content survives a binding-only update");
        const streamingElement = document.createElement("div"); document.body.appendChild(streamingElement);
        globalThis.__progressiveSetups = 0; globalThis.__progressiveUnmounts = 0;
        const setupSource = '\\u003cscript setup lang="ts">\\nimport {ref,computed,onUnmounted} from "vue";\\nglobalThis.__progressiveSetups++;\\nconst count=ref(0); const doubled=computed(()=>count.value*2);\\nonUnmounted(()=>globalThis.__progressiveUnmounts++);\\n\\u003c/script>\\n\\n';
        let streamingSource = setupSource + '<button class="progressive-counter" @click="count++">{{ count }}</button>\\n\\n$x^2$\\n\\n~~~py\\nprint(42)\\n~~~';
        const paintStreaming = async (streaming = true) => {
          delivered = Promise.withResolvers();
          await api.renderOutputItem({id:"streaming-vue",mime:"application/vnd.maieutics.live-assistant+json",json:()=>({text:streamingSource,streaming})},streamingElement);
          await delivered.promise;
          await vueRuntime.vueUpdatesComplete();
          const boundary = streamingElement.querySelector('[data-render-engine="vue"]');
          assert(boundary,"streaming complete setup mounts before reply completion");
          return boundary.shadowRoot ?? boundary;
        };
        const streamingRoot = await paintStreaming();
        if (${client !== undefined}) {
          assert(streamingRoot.querySelector(".math-inline svg path"),"progressive Vue hydrates SVG math without losing its namespace");
          copied = Promise.withResolvers();
          streamingRoot.querySelector("button.code-copy").click();
          const liveCopy = await copied.promise;
          assert(liveCopy.ok && liveCopy.copiedText === "print(42)\\n","copy controls also work inside a retained live Vue template");
        }
        const originalButton = streamingRoot.querySelector(".progressive-counter");
        originalButton.click(); await vueRuntime.vueUpdatesComplete();
        assert(originalButton.textContent === "1", "the first streaming component is interactive");
        streamingSource += '\\n\\n<p class="progressive-double">{{ doubled }}</p>';
        await paintStreaming();
        assert(streamingRoot.querySelector(".progressive-counter") === originalButton && originalButton.textContent === "1", "a later template preserves the existing DOM and ref state");
        assert(streamingRoot.querySelector(".progressive-double").textContent === "2", "later content reads the same computed and ref bindings");
        streamingSource += '\\n\\n<section>{{';
        await paintStreaming();
        assert(originalButton.isConnected && globalThis.__progressiveSetups === 1, "incomplete trailing syntax retains the live component without rerunning setup");
        originalButton.click(); await vueRuntime.vueUpdatesComplete();
        streamingSource += ' doubled }}</section>';
        await paintStreaming();
        assert(streamingRoot.querySelector("section").textContent === "4", "new source reads current client state rather than a new SSR initial value");
        await paintStreaming(false);
        assert(globalThis.__progressiveSetups === 1 && globalThis.__progressiveUnmounts === 0 && originalButton.textContent === "2", "successful sealing preserves the mounted setup scope");
        streamingSource += '\\n\\n<section>{{';
        await paintStreaming(false);
        assert(streamingRoot.querySelector('[data-component-diagnostic]') && originalButton.textContent === "2" && globalThis.__progressiveSetups === 1, "a sealed incomplete template reports a local diagnostic without resetting existing state");
        streamingSource = streamingSource.slice(0,streamingSource.lastIndexOf('\\n\\n')).replace("ref(0)","ref(9)");
        const rewrittenRoot = await paintStreaming();
        assert(rewrittenRoot.querySelector(".progressive-counter").textContent === "9" && globalThis.__progressiveSetups === 2 && globalThis.__progressiveUnmounts === 1, "an actual setup rewrite starts a new owned scope exactly once");
        api.disposeOutputItem("streaming-vue");
        assert(globalThis.__progressiveUnmounts === 2, "disposing streaming output stops its setup effects exactly once");
        const parallelElement = document.createElement("div"); document.body.appendChild(parallelElement);
        parallelElement.innerHTML = "<p>0</p>";
        globalThis.__parallelSetups = 0;
        const parallelKey = "b".repeat(64);
        const parallel = vueRuntime.mountVueIsland(parallelElement, 'return {setup(){globalThis.__parallelSetups++;return {value:Vue.ref(0)}},render(_ctx,_cache,$props,$setup){return Vue.h("p",String($setup.value))}};', {setupKey:parallelKey,renderCode:"unused"});
        const olderUpdate = parallel.update(parallelKey, 'return function(_ctx,_cache,$props,$setup){return Vue.h("p",String($setup.value)+" older")}');
        const newerUpdate = parallel.update(parallelKey, 'return function(_ctx,_cache,$props,$setup){return Vue.h("p",String($setup.value)+" newer")}');
        const appliedUpdates = await Promise.all([olderUpdate,newerUpdate]);
        assert(appliedUpdates[0] === false && appliedUpdates[1] === true && parallelElement.textContent === "0 newer", "batched template jobs commit only the latest render function");
        const badUpdate = await parallel.update(parallelKey,'return function(){throw new Error("fixture") }').then(()=>null,error=>error);
        assert(badUpdate && parallelElement.textContent === "0 newer" && globalThis.__parallelSetups === 1, "failed render updates recover the last committed function without repeating setup");
        const disposingUpdate = parallel.update(parallelKey,'return function(){return Vue.h("p","disposed update")}');
        parallel();
        assert(await disposingUpdate === false && parallelElement.dataset.vueState === "disposed", "disposal invalidates pending template flushes");
        const vueElement = document.createElement("div"); document.body.appendChild(vueElement);
        let vueSource = ${
      JSON.stringify(VueDemoSource).replaceAll("<", String.fromCharCode(92) + "u003c")
    };
        const vueOutput = () => ({ id: "vue-demo", mime: "application/vnd.maieutics.live-assistant+json", json: () => ({ text: vueSource }) });
        globalThis.__maieuticsVueUnmounts = 0;
        const mountVue = async () => {
          delivered = Promise.withResolvers();
          await api.renderOutputItem(vueOutput(), vueElement);
          await delivered.promise;
          const mounted = vueElement.querySelector('[data-render-engine="vue"]');
          assert(mounted, "kernel SSR is hydrated by the actual Vue runtime");
          return mounted.shadowRoot ?? mounted;
        };
        vscode.postMessage({ trace: "vue-request" });
        const firstVue = await mountVue();
        if (${
      client !== undefined
    }) assert(firstVue.querySelector(".math-inline svg path") && firstVue.querySelector("button.code-copy"),"completed Vue hydrates the same math and code content as passive Markdown");
        vscode.postMessage({ trace: "vue-mounted" });
        const clickCounter = async (root) => {
          const value = root.querySelector(".counter-display");
          assert(value?.textContent === "0", "the SSR counter starts at zero");
          assert(root.querySelector(".counter-decrease").disabled && root.querySelector(".counter-reset").disabled, "computed bindings disable decrement and reset at zero");
          root.querySelector(".counter-decrease").click();
          assert(value.textContent === "0", "disabled decrement cannot produce a negative count");
          root.querySelector(".counter-increase").click();
          await vueRuntime.vueUpdatesComplete();
          assert(value.textContent === "1", "Vue click events update reactive state");
          assert(!root.querySelector(".counter-decrease").disabled && root.querySelector(".counter-hint").textContent.includes("Clicked 1"), "computed and v-if branches react to a click");
          root.querySelector(".counter-reset").click(); await vueRuntime.vueUpdatesComplete();
          assert(value.textContent === "0" && root.querySelector(".counter-hint").textContent === "Not clicked", "reset restores count and the conditional hint");
          root.querySelector(".counter-increase").click(); await vueRuntime.vueUpdatesComplete();
        };
        assert(getComputedStyle(firstVue.querySelector(".counter-btn")).color === "rgb(255, 0, 0)", "compiled scoped CSS applies to hydrated nodes");
        await clickCounter(firstVue);
        vscode.postMessage({ trace: "vue-clicked" });
        const beforeDuplicate = vueElement.querySelector('[data-render-engine="vue"]');
        await api.renderOutputItem(vueOutput(), vueElement);
        assert(vueElement.querySelector('[data-render-engine="vue"]') === beforeDuplicate && globalThis.__maieuticsVueUnmounts === 0, "duplicate delivery retains the live Vue app");
        assert(firstVue.querySelector(".counter-display").textContent === "1", "duplicate delivery preserves client state");
        vueSource += "\\n\\nUpdated prose";
        holdDelivery = true;
        deliveryHeld = Promise.withResolvers();
        const replacingVue = mountVue();
        await deliveryHeld.promise;
        assert(beforeDuplicate.isConnected && globalThis.__maieuticsVueUnmounts === 0, "an outstanding SSR replacement keeps the existing Vue island mounted");
        firstVue.querySelector(".counter-increase").click();
        await vueRuntime.vueUpdatesComplete();
        assert(firstVue.querySelector(".counter-display").textContent === "2", "the retained Vue island remains interactive while SSR is pending");
        holdDelivery = false; deliver(heldMessage);
        const secondVue = await replacingVue;
        assert(globalThis.__maieuticsVueUnmounts === 1, "completed legacy source replacement unmounts its prior instance once");
        secondVue.querySelector(".counter-reset").click(); await vueRuntime.vueUpdatesComplete();
        await clickCounter(secondVue);
        api.disposeOutputItem("vue-demo");
        assert(globalThis.__maieuticsVueUnmounts === 2, "output disposal unmounts Vue effects exactly once");
        const finalLink = updatedRoot.querySelector(".table-of-contents a");
        const finalHeading = updatedRoot.querySelector("h2");
        let afterDisposal = 0;
        finalHeading.scrollIntoView = () => { afterDisposal++; };
        api.disposeOutputItem();
        finalLink.click();
        assert(afterDisposal === 0, "disposing output detaches its fragment navigation handler");
        vscode.postMessage({ ok: true });
      } catch (error) { vscode.postMessage({ ok: false, error: error.stack ?? String(error) }); }
      </script></body></html>`;
    await verdict;
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    subscription?.dispose();
    await bridge.dispose();
    panel.dispose();
  }
}
