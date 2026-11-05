import * as vscode from "vscode";
import { FrontendClient } from "../../client.ts";
import { OutputRenderingBridge } from "../../outputRendering.ts";
import { emptyNotebook } from "../../notebookFormat.ts";
import { bundleItems, MaieuticsNotebookSerializer } from "../../serializer.ts";
import { TurnOutputMime } from "../../turnView.ts";

const VueDemoSource = `# Counter

<script setup lang="ts">
import { ref, onUnmounted } from 'vue';
const count = ref<number>(0);
onUnmounted(() => { globalThis.__maieuticsVueUnmounts = (globalThis.__maieuticsVueUnmounts ?? 0) + 1; });
</script>

<button class="counter-btn" @click="count++">Count</button><span class="counter-display">{{ count }}</span>

<style scoped>.counter-btn { color: rgb(255, 0, 0); padding: 6px 14px; }</style>`;

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
): Promise<void> {
  const settings = vscode.workspace.getConfiguration("maieutics");
  const previous = settings.get<string>("discoveryFile");
  const channel = vscode.notebooks.createRendererMessaging("maieutics-turn-renderer");
  const source = vue
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
          if (message.engine === (vue ? "vue" : "ssr")) resolve();
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
      !template && !vue &&
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
  const bridge = new OutputRenderingBridge({
    render: async (documentId, text, signal, template, vue) =>
      client ? await client.renderMarkdown(documentId, text, signal, template, vue) : vue
        ? {
          version: 1,
          documentId,
          html:
            `<div class="maieutics-output vp-doc" data-v-${documentId}><h1 data-v-${documentId}>Counter</h1><button class="counter-btn" data-v-${documentId}>Count</button><span class="counter-display" data-v-${documentId}>0</span></div>`,
          css: `.counter-btn[data-v-${documentId}] { color: rgb(255, 0, 0); padding: 6px 14px; }`,
          vue: {
            version: 1,
            scopeId: `data-v-${documentId}`,
            code:
              `const {ref,onUnmounted,h}=Vue;return {__scopeId:"data-v-${documentId}",setup(){const count=ref(0);onUnmounted(()=>{globalThis.__maieuticsVueUnmounts=(globalThis.__maieuticsVueUnmounts??0)+1});return()=>h("div",{class:"maieutics-output vp-doc"},[h("h1",null,"Counter"),h("button",{class:"counter-btn",onClick:()=>count.value++},"Count"),h("span",{class:"counter-display"},String(count.value))])}};`,
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
            `<div class="maieutics-output vp-doc"><nav class="table-of-contents"><a href="#${documentId}-contents">Contents</a></nav><h2 id="${documentId}-contents" tabindex="-1">Contents</h2><div class="custom-block note github-alert">Shared alert</div><div class="custom-block tip">Shared SSR</div>
          <fieldset class="vp-code-group"><legend>Code examples</legend>
          <div class="code-tab"><input type="radio" name="${documentId}-code-group-0" id="${documentId}-tab-0" checked><label for="${documentId}-tab-0">report.ts</label><div class="code-panel"><pre class="shiki"><code><span class="line highlighted" data-line="7">const total = 42;</span></code></pre></div></div>
          <div class="code-tab"><input type="radio" name="${documentId}-code-group-0" id="${documentId}-tab-1"><label for="${documentId}-tab-1">report.json</label><div class="code-panel"><pre class="shiki"><code>{ "total": 42 }</code></pre></div></div>
          </fieldset></div>`,
          css: `.maieutics-output { color: var(--vscode-editor-foreground); }
          .code-panel { display: none; } .code-tab > input:checked ~ .code-panel { display: block; }`,
        },
    post: async (message) => await panel.webview.postMessage(message),
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
        const api = await renderer.activate({
          getRenderer: async () => markdown,
          postMessage: (message) => vscode.postMessage(message),
          onDidReceiveMessage: (handler) => window.addEventListener("message", (event) => {
            handler(event.data);
            if (event.data.type === "rendered") delivered.resolve();
            if (event.data.type === "unavailable") delivered.reject(new Error("SSR snapshot unavailable"));
          })
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
        const vueElement = document.createElement("div"); document.body.appendChild(vueElement);
        const vueOutput = () => ({ id: "vue-demo", mime: "application/vnd.maieutics.vue+json", json: () => ({ version: 1, format: "markdown", source: ${
      JSON.stringify(VueDemoSource).replaceAll("<", String.fromCharCode(92) + "u003c")
    } }) });
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
        vscode.postMessage({ trace: "vue-mounted" });
        const clickCounter = async (root) => {
          const value = root.querySelector(".counter-display");
          assert(value?.textContent === "0", "the SSR counter starts at zero");
          root.querySelector(".counter-btn").click();
          await vueRuntime.vueUpdatesComplete();
          assert(value.textContent === "1", "Vue click events update reactive state");
        };
        assert(getComputedStyle(firstVue.querySelector(".counter-btn")).color === "rgb(255, 0, 0)", "compiled scoped CSS applies to hydrated nodes");
        await clickCounter(firstVue);
        vscode.postMessage({ trace: "vue-clicked" });
        const secondVue = await mountVue();
        assert(globalThis.__maieuticsVueUnmounts === 1, "source replacement unmounts the previous component exactly once");
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
