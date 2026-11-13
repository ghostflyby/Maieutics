import * as vscode from "vscode";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { emptyNotebook } from "../../notebookFormat.ts";

/** Exercise real notebook renderer messages and sockets through the activated extension. */
export async function testFormSessionsAsync(): Promise<void> {
  const extension = vscode.extensions.getExtension("ghostflyby.maieutics-vscode");
  const root = vscode.workspace.workspaceFolders?.[0]?.uri;
  if (!extension || !root) throw new Error("Missing extension or test workspace");
  const child = spawn("deno", [
    "run",
    "--no-config",
    "--allow-net=127.0.0.1",
    vscode.Uri.joinPath(extension.extensionUri, "test", "formSessionServer.ts").fsPath,
  ], { stdio: ["ignore", "pipe", "pipe"] });
  const exited = new Promise<void>((resolve, reject) => {
    child.once("error", reject);
    child.once(
      "exit",
      (code) => code === 0 ? resolve() : reject(new Error(`Form fixture exited: ${code}`)),
    );
  });
  void exited.catch(() => {}); // The finally block owns and joins this process.
  let stderr = "";
  child.stderr.on("data", (chunk: Buffer) => stderr = (stderr + chunk.toString()).slice(-4000));
  const lines = createInterface({ input: child.stdout });
  const settings = vscode.workspace.getConfiguration("maieutics");
  const previous = settings.get<string>("discoveryFile");
  const discoveryUri = vscode.Uri.joinPath(root, "form-session-discovery.json");
  const documents: vscode.NotebookDocument[] = [];
  const lifetime = new AbortController();
  let url: string | undefined;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let stage = "fixture readiness";
  const budget = new Promise<never>((_, reject) => {
    timer = setTimeout(
      () => {
        lifetime.abort(new Error(`Notebook form session fixture timed out at ${stage}. ${stderr}`));
        reject(lifetime.signal.reason);
      },
      30_000,
    );
  });
  try {
    const firstLine = lines[Symbol.asyncIterator]().next();
    const line = await Promise.race([
      firstLine,
      budget,
      exited.then(() => {
        throw new Error("Missing fixture readiness");
      }),
    ]);
    if (line.done) throw new Error(`Missing fixture readiness. ${stderr}`);
    const discovery = JSON.parse(line.value) as { url: string };
    url = discovery.url;
    await vscode.workspace.fs.writeFile(discoveryUri, new TextEncoder().encode(line.value));
    await settings.update(
      "discoveryFile",
      discoveryUri.fsPath,
      vscode.ConfigurationTarget.Workspace,
    );
    const request = async (path: string) => {
      stage = path;
      const response = await fetch(`${discovery.url}${path}`, {
        signal: lifetime.signal,
      });
      if (!response.ok) throw new Error(`Form fixture request failed: ${path}`);
      return await response.json();
    };
    const scenario = async () => {
      stage = "kernel connection";
      await vscode.commands.executeCommand("maieutics.showStatus");
      for (const [index, id] of ["a".repeat(32), "b".repeat(32)].entries()) {
        const uri = vscode.Uri.joinPath(root, `form-session-${index}.maieuticsnb`);
        const notebook = emptyNotebook();
        notebook.session = { serverSessionId: id };
        await vscode.workspace.fs.writeFile(
          uri,
          new TextEncoder().encode(JSON.stringify(notebook)),
        );
        const document = await vscode.workspace.openNotebookDocument(uri);
        stage = `notebook ${index} preparation`;
        documents.push(document);
        // Live output is injected into the notebook model; saved form MIME stays readonly.
        const cell = new vscode.NotebookCellData(vscode.NotebookCellKind.Code, "Form", "markdown");
        cell.outputs = [
          new vscode.NotebookCellOutput([
            vscode.NotebookCellOutputItem.json({
              version: 1,
              modelId: "scoped-form",
              state: { title: "Connecting", fields: [], values: {} },
            }, "application/vnd.maieutics.form+json"),
          ]),
        ];
        const edit = new vscode.WorkspaceEdit();
        edit.set(uri, [vscode.NotebookEdit.replaceCells(new vscode.NotebookRange(0, 1), [cell])]);
        if (!await vscode.workspace.applyEdit(edit)) throw new Error("Form notebook edit failed");
        await vscode.window.showNotebookDocument(document, {
          viewColumn: index === 0 ? vscode.ViewColumn.One : vscode.ViewColumn.Beside,
          preserveFocus: true,
          preview: false,
        });
        console.log(
          `Form session fixture: notebook ${index} pin=${
            String(document.metadata.maieuticsSessionId)
          }, outputs=${document.cellAt(0).outputs.length}`,
        );
        const connected = await request("/test/next-open") as { sessionId: string };
        if (connected.sessionId !== id) {
          throw new Error(
            `Form connected to ${connected.sessionId}; its notebook is pinned to ${id}`,
          );
        }
        await document.save();
      }
      const stats = await request("/test/stats") as { sessions: string[]; foregroundReads: number };
      if (
        stats.foregroundReads !== 0 ||
        stats.sessions.sort().join() !== ["a".repeat(32), "b".repeat(32)].join()
      ) {
        throw new Error(
          "Notebook forms borrowed the foreground session or shared their comm connection",
        );
      }
      const first = documents[0];
      const repin = new vscode.WorkspaceEdit();
      repin.set(first.uri, [
        vscode.NotebookEdit.updateNotebookMetadata({ maieuticsSessionId: "c".repeat(32) }),
      ]);
      if (!await vscode.workspace.applyEdit(repin)) throw new Error("Form notebook repin failed");
      await request(`/test/wait?id=${"a".repeat(32)}&closed`);
      const retained = await request("/test/stats") as { sessions: string[] };
      if (retained.sessions.join() !== "b".repeat(32)) {
        throw new Error("Repinning one notebook disturbed the other form session");
      }
      await first.save();
      const second = documents[1];
      const tabs = vscode.window.tabGroups.all.flatMap((group) => group.tabs).filter((tab) =>
        tab.input instanceof vscode.TabInputNotebook &&
        tab.input.uri.toString() === second.uri.toString()
      );
      if (tabs.length !== 1) {
        throw new Error(`Expected one form notebook tab, found ${tabs.length}`);
      }
      if (!await vscode.window.tabGroups.close(tabs, true)) {
        throw new Error("Form notebook close failed");
      }
      console.log(`Form session fixture: closed tab; document.isClosed=${second.isClosed}`);
      await request(`/test/wait?id=${"b".repeat(32)}&closed`);
    };
    await scenario();
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    lifetime.abort();
    lines.close();
    try {
      try {
        for (const document of documents) {
          if (!document.isClosed && document.isDirty) await document.save();
          const tabs = vscode.window.tabGroups.all.flatMap((group) => group.tabs).filter((tab) =>
            tab.input instanceof vscode.TabInputNotebook &&
            tab.input.uri.toString() === document.uri.toString()
          );
          if (tabs.length) await vscode.window.tabGroups.close(tabs, true);
        }
      } finally {
        await settings.update("discoveryFile", previous, vscode.ConfigurationTarget.Workspace);
        await vscode.commands.executeCommand("maieutics.restartServer").then(
          () => {
            throw new Error("The isolated test host unexpectedly started a real kernel");
          },
          () => {},
        );
      }
    } finally {
      const shutdown = AbortSignal.timeout(5_000);
      const expired = new Promise<never>((_, reject) => {
        shutdown.addEventListener(
          "abort",
          () => reject(new Error("Form fixture shutdown exceeded its budget")),
          { once: true },
        );
      });
      try {
        const stopping = async () => {
          if (url) {
            const response = await fetch(`${url}/test/stop`, { signal: shutdown });
            await response.arrayBuffer();
          } else child.kill();
          await exited;
        };
        await Promise.race([stopping(), expired]);
      } finally {
        if (child.exitCode === null) {
          child.kill("SIGKILL");
          await exited.catch(() => {});
        }
      }
    }
  }
}
