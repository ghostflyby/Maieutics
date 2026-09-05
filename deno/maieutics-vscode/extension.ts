/**
 * Maieutics for Visual Studio Code: a notebook-native frontend for the
 * Maieutics agent over the custom web protocol (ADR 0023). The extension owns
 * the `.maieuticsnb` snapshot, spawns or attaches to the `maieutics`
 * executable, and executes cells as Agent turns through a NotebookController
 * — no Jupyter kernel involved.
 */

import * as vscode from "vscode";
import { FrontendClient } from "./client.ts";
import { registerCommandCompletion } from "./completion.ts";
import { connect, type Connection } from "./connection.ts";
import { MaieuticsNotebookController, type NotebookBridge } from "./controller.ts";
import { MaieuticsNotebookSerializer } from "./serializer.ts";

export const ExecutablePathSetting = "maieutics.executablePath";
export const DiscoveryFileSetting = "maieutics.discoveryFile";

let output: vscode.OutputChannel | undefined;
let connection: Connection | undefined;
let connecting: Promise<Connection> | undefined;
let controller: MaieuticsNotebookController | undefined;

export function activate(context: vscode.ExtensionContext): void {
  output = vscode.window.createOutputChannel("Maieutics");
  context.subscriptions.push(output);

  context.subscriptions.push(
    vscode.workspace.registerNotebookSerializer(
      "maieutics-notebook",
      new MaieuticsNotebookSerializer(),
      { transientOutputs: false },
    ),
  );

  controller = new MaieuticsNotebookController(createBridge(), output);
  context.subscriptions.push(controller);

  const notebooksClosed = vscode.workspace.onDidCloseNotebookDocument((document) => {
    if (document.uri.path.endsWith(".maieuticsnb")) controller?.handleNotebookClosed(document);
  });
  context.subscriptions.push(notebooksClosed);

  context.subscriptions.push(
    registerCommandCompletion(
      () => clientOf(),
      (message) => output?.appendLine(message),
    ),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("maieutics.newSession", async () => {
      const client = await clientOf();
      await client.newSession();
      await vscode.window.showInformationMessage("Maieutics: started a new session.");
    }),
    vscode.commands.registerCommand("maieutics.showStatus", async () => {
      const client = await clientOf();
      const markdown = await client.statusMarkdown();
      output?.appendLine(markdown);
      output?.show();
    }),
    vscode.commands.registerCommand("maieutics.listSessions", async () => {
      const client = await clientOf();
      const sessions = await client.listSessions();
      if (sessions.length === 0) {
        await vscode.window.showInformationMessage("Maieutics: no stored sessions yet.");
        return;
      }

      const picked = await vscode.window.showQuickPick(
        sessions.map((session) => ({
          label: session.id.slice(0, 12),
          description: `${session.turns} turn(s), last active ${session.lastActivityAt}`,
          id: session.id,
        })),
        { placeHolder: "Resume a stored session" },
      );
      if (picked) await client.resumeSession(picked.id);
    }),
    vscode.commands.registerCommand("maieutics.restartServer", async () => {
      if (connection) await connection.dispose();
      connection = undefined;
      connecting = undefined;
      await clientOf();
      await vscode.window.showInformationMessage("Maieutics: server restarted.");
    }),
  );
}

export async function deactivate(): Promise<void> {
  if (connection) await connection.dispose();
  connection = undefined;
}

function createBridge(): NotebookBridge {
  return {
    client: clientOf,
    session: async () => await (await clientOf()).session(),
    fetchObject: async (sha256: string) => await (await clientOf()).fetchObject(sha256),
  };
}

async function clientOf(): Promise<FrontendClient> {
  connection ??= await (connecting ??= openConnection());
  return connection.client;
}

async function openConnection(): Promise<Connection> {
  const settings = vscode.workspace.getConfiguration();
  const discoveryFile = settings.get<string>(DiscoveryFileSetting);
  const executablePath = settings.get<string>(ExecutablePathSetting) ?? "maieutics";
  const workspaceRoot = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  const handle = await connect({
    executablePath,
    workspaceRoot,
    discoveryFile: discoveryFile || undefined,
  });
  output?.appendLine(`Connected to ${handle.client.baseUrl}.`);
  return handle;
}
