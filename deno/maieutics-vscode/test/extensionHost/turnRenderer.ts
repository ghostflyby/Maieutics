import * as vscode from "vscode";

/** Runs the shipped renderer against VS Code's real Markdown implementation and DOM. */
export async function testTurnRendererAsync(): Promise<void> {
  const extension = vscode.extensions.getExtension("ghostflyby.maieutics-vscode");
  const markdown = vscode.extensions.getExtension("vscode.markdown-language-features");
  if (extension === undefined || markdown === undefined) {
    throw new Error("Renderer extensions missing");
  }
  const panel = vscode.window.createWebviewPanel(
    "maieutics-renderer-test",
    "Renderer regression",
    { viewColumn: vscode.ViewColumn.Beside, preserveFocus: true },
    { enableScripts: true, localResourceRoots: [extension.extensionUri, markdown.extensionUri] },
  );
  let timer: ReturnType<typeof setTimeout> | undefined;
  let subscription: vscode.Disposable | undefined;
  try {
    const verdict = new Promise<void>((resolve, reject) => {
      timer = setTimeout(
        () => reject(new Error("Renderer DOM test did not report completion")),
        20_000,
      );
      subscription = panel.webview.onDidReceiveMessage((message) => {
        if (message.ok === true) resolve();
        else reject(new Error(`Renderer DOM test: ${message.error}`));
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
        const module = await import(${JSON.stringify(rendererUri.toString())});
        const markdown = native.activate({ workspace: { isTrusted: true } });
        const messages = [];
        let receiveMessage;
        const api = await module.activate({ getRenderer: async (id) => {
          assert(id === "vscode.markdown-it-renderer", "use the native Markdown renderer");
          return markdown;
        }, postMessage: (message) => messages.push(message),
          onDidReceiveMessage: (handler) => { receiveMessage = handler; } });
        const answer = "# Answer\\n\\n| Key | Value |\\n| --- | --- |\\n| a | **bold** |";
        const output = { id: "test-turn", json: () => ({ text: answer }) };
        const element = document.createElement("div"); document.body.appendChild(element);
        await api.renderOutputItem(output, element, new AbortController().signal);
        const preview = element.querySelector("[data-markdown-index]").shadowRoot.getElementById("preview");
        const baseline = document.createElement("div"); document.body.appendChild(baseline);
        markdown.renderOutputItem(module.markdownOutputItem(output, answer, 9), baseline);
        assert(preview.querySelector("table") && preview.innerHTML === baseline.shadowRoot.getElementById("preview").innerHTML, "the notebook uses the real native Markdown renderer");

        const request = messages.find((message) => message.type === "render");
        assert(request?.text === answer, "the notebook adapter forwards the native output to SSR");
        receiveMessage({ ...request, type: "rendered", html: "<p>Renderer reply</p>", css: "" });
        assert(element.querySelector('[data-render-engine="ssr"]').shadowRoot.textContent.includes("Renderer reply"), "the adapter applies the addressed SSR response");

        const retry = document.createElement("div"); document.body.appendChild(retry);
        await api.renderOutputItem({ id: "retry", json: () => ({ text: '[Retry](command:maieutics.retryTurn?' + encodeURIComponent(JSON.stringify(["notebook:test", 3])) + ')', error: { code: "failed", message: "Failure" } }) }, retry);
        retry.querySelector("[data-retry]").click();
        const retries = messages.filter((message) => message.type === "retry");
        assert(retries.length === 1 && retries[0].notebookUri === "notebook:test" && retries[0].cellIndex === 3, "retry uses the owning renderer context and JSON arguments");

        api.disposeOutputItem();
        vscode.postMessage({ ok: true });
      } catch (error) { vscode.postMessage({ ok: false, error: error.stack ?? String(error) }); }
      </script></body></html>`;
    await verdict;
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    subscription?.dispose();
    panel.dispose();
  }
}
