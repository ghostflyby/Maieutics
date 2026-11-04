import * as vscode from "vscode";
import { FrontendClient } from "../../client.ts";
import { OutputRenderingBridge } from "../../outputRendering.ts";
import { emptyNotebook } from "../../notebookFormat.ts";
import { MaieuticsNotebookSerializer } from "../../serializer.ts";
import { TurnOutputMime } from "../../turnView.ts";

/** Exercises the production extension bridge and actual notebook MIME renderer, without a model call. */
export async function testNotebookSsrAsync(discovery: string): Promise<void> {
  const settings = vscode.workspace.getConfiguration("maieutics");
  const previous = settings.get<string>("discoveryFile");
  const channel = vscode.notebooks.createRendererMessaging("maieutics-turn-renderer");
  const source =
    "# Actual Notebook SSR\n\n::: tip\nLive kernel rendering demo\n:::\n\n```ts\nconst total = 42;\n```";
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
          if (message.engine === "ssr") resolve();
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
    snapshot.cells[0].output = {
      text: source,
      timeline: [{ kind: "text", messageId: "demo", text: source }],
    };
    const restored = new MaieuticsNotebookSerializer().deserializeNotebook(
      new TextEncoder().encode(JSON.stringify(snapshot)),
      cancellation.token,
    );
    if (
      !restored.cells[0].outputs?.some((output) =>
        output.items.some((item) =>
          item.mime === TurnOutputMime &&
          JSON.parse(new TextDecoder().decode(item.data)).timeline?.[0]?.kind === "text"
        )
      )
    ) {
      throw new Error("The production serializer bypassed the SSR-capable assistant renderer");
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
    render: async (documentId, text, signal) =>
      client ? await client.renderMarkdown(documentId, text, signal) : {
        version: 1,
        documentId,
        html:
          '<div class="maieutics-output vp-doc"><div class="custom-block tip">Shared SSR</div><pre class="shiki"><code>const total = 42;</code></pre></div>',
        css: ".maieutics-output { color: var(--vscode-editor-foreground); }",
      },
    post: async (message) => await panel.webview.postMessage(message),
    log: (message) => console.log(message),
  });
  let timer: ReturnType<typeof setTimeout> | undefined;
  let subscription: vscode.Disposable | undefined;
  try {
    const verdict = new Promise<void>((resolve, reject) => {
      timer = setTimeout(
        () => reject(new Error("SSR DOM probe exceeded its completion budget")),
        45_000,
      );
      subscription = panel.webview.onDidReceiveMessage((message) => {
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
      <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src ${panel.webview.cspSource} 'unsafe-inline'; style-src 'unsafe-inline';">
      <style id="_defaultStyles">body { font-size: 13px; }</style></head><body>
      <script type="module">
      const vscode = acquireVsCodeApi();
      const assert = (condition, message) => { if (!condition) throw new Error(message); };
      try {
        const native = await import(${JSON.stringify(markdownUri.toString())});
        const renderer = await import(${JSON.stringify(rendererUri.toString())});
        const markdown = native.activate({ workspace: { isTrusted: true } });
        const delivered = Promise.withResolvers();
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
        const source = "# Demo\\n\\n::: tip\\nShared SSR\\n:::\\n\\n~~~ts\\nconst total = 42;\\n~~~";
        await api.renderOutputItem({ id: "ssr-demo", json: () => ({ text: source }) }, element);
        await delivered.promise;
        const node = element.querySelector('[data-render-engine="ssr"]');
        assert(node, "the actual turn renderer switches to SSR");
        const root = node.shadowRoot ?? node;
        assert(root.querySelector(".custom-block.tip")?.textContent.includes("Shared SSR"), "SSR containers reach the browser");
        assert(root.querySelector("pre.shiki code")?.textContent.includes("const total"), "SSR code output remains visible");
        assert(root.querySelector("style")?.textContent.includes("--vscode"), "host-scoped styles are delivered through messaging");
        api.disposeOutputItem();
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
