/**
 * Maieutics for Visual Studio Code: a notebook-native frontend for the
 * Maieutics agent over the custom web protocol (ADR 0023). The extension
 * spawns or attaches to the `maieutics` executable, lists sessions in a tree
 * view, and opens each session as a virtual notebook backed by the server's
 * authoritative transcript — no Jupyter kernel and no required disk file.
 */

import * as vscode from "vscode";
import { FrontendClient } from "./client.ts";
import { registerCommandCompletion } from "./completion.ts";
import { connect, type Connection } from "./connection.ts";
import { MaieuticsNotebookController, type NotebookBridge } from "./controller.ts";
import { MaieuticsNotebookSerializer } from "./serializer.ts";
import { MaieuticsSessionsProvider } from "./sessionsTree.ts";
import { emptyNotebook } from "./notebookFormat.ts";
import {
  MaieuticsFileSystemProvider,
  sessionNotebookPath,
  sessionsFolderUri,
  VfsScheme,
} from "./vfs.ts";

export const ExecutablePathSetting = "maieutics.executablePath";
export const DiscoveryFileSetting = "maieutics.discoveryFile";

let output: vscode.OutputChannel | undefined;
let connection: Connection | undefined;
let connecting: Promise<Connection> | undefined;
let controller: MaieuticsNotebookController | undefined;
let fsProvider: MaieuticsFileSystemProvider | undefined;
let treeRefresh: (() => void) | undefined;

export function activate(context: vscode.ExtensionContext): void {
  output = vscode.window.createOutputChannel("Maieutics");
  context.subscriptions.push(output);

  const fs = new MaieuticsFileSystemProvider();
  fsProvider = fs;
  context.subscriptions.push(
    vscode.workspace.registerFileSystemProvider(VfsScheme, fs, {
      isCaseSensitive: true,
    }),
  );

  context.subscriptions.push(
    vscode.workspace.registerNotebookSerializer(
      "maieutics-notebook",
      new MaieuticsNotebookSerializer(),
      { transientOutputs: false },
    ),
  );

  controller = new MaieuticsNotebookController(createBridge(), output);
  context.subscriptions.push(controller);

  const treeProvider = new MaieuticsSessionsProvider(clientOf);
  const treeView = vscode.window.createTreeView("maieutics.sessions", {
    treeDataProvider: treeProvider,
    showCollapseAll: false,
  });
  context.subscriptions.push(treeView);
  treeRefresh = () => treeProvider.refresh();
  context.subscriptions.push(
    vscode.commands.registerCommand("maieutics.refreshSessions", () => treeProvider.refresh()),
  );

  const notebooksClosed = vscode.workspace.onDidCloseNotebookDocument((document) => {
    controller?.handleNotebookClosed(document);
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
      const session = await client.newSession();
      treeProvider.refresh();
      await openSessionNotebookForUser(session.id);
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
      if (picked) {
        await client.resumeSession(picked.id);
        treeProvider.refresh();
        await openSessionNotebookForUser(picked.id);
      }
    }),
    vscode.commands.registerCommand("maieutics.restartServer", async () => {
      if (connection) await connection.dispose();
      connection = undefined;
      connecting = undefined;
      controller?.resetConnections();
      treeProvider.refresh();
      await clientOf();
      await vscode.window.showInformationMessage("Maieutics: server restarted.");
    }),
    vscode.commands.registerCommand(
      "maieutics.openSessionNotebook",
      async (sessionId: string) => {
        await openSessionNotebookForUser(sessionId);
      },
    ),
    vscode.commands.registerCommand(
      "maieutics.resumeSessionFromTree",
      async (sessionId: string) => {
        const client = await clientOf();
        await client.resumeSession(sessionId);
        treeProvider.refresh();
        await openSessionNotebookForUser(sessionId);
      },
    ),
    vscode.commands.registerCommand("maieutics.mountSessionsFolder", async () => {
      const mounted = vscode.workspace.workspaceFolders?.some((folder) =>
        folder.uri.scheme === VfsScheme
      ) ?? false;
      if (!mounted) {
        const insertAt = vscode.workspace.workspaceFolders?.length ?? 0;
        const added = vscode.workspace.updateWorkspaceFolders(insertAt, 0, {
          uri: sessionsFolderUri(),
          name: "Maieutics Sessions",
        });
        if (!added) {
          await vscode.window.showErrorMessage(
            "Maieutics: the sessions folder could not be added to this workspace.",
          );
          return;
        }
      }

      await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: "Maieutics: syncing sessions" },
        () => syncSessions(),
      );
    }),
  );

  // A restored workspace that already contains the sessions folder must show
  // its files without the user running a command first. Best effort: an
  // unreachable server leaves the folder empty until a refresh.
  if (vscode.workspace.workspaceFolders?.some((folder) => folder.uri.scheme === VfsScheme)) {
    void syncSessions().catch((error) => output?.appendLine(`Session sync failed: ${error}`));
  }
}

