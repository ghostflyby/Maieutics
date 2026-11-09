import * as vscode from "vscode";
import { createServer } from "node:http";
import { emptyNotebook } from "../../notebookFormat.ts";

/** Restored outputs must join an already-started connection, not latch native fallback. */
export async function testRenderingDuringConnectionAsync(root: vscode.Uri): Promise<void> {
  const discovery = vscode.Uri.joinPath(root, "rendering-discovery.json");
  const uri = vscode.Uri.joinPath(root, "connecting-renderer.maieuticsnb");
  const requested = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  const applied = Promise.withResolvers<string>();
  let renderRequests = 0;
  const server = createServer((request, response) => {
    const reply = (value: unknown) => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(value));
    };
    if (request.url === "/v1/agent/capabilities") {
      requested.resolve();
      void release.promise.then(() =>
        reply({
          protocolVersion: 1,
          workspaceRoot: root.fsPath,
          rendering: { version: 1 },
          session: { id: "a".repeat(32), turns: 0, persistenceEnabled: false },
        })
      );
    } else if (request.url === "/v1/render/markdown") {
      renderRequests++;
      let body = "";
      request.setEncoding("utf8");
      request.on("data", (chunk: string) => body += chunk);
      request.on("end", () => {
        const { documentId } = JSON.parse(body);
        reply({ version: 1, documentId, html: "<h1>Restored</h1><table></table>", css: "" });
      });
    } else if (request.url === "/v1/agent/sessions") reply({ sessions: [] });
    else reply({ markdown: "Connected" });
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
  const channel = vscode.notebooks.createRendererMessaging("maieutics-turn-renderer");
  const subscription = channel.onDidReceiveMessage(({ editor, message }) => {
    if (editor.notebook.uri.toString() !== uri.toString()) return;
    if (message?.source !== "maieutics-output") return;
    if (message.type === "render") release.resolve();
    if (message.type === "applied") applied.resolve(message.engine);
  });
  let timer: ReturnType<typeof setTimeout> | undefined;
  let command: Promise<unknown> | undefined;
  try {
    await vscode.workspace.fs.writeFile(
      discovery,
      new TextEncoder().encode(JSON.stringify({
        version: 1,
        url: `http://127.0.0.1:${address.port}`,
        token: "test-rendering-recovery",
      })),
    );
    await settings.update("discoveryFile", discovery.fsPath, vscode.ConfigurationTarget.Workspace);
    // Observe command failure immediately even if notebook preparation fails first.
    command = Promise.resolve(vscode.commands.executeCommand("maieutics.showStatus")).then(
      () => undefined,
      (error: unknown) => error,
    );
    const scenario = async () => {
      await requested.promise;
      const notebook = emptyNotebook();
      notebook.cells[0].output = {
        text: "# Restored\n\n| Key | Value |\n| --- | --- |\n| a | b |",
      };
      await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(JSON.stringify(notebook)));
      const document = await vscode.workspace.openNotebookDocument(uri);
      await vscode.window.showNotebookDocument(document, { preview: false });
      const engine = await applied.promise;
      if (engine !== "ssr" || renderRequests !== 1) {
        throw new Error(
          `Restored output abandoned the starting connection: engine=${engine}, SSR requests=${renderRequests}`,
        );
      }
    };
    await Promise.race([
      scenario(),
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error("Restored rendering did not settle")), 20_000);
      }),
    ]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    release.resolve();
    subscription.dispose();
    await command;
    await settings.update("discoveryFile", previous, vscode.ConfigurationTarget.Workspace);
    try {
      // The isolated test host has a deliberately invalid executable; reset the fixture connection.
      await vscode.commands.executeCommand("maieutics.restartServer").then(
        () => {
          throw new Error("The isolated host unexpectedly launched a real kernel");
        },
        () => {},
      );
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve, reject) =>
        server.close((error) => error ? reject(error) : resolve())
      );
    }
  }
}
