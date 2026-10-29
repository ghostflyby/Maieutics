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
        const api = await module.activate({ getRenderer: async (id) => {
          assert(id === "vscode.markdown-it-renderer", "use the native Markdown renderer");
          return markdown;
        }, postMessage: (message) => messages.push(message) });
        const answer = "# Answer\\n\\n| Key | Value |\\n| --- | --- |\\n| a | **bold** |\\n\\n1. outer\\n   - nested\\n\\n~~~ts\\nconst answer = 42;\\n~~~\\n\\n[reference][ref]\\n\\n[ref]: https://example.com";
        const snapshot = { text: answer, tools: [{ tool: "list_directory", status: "ok" }],
          reasoning: [{ messageId: "m1", text: "First summary" }, { messageId: "m2", text: "Second summary" }],
          timeline: [{ kind: "reasoning", messageId: "m1" }, { kind: "text", messageId: "m1", text: "Checking files" },
            { kind: "tool", index: 0 }, { kind: "reasoning", messageId: "m2" }, { kind: "text", messageId: "m2", text: answer }] };
        const element = document.createElement("div"); document.body.appendChild(element);
        const output = { id: "test-turn", json: () => snapshot };
        await api.renderOutputItem(output, element, new AbortController().signal);
        const rows = [...element.querySelectorAll("details.timeline-node")];
        assert(rows.length === 5, "every message, summary and tool has its own collapsed node");
        assert(rows.every((row) => !row.open), "all nodes default to compact collapsed form");
        assert(!element.querySelector("[data-maieutics-view]"), "there is no alternate rendering mode");
        assert(rows[0].querySelector("summary").textContent.includes("First summary") && rows[2].querySelector("summary").textContent.includes("list_directory"), "node summaries follow timeline order");
        assert(rows[4].querySelector("[data-markdown-index]").shadowRoot === null, "collapsed Markdown is rendered lazily");
        const setOpen = async (detail, value) => {
          const toggle = new Promise((resolve) => detail.addEventListener("toggle", resolve, { once: true }));
          detail.open = value;
          await toggle;
        };
        await setOpen(rows[4], true);
        const finalMarkdown = rows[4].querySelector("[data-markdown-index]").shadowRoot;
        assert(finalMarkdown.querySelector("table") && finalMarkdown.querySelector("ol ul"), "expanded original preserves tables and nested lists");
        const baseline = document.createElement("div"); document.body.appendChild(baseline);
        markdown.renderOutputItem(module.markdownOutputItem(output, answer, 9), baseline);
        const baselineHtml = baseline.shadowRoot.getElementById("preview").innerHTML;
        assert(finalMarkdown.getElementById("preview").innerHTML === baselineHtml, "expanded answer matches native Markdown exactly");
        await setOpen(rows[0], true);
        await api.renderOutputItem(output, element, new AbortController().signal);
        const repainted = [...element.querySelectorAll("details.timeline-node")];
        assert(repainted[0].open && repainted[4].open && !repainted[2].open, "each node retains its own expanded state across output updates");
        assert(repainted[4].querySelector("[data-markdown-index]").shadowRoot.getElementById("preview").innerHTML === baselineHtml, "expanded content survives streaming repaint");
        await setOpen(repainted[4], false);
        await api.renderOutputItem(output, element, new AbortController().signal);
        assert(!element.querySelector('[data-node-id="entry:4"]').open, "explicit collapse survives repaint");
        const separate = document.createElement("div"); document.body.appendChild(separate);
        const separateOutput = { id: "separate-tool", json: () => ({ nodeId: "entry:0", timeline: [{ kind: "tool", index: 0 }], tools: [{ tool: "read_file", status: "ok", arguments: { path: "private.ts" }, result: { status: "ok", value: "original file content" } }] }) };
        await api.renderOutputItem(separateOutput, separate);
        assert(separate.querySelectorAll("details.timeline-node").length === 1 && !separate.querySelector("details").open, "separate output blocks default to collapsed even when another node is open");
        await setOpen(separate.querySelector("details"), true);
        assert(separate.querySelector(".node-body").textContent.includes("original file content"), "expanded tool node preserves its original structured result");
        await api.renderOutputItem(output, element);
        assert(!element.querySelector('[data-node-id="entry:2"]').open, "opening a standalone output never expands a sibling");
        const hostile = document.createElement("div"); document.body.appendChild(hostile);
        markdown.renderOutputItem(module.markdownOutputItem(output, '<img src=x onerror="alert(1)">\\n\\n<script>alert(2)<\\/script>\\n\\n[run](command:evil)', 10), hostile);
        const preview = hostile.shadowRoot.getElementById("preview");
        assert(!preview.querySelector("img, script, a[href^='command:']"), "untrusted HTML and executable links stay inert in trusted workspaces");
        const retry = document.createElement("div"); document.body.appendChild(retry);
        await api.renderOutputItem({ id: "retry", json: () => ({ text: '[Retry](command:maieutics.retryTurn?' + encodeURIComponent(JSON.stringify(["notebook:test", 3])) + ')', error: { code: "failed", message: "Failure" } }) }, retry);
        await setOpen(retry.querySelector("details.timeline-node"), true);
        retry.querySelector("[data-retry]").click();
        assert(messages.length === 1 && messages[0].notebookUri === "notebook:test" && messages[0].cellIndex === 3, "retry uses the owning renderer context and JSON arguments");
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