export async function deactivate(): Promise<void> {
  if (connection) await connection.dispose();
  connection = undefined;
}

async function openSessionNotebookForUser(sessionId: string): Promise<void> {
  await ensureVfsNotebook(sessionId);
  const uri = vscode.Uri.from({
    scheme: VfsScheme,
    path: sessionNotebookPath(sessionId),
  });
  // vscode.openWith opens (or focuses) the notebook editor.
  await vscode.commands.executeCommand("vscode.openWith", uri, "maieutics-notebook");
}

/** Writes the session notebook into the VFS from the server transcript (a
 * fresh notebook for an empty session, or one cell per committed turn). */
async function ensureVfsNotebook(sessionId: string): Promise<void> {
  const client = await clientOf();
  const [transcript, existing] = await Promise.all([
    client.transcript(sessionId),
    fsProvider?.readSessionNotebook(sessionId),
  ]);

  if (existing) {
    // Keep the user's edits; only extend the session binding if absent.
    if (!existing.session?.serverSessionId) {
      existing.session = { serverSessionId: sessionId };
      fsProvider?.writeSessionNotebook(sessionId, existing);
    }

    return;
  }

  const notebook = emptyNotebook();
  notebook.session = { serverSessionId: sessionId };
  for (const turn of transcript.turns) {
    notebook.cells.push({
      kind: "agent",
      text: turn.messages[0]?.parts.find((part) => part.kind === "text")?.text ?? "",
      output: {
        text: turn.messages.at(-1)?.parts.find((part) => part.kind === "text")?.text ?? "",
        truncated: turn.truncated,
      },
    });
  }

  fsProvider?.writeSessionNotebook(sessionId, notebook);
}

/** Materializes one notebook view per stored session into the VFS so a
 * mounted sessions folder lists them as ordinary files. Existing views are
 * kept untouched. */
async function syncSessions(): Promise<void> {
  const client = await clientOf();
  const sessions = await client.listSessions();
  for (const session of sessions) {
    await ensureVfsNotebook(session.id);
  }

  treeRefresh?.();
}

function createBridge(): NotebookBridge {
  return {
    client: clientOf,
    session: async () => await (await clientOf()).session(),
    fetchObject: async (sha256: string) => await (await clientOf()).fetchObject(sha256),
  };
}

async function clientOf(): Promise<FrontendClient> {
  connection ??= await (connecting ??= openConnection().catch((error) => {
    // Forget the failed attempt so the next command retries instead of
    // replaying a stale rejection until an explicit restart.
    connecting = undefined;
    throw error;
  }));
  return connection.client;
}

async function openConnection(): Promise<Connection> {
  const settings = vscode.workspace.getConfiguration();
  const discoveryFile = settings.get<string>(DiscoveryFileSetting);
  const executablePath = settings.get<string>(ExecutablePathSetting) ?? "maieutics";
  // Only a local folder is a meaningful workspace root for the executable;
  // the mounted sessions folder is virtual.
  const workspaceRoot = vscode.workspace.workspaceFolders?.find((folder) =>
    folder.uri.scheme === "file"
  )?.uri.fsPath;
  const handle = await connect({
    executablePath,
    workspaceRoot,
    discoveryFile: discoveryFile || undefined,
  });
  output?.appendLine(`Connected to ${handle.client.baseUrl}.`);
  return handle;
}
