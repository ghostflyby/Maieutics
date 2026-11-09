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
        const answer = "# Answer\\n\\n| Key | Value |\\n| --- | --- |\\n| a | **bold** |\\n\\n1. outer\\n   - nested\\n\\n~~~ts\\nconst answer = 42;\\n~~~\\n\\n[reference][ref]\\n\\n[ref]: https://example.com";
        const snapshot = { text: answer, tools: [{ tool: "list_directory", status: "ok" }],
          reasoning: [{ messageId: "m1", text: "First summary" }, { messageId: "m2", text: "Second summary" }],
          timeline: [{ kind: "reasoning", messageId: "m1" }, { kind: "text", messageId: "m1", text: "Checking files" },
            { kind: "tool", index: 0 }, { kind: "reasoning", messageId: "m2" }, { kind: "text", messageId: "m2", text: answer }] };
        const element = document.createElement("div"); document.body.appendChild(element);
        const output = { id: "test-turn", json: () => snapshot };
        await api.renderOutputItem(output, element, new AbortController().signal);
        const rows = [...element.querySelectorAll("details.timeline-node")];
        assert(rows.length === 3 && rows.every((row) => !row.open), "activity remains collapsed");
        assert(!element.querySelector("[data-maieutics-view]"), "there is no mode toggle");
        const replies = [...element.querySelectorAll(".assistant-reply")];
        assert(replies.length === 2 && replies.every((reply) => !reply.closest("details")), "assistant replies occupy the full output without collapse wrappers");
        const finalMarkdown = replies[1].querySelector("[data-markdown-index]").shadowRoot;
        assert(finalMarkdown.querySelector("table") && finalMarkdown.querySelector("ol ul"), "visible replies use native Markdown");
        const baseline = document.createElement("div"); document.body.appendChild(baseline);
        markdown.renderOutputItem(module.markdownOutputItem(output, answer, 9), baseline);
        assert(finalMarkdown.getElementById("preview").innerHTML === baseline.shadowRoot.getElementById("preview").innerHTML, "reply matches the default Markdown renderer");
        const streamed = document.createElement("div"); document.body.appendChild(streamed);
        const progressive = document.createElement("div"); document.body.appendChild(progressive);
        const partialVue = "# Before\\n\\n<div>\\n## Independent Markdown\\n\\n<script setup>\\nconst count =";
        const progressOutput = (text) => ({ id: "progressive", json: () => ({ text, streaming: true }) });
        await api.renderOutputItem(progressOutput(partialVue), progressive);
        const prefixRequest = messages.at(-1);
        const previewRoot = progressive.querySelector("[data-markdown-index]").shadowRoot;
        assert(previewRoot.querySelector("h2")?.textContent.includes("Independent Markdown"), "an incomplete Vue/HTML block cannot consume independent native Markdown");
        assert(prefixRequest.streaming === true && !prefixRequest.assistant && !prefixRequest.vue, "streaming snapshots use only the passive lane");
        const secondSource = partialVue + " ref(0)";
        await api.renderOutputItem(progressOutput(secondSource), progressive);
        const secondRequest = messages.at(-1);
        receiveMessage({ ...prefixRequest, type: "rendered", renderedSource: partialVue, html: "<h1>First prefix</h1>", css: "" });
        const progressNode = progressive.querySelector('[data-render-engine="ssr"]');
        assert(progressNode && (progressNode.shadowRoot ?? progressNode).textContent.includes("First prefix"), "a completed append-only prefix commits before the newest request finishes");
        const thirdSource = secondSource + ";";
        await api.renderOutputItem(progressOutput(thirdSource), progressive);
        const thirdRequest = messages.at(-1);
        receiveMessage({ ...secondRequest, type: "rendered", renderedSource: secondSource, html: "<h2>Second prefix</h2>", css: "" });
        assert(progressive.querySelector('[data-render-engine="ssr"]') === progressNode && (progressNode.shadowRoot ?? progressNode).textContent.includes("Second prefix"), "continued streaming updates the same committed boundary");
        receiveMessage({ ...prefixRequest, type: "rendered", renderedSource: partialVue, html: "<p>Regressed prefix</p>", css: "" });
        assert(!(progressNode.shadowRoot ?? progressNode).textContent.includes("Regressed prefix"), "late earlier prefixes cannot regress rendered progress");
        receiveMessage({ ...thirdRequest, type: "rendered", renderedSource: thirdSource, html: "<p>Unsafe preview</p>", css: "", vue: { version: 1, code: "globalThis.__unsafeStreamingVue = true", scopeId: "data-v-" + thirdRequest.documentId } });
        assert(!globalThis.__unsafeStreamingVue && !(progressNode.shadowRoot ?? progressNode).textContent.includes("Unsafe preview"), "streaming progress can never execute a component factory");
        await api.renderOutputItem(progressOutput("Rewritten source"), progressive);
        await api.renderOutputItem(progressOutput(thirdSource + " new epoch"), progressive);
        receiveMessage({ ...thirdRequest, type: "rendered", renderedSource: thirdSource, html: "<p>Old epoch</p>", css: "" });
        assert(!(progressNode.shadowRoot ?? progressNode).textContent.includes("Old epoch"), "a rewrite invalidates ancestors even when new text later shares their prefix again");
        await api.renderOutputItem({ id: "progressive", mime: "application/vnd.maieutics.live-assistant+json", json: () => ({ text: "Sealed answer" }) }, progressive);
        const sealedRequest = messages.at(-1);
        receiveMessage({ ...thirdRequest, type: "rendered", renderedSource: thirdSource, html: "<p>Late passive preview</p>", css: "" });
        assert(!progressive.textContent.includes("Late passive preview"), "sealing invalidates every passive prefix");
        receiveMessage({ ...sealedRequest, type: "rendered", html: "<p>Sealed SSR</p>", css: "" });
        assert((progressive.querySelector('[data-render-engine="ssr"]').shadowRoot ?? progressive).textContent.includes("Sealed SSR"), "sealed requests retain exact-revision rendering");
        api.disposeOutputItem("progressive");
        const streamedOutput = (text) => ({ id: "streamed", json: () => ({ text }) });
        await api.renderOutputItem(streamedOutput("old source"), streamed);
        const oldRequest = messages.at(-1);
        await api.renderOutputItem(streamedOutput("new source"), streamed);
        const newRequest = messages.at(-1);
        assert(oldRequest.documentId === newRequest.documentId && oldRequest.requestId !== newRequest.requestId, "streamed text keeps a stable document identity and advances its request revision");
        receiveMessage({ ...oldRequest, type: "rendered", html: "<p>obsolete result</p>", css: "" });
        assert(!streamed.querySelector('[data-render-engine="ssr"]'), "late results cannot replace a newer source revision");
        receiveMessage({ ...newRequest, type: "rendered", html: '<div class="maieutics-output">new SSR result</div>', css: ".maieutics-output {}" });
        const ssrNode = streamed.querySelector('[data-render-engine="ssr"]');
        assert((ssrNode.shadowRoot ?? ssrNode).textContent.includes("new SSR result"), "the matching SSR result replaces the fallback");
        const beforeDuplicate = messages.length;
        await api.renderOutputItem(streamedOutput("new source"), streamed);
        assert(messages.length === beforeDuplicate && ssrNode.isConnected, "identical output neither re-requests SSR nor rebuilds the committed boundary");
        const retainedContent = streamed.querySelector(".maieutics-turn");
        const retainedRoot = ssrNode.shadowRoot ?? ssrNode;
        const retainedParagraph = retainedRoot.querySelector("p, .maieutics-output");
        const fallbackStarted = Promise.withResolvers();
        const releaseFallback = Promise.withResolvers();
        const nativeRender = markdown.renderOutputItem;
        markdown.renderOutputItem = async (...args) => {
          fallbackStarted.resolve(); await releaseFallback.promise;
          return nativeRender(...args);
        };
        const preparing = api.renderOutputItem(streamedOutput("pending replacement source"), streamed);
        await fallbackStarted.promise;
        assert(ssrNode.isConnected && retainedParagraph.isConnected, "asynchronous native preparation never clears committed SSR");
        releaseFallback.resolve(); await preparing;
        markdown.renderOutputItem = nativeRender;
        assert(streamed.querySelector(".maieutics-turn") === retainedContent && streamed.querySelector('[data-render-engine="ssr"]') === ssrNode, "streaming updates retain the committed SSR boundary instead of reverting to native Markdown");
        assert(retainedParagraph.isConnected && retainedRoot.textContent.includes("new SSR result"), "the committed content remains attached throughout an outstanding render");
        const supersededRequest = messages.at(-1);
        await api.renderOutputItem(streamedOutput("latest replacement source"), streamed);
        receiveMessage({ ...supersededRequest, type: "rendered", html: "<p>stale intermediate</p>", css: "" });
        assert(retainedParagraph.isConnected && !retainedRoot.textContent.includes("stale intermediate"), "obsolete replies cannot replace retained content");
        const failedRequest = messages.at(-1);
        receiveMessage({ ...failedRequest, type: "unavailable" });
        assert(!streamed.querySelector('[data-render-engine="ssr"]'), "an unavailable replacement commits the latest native fallback rather than stale SSR");
        const fallbackNode = streamed.querySelector("[data-markdown-index]");
        assert((fallbackNode.shadowRoot ?? fallbackNode).textContent.includes("latest replacement source"), "the latest fallback is fully rendered before it becomes visible");
        const olderStarted = Promise.withResolvers();
        const releaseOlder = Promise.withResolvers();
        markdown.renderOutputItem = async (...args) => {
          olderStarted.resolve(); await releaseOlder.promise; return nativeRender(...args);
        };
        const olderPaint = api.renderOutputItem(streamedOutput("slow obsolete native"), streamed);
        await olderStarted.promise;
        markdown.renderOutputItem = nativeRender;
        await api.renderOutputItem(streamedOutput("newest source"), streamed);
        receiveMessage({ ...messages.at(-1), type: "rendered", html: "<p>newest committed</p>", css: "" });
        const newestNode = streamed.querySelector('[data-render-engine="ssr"]');
        releaseOlder.resolve(); await olderPaint;
        assert(streamed.querySelector('[data-render-engine="ssr"]') === newestNode && (newestNode.shadowRoot ?? newestNode).textContent.includes("newest committed"), "a late native preparation cannot overwrite a newer committed SSR revision");
        await api.renderOutputItem(streamedOutput("disposed source"), streamed);
        const disposedRequest = messages.at(-1);
        api.disposeOutputItem("streamed");
        receiveMessage({ ...disposedRequest, type: "rendered", html: "<p>late disposed result</p>", css: "" });
        assert(streamed.querySelector('[data-render-engine="ssr"]') === newestNode && !(newestNode.shadowRoot ?? newestNode).textContent.includes("late disposed result"), "disposed output ignores late SSR messages without repainting retained content");
        const nativeStarted = Promise.withResolvers();
        const releaseNative = Promise.withResolvers();
        const originalRender = markdown.renderOutputItem;
        markdown.renderOutputItem = async (...args) => {
          nativeStarted.resolve();
          await releaseNative.promise;
          return originalRender(...args);
        };
        const beforeDisposedRender = messages.length;
        const pendingNative = api.renderOutputItem({ id: "disposed-during-native", json: () => ({ text: "disposed while native rendering awaits" }) }, streamed);
        await nativeStarted.promise;
        api.disposeOutputItem("disposed-during-native");
        releaseNative.resolve();
        await pendingNative;
        markdown.renderOutputItem = originalRender;
        assert(messages.length === beforeDisposedRender, "disposal during native rendering must not enqueue a new SSR request");
        const setOpen = async (detail, value) => {
          const toggle = new Promise((resolve) => detail.addEventListener("toggle", resolve, { once: true }));
          detail.open = value; await toggle;
        };
        await setOpen(rows[0], true);
        await api.renderOutputItem(output, element);
        assert(element.querySelector("details.reasoning").open, "activity expansion survives repaint");
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
        const marked = module.markdownOutputItem(output, '<img src=x onerror="alert(1)">', 11);
        delete marked.maieuticsSafeMarkdown;
        marked.metadata = { maieuticsSafeMarkdown: true };
        markdown.renderOutputItem(marked, hostile);
        const preview = hostile.shadowRoot.getElementById("preview");
        assert(!preview.querySelector("img, script, a[href^='command:']"), "untrusted HTML and executable links stay inert in trusted workspaces");
        const retry = document.createElement("div"); document.body.appendChild(retry);
        await api.renderOutputItem({ id: "retry", json: () => ({ text: '[Retry](command:maieutics.retryTurn?' + encodeURIComponent(JSON.stringify(["notebook:test", 3])) + ')', error: { code: "failed", message: "Failure" } }) }, retry);
        await setOpen(retry.querySelector("details.timeline-node"), true);
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
