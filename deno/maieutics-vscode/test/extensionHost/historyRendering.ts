import * as vscode from "vscode";
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { FrontendClient } from "../../client.ts";
import { emptyNotebook } from "../../notebookFormat.ts";
import type { Transcript } from "../../protocol.ts";
import { LiveAssistantOutputMime } from "../../turnView.ts";

/** Actual activated serializer, kernel history verification, render routing and Vue mount. */
export async function testHistoricalVueAsync(root: vscode.Uri): Promise<void> {
  const renderingDiscovery = process.env.MAIEUTICS_HISTORY_RENDERING_DISCOVERY;
  const renderer = renderingDiscovery
    ? FrontendClient.fromDiscovery(JSON.parse(await readFile(renderingDiscovery, "utf8")))
    : undefined;
  const pending: Promise<void>[] = [];
  const sessionId = "c".repeat(32);
  const source =
    "<script setup>\nimport { ref } from 'vue';\nconst count = ref(0);const browser=()=>import.meta.env.SSR ? 'server' : window.location.protocol;\n</script>\n\n# History counter\n\n<button @click=\"count++\">{{ count }}</button>\n\n<ClientOnly><span>{{browser()}}</span></ClientOnly>";
  const transcript: Transcript = {
    version: 17,
    sessionId,
    turns: [{
      runId: "history-run",
      truncated: false,
      messages: [
        { role: "user", parts: [{ kind: "text", text: "Question" }] },
        { role: "assistant", parts: [{ kind: "text", text: source }] },
      ],
    }],
  };
  let assistantRequests = 0;
  let transcriptRequests = 0;
  let historyUnavailable = false;
  const unexpected: string[] = [];
  const server = createServer((request, response) => {
    const reply = (value: unknown, status = 200) => {
      response.writeHead(status, { "Content-Type": "application/json" });
      response.end(JSON.stringify(value));
    };
    if (request.headers.authorization !== "Bearer test-history") return reply({}, 401);
    if (request.url === "/v1/agent/capabilities") {
      return reply({
        protocolVersion: 1,
        serverVersion: "fixture",
        workspaceRoot: root.fsPath,
        rendering: { version: 1, assistantVersion: 1 },
        session: { id: sessionId, turns: 1, persistenceEnabled: true },
      });
    }
    if (request.url === `/v1/agent/sessions/${sessionId}/transcript`) {
      transcriptRequests++;
      if (historyUnavailable) return reply({ code: "unavailable", message: "fixture" }, 503);
      return reply(transcript);
    }
    if (request.url === `/v1/agent/sessions/${sessionId}`) {
      return reply({ id: sessionId, turns: 1, persistenceEnabled: true });
    }
    if (request.url === "/v1/status") return reply({ markdown: "Connected" });
    if (request.url === "/v1/agent/sessions") return reply({ sessions: [] });
    if (request.url === "/v1/render/assistant" || request.url === "/v1/render/markdown") {
      const assistant = request.url.endsWith("/assistant");
      let body = "";
      request.setEncoding("utf8");
      request.on("data", (chunk: string) => body += chunk);
      request.on("end", () => {
        const { documentId, source: requestedSource } = JSON.parse(body);
        if (assistant) {
          assistantRequests++;
          if (requestedSource !== source) unexpected.push("unverified source executed");
        }
        if (renderer) {
          pending.push(
            renderer.renderMarkdown(
              documentId,
              requestedSource,
              AbortSignal.timeout(20_000),
              undefined,
              undefined,
              assistant,
            ).then(
              (snapshot) => reply(snapshot),
              (error: unknown) => {
                unexpected.push(error instanceof Error ? error.name : "renderer failure");
                reply({ code: "render_failed", message: "fixture" }, 503);
              },
            ),
          );
          return;
        }
        reply({
          version: 1,
          documentId,
          html: assistant ? "<button>0</button>" : "<p>Passive history</p>",
          css: "",
          ...(assistant
            ? {
              vue: {
                version: 1,
                scopeId: `data-v-${documentId}`,
                code:
                  'const {h,ref}=Vue;return {setup(){const count=ref(0);return()=>h("button",{onClick:()=>count.value++},String(count.value))}};',
              },
            }
            : {}),
        });
      });
      return;
    }
    unexpected.push(`${request.method} ${request.url}`);
    reply({}, 404);
  });
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", reject);
      resolve();
    });
  });
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("No fixture port");
  const settings = vscode.workspace.getConfiguration("maieutics");
  const previous = settings.get<string>("discoveryFile");
  const discovery = vscode.Uri.joinPath(root, "history-discovery.json");
  const channel = vscode.notebooks.createRendererMessaging("maieutics-turn-renderer");
  const waiting = new Map<string, (engine: string) => void>();
  const subscription = channel.onDidReceiveMessage(({ editor, message }) => {
    if (message?.source === "maieutics-output" && message.type === "applied") {
      waiting.get(editor.notebook.uri.toString())?.(message.engine);
    }
  });
  const open = async (uri: vscode.Uri, expected: string) => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const applied = new Promise<string>((resolve, reject) => {
      waiting.set(uri.toString(), resolve);
      timer = setTimeout(
        () => reject(new Error(`History rendering timed out: ${uri.path}`)),
        20_000,
      );
    });
    try {
      const document = await vscode.workspace.openNotebookDocument(uri);
      await vscode.window.showNotebookDocument(document, { preview: false });
      const engine = await applied;
      if (engine !== expected) {
        throw new Error(`Historical component was not restored: ${engine}, expected ${expected}`);
      }
      if (
        expected === "vue" &&
        !document.getCells().some((cell) =>
          cell.outputs.some((output) =>
            output.items.some((item) => item.mime === LiveAssistantOutputMime)
          )
        )
      ) {
        throw new Error("History verification did not recreate runtime provenance");
      }
      if (document.isDirty) throw new Error("History restoration changed the saved notebook");
      if (vscode.window.activeNotebookEditor?.notebook === document) {
        await vscode.commands.executeCommand("workbench.action.closeActiveEditor");
      }
    } finally {
      if (timer !== undefined) clearTimeout(timer);
      waiting.delete(uri.toString());
    }
  };
  const saved = async (name: string, text: string, bound = true) => {
    const notebook = emptyNotebook();
    notebook.session = { serverSessionId: sessionId };
    notebook.cells[0] = {
      kind: "agent",
      text: "Question",
      output: { text },
      ...(bound ? { turn: { runId: "history-run", input: "Question" } } : {}),
    };
    const uri = vscode.Uri.joinPath(root, `${name}.maieuticsnb`);
    await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(JSON.stringify(notebook)));
    return uri;
  };
  try {
    await vscode.workspace.fs.writeFile(
      discovery,
      new TextEncoder().encode(JSON.stringify({
        version: 1,
        url: `http://127.0.0.1:${address.port}`,
        token: "test-history",
      })),
    );
    await settings.update("discoveryFile", discovery.fsPath, vscode.ConfigurationTarget.Workspace);
    // No command pre-connects: reloading bound history must establish its own connection.
    await open(await saved("verified-history", source), "vue");
    await open(await saved("edited-history", source + "\nChanged"), "ssr");
    await open(await saved("unbound-history", source, false), "ssr");
    await open(vscode.Uri.parse(`maieutics:/sessions/${sessionId}.maieuticsnb`), "vue");
    transcript.turns[0].truncated = true;
    await open(await saved("truncated-history", source), "ssr");
    transcript.turns[0].truncated = false;
    transcript.sessionId = "d".repeat(32);
    await open(await saved("mismatched-session-history", source), "ssr");
    transcript.sessionId = sessionId;
    historyUnavailable = true;
    await open(await saved("unavailable-history", source), "ssr");
    if (assistantRequests !== 2 || transcriptRequests < 2 || unexpected.length > 0) {
      throw new Error(
        `History recovery bypassed its verification boundary: ${assistantRequests} component requests, ${transcriptRequests} transcript reads, ${
          unexpected.join(", ")
        }`,
      );
    }
  } finally {
    subscription.dispose();
    await settings.update("discoveryFile", previous, vscode.ConfigurationTarget.Workspace);
    try {
      await vscode.commands.executeCommand("maieutics.restartServer").then(
        () => {
          throw new Error("The isolated host unexpectedly launched a real kernel");
        },
        () => {},
      );
    } finally {
      await Promise.all(pending);
      server.closeAllConnections();
      await new Promise<void>((resolve, reject) =>
        server.close((error) => error ? reject(error) : resolve())
      );
    }
  }
}
