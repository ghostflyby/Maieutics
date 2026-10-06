/**
 * Maieutics for Visual Studio Code: a notebook-native frontend for the
 * Maieutics agent over the custom web protocol (ADR 0023). The extension
 * spawns or attaches to the `maieutics` executable, lists sessions in a
 * workspace-grouped tree view, and opens each session as a virtual notebook
 * backed by the server's authoritative transcript — no Jupyter kernel and no
 * required disk file. The `maieutics:` virtual filesystem exposes lens
 * directories (all / current workspace / recent) over one canonical
 * per-session view; disk snapshots remain optional.
 */

import * as vscode from "vscode";
import { platform } from "node:os";
import { FrontendClient } from "./client.ts";
import { registerCommandCompletion } from "./completion.ts";
import { connect, type Connection } from "./connection.ts";
import { cellHistoryState, frontierIndex, readTurnBinding } from "./cellHistory.ts";
import { TurnOutputMime } from "./turnView.ts";
import { MaieuticsNotebookController, type NotebookBridge, taggedCell } from "./controller.ts";
import { QueueProjection } from "./queueProjection.ts";
import { MaieuticsNotebookSerializer, NotebookType, readStoredSessionId } from "./serializer.ts";
import { NotebookLanguage } from "./notebookFormat.ts";
import { FrontendError, type TaskListEntry, type Transcript } from "./protocol.ts";
import { sessionLabel, type SessionLike } from "./sessionGroups.ts";
import { MaieuticsSessionsProvider, type TreeEnvironment } from "./sessionsTree.ts";
import { shortTaskId, taskDetailText, taskPicks, TasksEmptyHint } from "./taskPicks.ts";
import { iframeViewState, type PluginPick, pluginPicks, PluginsEmptyHint } from "./pluginPicks.ts";
import type { AttachmentMarker } from "./attachments.ts";
import {
  appendAttachmentMarkers,
  formatAttachmentMarker,
  guessAttachmentMediaType,
  parseAttachmentMarkers,
} from "./attachments.ts";
import { UiBridge } from "./uiBridge.ts";
import { emptyNotebook } from "./notebookFormat.ts";
import {
  MaieuticsFileSystemProvider,
  type SessionLens,
  sessionNotebookPath,
  VfsScheme,
} from "./vfs.ts";

export const ExecutablePathSetting = "maieutics.executablePath";
export const DiscoveryFileSetting = "maieutics.discoveryFile";
/** Workspace-state key for the current-workspace-only tree filter. */
const CurrentOnlyStateKey = "maieutics.currentOnlySessions";

let output: vscode.OutputChannel | undefined;
let connection: Connection | undefined;
let connecting: Promise<Connection> | undefined;
let controller: MaieuticsNotebookController | undefined;
let fsProvider: MaieuticsFileSystemProvider | undefined;
let treeRefresh: (() => void) | undefined;
const treeEnvironment: TreeEnvironment = {
  caseInsensitive: false,
  currentOnly: false,
};

export function activate(context: vscode.ExtensionContext): void {
  output = vscode.window.createOutputChannel("Maieutics");
  context.subscriptions.push(output);

  const fs = new MaieuticsFileSystemProvider({
    listSessions: () => clientOf().then((client) => client.listSessions()),
    fetchTranscript: (sessionId) =>
      clientOf().then(async (client) => {
        let transcript: Transcript;
        try {
          transcript = await client.transcript(sessionId);
        } catch (error) {
          // Transcripts are served per session, but lazily: reading a stored
          // session that is not live yet resumes it. On legacy single-active
          // servers the read fails with session_not_active for a non-foreground
          // session; resuming first keeps both server shapes working.
          if (!(error instanceof FrontendError) || error.code !== "session_not_active") throw error;
          await client.resumeSession(sessionId);
          transcript = await client.transcript(sessionId);
        }
        const notebook = emptyNotebook();
        notebook.session = { serverSessionId: sessionId };
        for (const turn of transcript.turns) {
          const input = turn.messages[0]?.parts.find((part) => part.kind === "text")?.text ?? "";
          notebook.cells.push({
            kind: "agent",
            text: input,
            output: {
              text: turn.messages.at(-1)?.parts.find((part) => part.kind === "text")?.text ?? "",
              truncated: turn.truncated,
            },
            // Every stored turn was committed: the materialized view opens as
            // committed history with the frontier at its end.
            turn: turn.runId === "" ? undefined : { runId: turn.runId, input },
          });
        }
        return notebook;
      }),
    currentWorkspace: () => ({
      root: treeEnvironment.workspaceRoot,
      caseInsensitive: treeEnvironment.caseInsensitive,
    }),
  });
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

  // The queued-cell markers are a projection of the server-owned session
  // queue: the controller maintains the itemId ↔ cell map from queue.updated
  // frames and reconnect snapshots; the emitter is the providers' refresh
  // signal.
  const queueProjection = new QueueProjection<vscode.NotebookCell>();
  const queueChanges = new vscode.EventEmitter<void>();
  queueProjection.onDidChange(() => queueChanges.fire());
  controller = new MaieuticsNotebookController(createBridge(), output, queueProjection);
  context.subscriptions.push(controller);

  context.subscriptions.push(...registerHistorySurface());
  context.subscriptions.push(...registerQueueSurface(queueProjection, queueChanges));
  context.subscriptions.push(...registerAttachmentSurface());
  context.subscriptions.push(...registerUsageBadge());

  treeEnvironment.currentOnly = context.workspaceState.get(CurrentOnlyStateKey, false);
  // Reflect the persisted filter in `when` clauses from the start.
  void vscode.commands.executeCommand(
    "setContext",
    "maieutics.currentOnly",
    treeEnvironment.currentOnly,
  );
  const treeProvider = new MaieuticsSessionsProvider(
    clientOf,
    () => treeEnvironment,
    controller.activity,
  );
  context.subscriptions.push(
    ...registerActivitySurface(controller.activity, {
      refresh: () => treeProvider.refresh(),
    }),
  );
  const treeView = vscode.window.createTreeView("maieutics.sessions", {
    treeDataProvider: treeProvider,
    showCollapseAll: false,
  });
  context.subscriptions.push(treeView);
  treeRefresh = () => treeProvider.refresh();
  context.subscriptions.push(
    vscode.commands.registerCommand("maieutics.refreshSessions", () => treeProvider.refresh()),
    vscode.commands.registerCommand("maieutics.toggleCurrentWorkspaceFilter", () => {
      treeEnvironment.currentOnly = !treeEnvironment.currentOnly;
      void context.workspaceState.update(CurrentOnlyStateKey, treeEnvironment.currentOnly);
      void vscode.commands.executeCommand(
        "setContext",
        "maieutics.currentOnly",
        treeEnvironment.currentOnly,
      );
      treeProvider.refresh();
    }),
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

  // UI renderers (widget compat + native view families) talk to the kernel's
  // UI models through this bridge; the comms socket opens lazily on the first
  // mount so plain notebooks never pay for it (ADR 0024, ADR 0038).
  const widgetChannel = vscode.notebooks.createRendererMessaging("maieutics-widget-renderer");
  const viewChannel = vscode.notebooks.createRendererMessaging("maieutics-view-renderer");
  const uiBridge = new UiBridge({
    connect: async () => {
      const client = await clientOf();
      // The comm capability gate (ADR 0024): an older server without comm
      // support would otherwise hang on a socket that never opens.
      const capabilities = await client.capabilities();
      if (capabilities.comm === undefined) {
        throw new FrontendError(
          "comm_unavailable",
          0,
          "This Maieutics server does not expose widget comm channels.",
        );
      }

      const session = await client.session();
      return client.commSocket(session.id);
    },
    // State frames broadcast to both channels; renderers filter by source.
    post: (message) => {
      for (const channel of [widgetChannel, viewChannel]) {
        void Promise.resolve(channel.postMessage(message)).catch(
          (error: unknown) => output?.appendLine(`ui post failed: ${error}`),
        );
      }
    },
    log: (message) => output?.appendLine(message),
  });
  context.subscriptions.push(
    // The turn timeline's retry anchor (requiresMessaging: always).
    vscode.notebooks.createRendererMessaging("maieutics-turn-renderer").onDidReceiveMessage(
      ({ message }) => {
        const envelope = message as {
          source?: unknown;
          type?: unknown;
          notebookUri?: unknown;
          cellIndex?: unknown;
        };
        if (
          envelope.source === "maieutics-turn" && envelope.type === "retry" &&
          typeof envelope.notebookUri === "string" && typeof envelope.cellIndex === "number"
        ) {
          void vscode.commands.executeCommand("maieutics.retryTurn", [
            envelope.notebookUri,
            envelope.cellIndex,
          ]);
        }
      },
    ),
    widgetChannel.onDidReceiveMessage(({ message }) => {
      const envelope = message as { source?: unknown };
      if (envelope.source !== "maieutics-widget") return;
      void uiBridge.handleRendererMessage(message).catch((error: unknown) =>
        output?.appendLine(`ui bridge failed: ${error}`)
      );
    }),
    viewChannel.onDidReceiveMessage(({ message }) => {
      const envelope = message as { source?: unknown };
      if (envelope.source !== "maieutics-ui") return;
      void uiBridge.handleRendererMessage(message).catch((error: unknown) =>
        output?.appendLine(`ui bridge failed: ${error}`)
      );
    }),
    { dispose: () => void uiBridge.dispose().catch(() => {}) },
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
          label: sessionLabel(session),
          description: pickDescription(session),
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
    vscode.commands.registerCommand("maieutics.listTasks", async () => {
      const client = await clientOf();
      const session = await client.session();
      await runTaskQuickPick(client, session.id);
    }),
    vscode.commands.registerCommand("maieutics.listPlugins", async () => {
      const client = await clientOf();
      const { plugins } = await client.plugins();
      if (plugins.length === 0) {
        await vscode.window.showInformationMessage(PluginsEmptyHint);
        return;
      }

      // The pick loop: picking a form publishes it into the live session's
      // comm plane (the view renderer claims the announcement); picking a page
      // publishes an iframe view state the same way. Neither action exists for
      // unapproved plugins (pluginPicks strips their actions).
      let active: PluginPick[] = pluginPicks(plugins);
      while (active.length > 0) {
        const picked = await vscode.window.showQuickPick(active, {
          placeHolder: "Maieutics plugins — pick a form to open or a page to view",
          ignoreFocusOut: false,
        });
        if (picked === undefined) return;
        if (picked.publishForm) {
          await client.publishPluginForm(picked.pluginId);
          await vscode.window.showInformationMessage(
            `Plugin form '${picked.pluginId}' published to the live notebook output.`,
          );
          return;
        }
        if (picked.pageUrl !== undefined) {
          // The gateway URL opens in a browser; in-product iframe embedding waits
          // for the maieutics/iframe kernel producer path. pluginPicks only hands
          // out vetted loopback gateway links, but the external open re-checks.
          if (iframeViewState(picked.pageUrl, picked.pluginId) === undefined) return;
          await vscode.env.openExternal(vscode.Uri.parse(picked.pageUrl));
          return;
        }
        active = active.filter((candidate) => candidate.pluginId !== picked.pluginId);
      }
    }),
    vscode.commands.registerCommand(
      "maieutics.renameSession",
      async (argument?: CommandArgument) => {
        const client = await clientOf();
        const target = await pickSession(client, unwrapSessionRef(argument), "Rename a session");
        if (target === undefined) return;

        const title = await vscode.window.showInputBox({
          prompt: `Title for session ${target.session.id.slice(0, 12)} (empty clears)`,
          value: target.session.title ?? "",
          ignoreFocusOut: true,
        });
        if (title === undefined) return;

        await client.renameSession(target.session.id, title);
        treeProvider.refresh();
      },
    ),
    vscode.commands.registerCommand(
      "maieutics.copySessionId",
      async (argument?: CommandArgument) => {
        const client = await clientOf();
        const target = await pickSession(client, unwrapSessionRef(argument), "Copy a session id");
        if (target === undefined) return;
        await vscode.env.clipboard.writeText(target.session.id);
        await vscode.window.showInformationMessage("Maieutics: session id copied.");
      },
    ),
    vscode.commands.registerCommand("maieutics.pruneObjects", async () => {
      const client = await clientOf();
      const active = await client.session();
      const raw = await vscode.window.showInputBox({
        prompt: "Object GC grace period in hours",
        value: "24",
        ignoreFocusOut: true,
      });
      if (raw === undefined) return;
      const graceHours = Number(raw);
      if (!Number.isInteger(graceHours) || graceHours < 0) {
        await vscode.window.showErrorMessage(
          "Maieutics: the grace period must be a non-negative number of hours.",
        );
        return;
      }

      const markdown = await client.pruneObjects(active.id, graceHours);
      await vscode.window.showInformationMessage(`Maieutics: ${markdown.replace(/\*\*/g, "")}`);
    }),
    vscode.commands.registerCommand("maieutics.repairObjectView", async () => {
      const client = await clientOf();
      const active = await client.session();
      const markdown = await client.repairObjectView(active.id);
      await vscode.window.showInformationMessage(`Maieutics: ${markdown.replace(/\*\*/g, "")}`);
    }),
    vscode.commands.registerCommand("maieutics.restartServer", async () => {
      if (connection) await connection.dispose();
      connection = undefined;
      connecting = undefined;
      fsProvider?.reset();
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
    vscode.commands.registerCommand(
      "maieutics.switchBranch",
      async (cell?: vscode.NotebookCell) => {
        const notebook = cell?.notebook ?? vscode.window.activeNotebookEditor?.notebook;
        if (notebook === undefined || notebook.notebookType !== NotebookType) return;

        const client = await clientOf();
        const currentId = readStoredSessionId(notebook.metadata);
        if (currentId === undefined) {
          await vscode.window.showInformationMessage(
            "Maieutics: run a cell first — branches attach to the session this notebook ran against.",
          );
          return;
        }

        const sessions = await client.listSessions().catch(() => []);
        const byId = new Map(sessions.map((session) => [session.id, session]));
        const current = byId.get(currentId);
        if (current === undefined) {
          await vscode.window.showInformationMessage(
            "Maieutics: this notebook's session has no stored branches.",
          );
          return;
        }

        // The family: everything connected through parent links. Both walks
        // are depth-bounded — the server caps chains at 64, and a corrupted
        // cyclic parent link must not spin the extension host.
        const parentOf = new Map(sessions.map((session) => [session.id, session.parentSessionId]));
        const depthOf = new Map<string, number>([[currentId, 0]]);
        let cursor = current.parentSessionId;
        for (let depth = 0; cursor !== undefined && byId.has(cursor) && depth < 64; depth++) {
          depthOf.set(cursor, (depthOf.get(cursor) ?? 0) - 1);
          cursor = parentOf.get(cursor);
        }
        for (const session of sessions) {
          let cursor: string | undefined = session.id;
          let depth = 0;
          while (cursor !== undefined && !depthOf.has(cursor) && depth < 16) {
            cursor = parentOf.get(cursor);
            depth++;
          }
          if (cursor !== undefined && depthOf.has(cursor)) {
            depthOf.set(session.id, (depthOf.get(cursor) ?? 0) + depth);
          }
        }

        const picks = sessions
          .filter((session) => session.id !== currentId && depthOf.has(session.id))
          .sort((a, b) => (depthOf.get(a.id) ?? 0) - (depthOf.get(b.id) ?? 0))
          .map((session) => ({
            label: `${session.parentSessionId === currentId ? "$(git-branch) " : ""}${
              sessionLabel(session)
            }`,
            description: `branch @ turn ${(session.forkPointSeq ?? 0) + 1}`,
            id: session.id,
          }));
        const parent = current.parentSessionId === undefined
          ? undefined
          : byId.get(current.parentSessionId);
        if (parent !== undefined) {
          picks.unshift({
            label: `$(arrow-up) ${sessionLabel(parent)}`,
            description: "parent conversation",
            id: parent.id,
          });
        }
        if (picks.length === 0) {
          await vscode.window.showInformationMessage(
            "Maieutics: no sibling branches — run a committed cell to fork one.",
          );
          return;
        }

        const picked = await vscode.window.showQuickPick(picks, {
          placeHolder: `Switch this conversation to another branch (current: ${
            sessionLabel(current)
          })`,
        });
        if (picked === undefined) return;

        await client.resumeSession(picked.id);
        treeProvider.refresh();
        await openSessionNotebookForUser(picked.id);
      },
    ),
    vscode.commands.registerCommand("maieutics.queueTurn", async () => {
      const editor = vscode.window.activeNotebookEditor;
      if (editor === undefined || editor.notebook.notebookType !== NotebookType) {
        await vscode.window.showInformationMessage(
          "Maieutics: open a Maieutics notebook to queue a turn.",
        );
        return;
      }

      const text = await vscode.window.showInputBox({
        prompt:
          "Queue a follow-up turn — it is appended below the commit frontier and submits after the in-flight run finishes",
        ignoreFocusOut: true,
      });
      if (text === undefined || text.trim().length === 0) return;

      const document = editor.notebook;
      const cells = document.getCells();
      const frontier = frontierIndex(cells.map(taggedCell));
      const insertAt = frontier >= 0 ? frontier + 1 : cells.length;
      const edit = new vscode.WorkspaceEdit();
      edit.set(document.uri, [
        vscode.NotebookEdit.insertCells(
          insertAt,
          [
            new vscode.NotebookCellData(
              vscode.NotebookCellKind.Code,
              text,
              NotebookLanguage,
            ),
          ],
        ),
      ]);
      await vscode.workspace.applyEdit(edit);

      // The cell is a pending turn, so it joins the session's server-owned
      // queue behind anything already queued or running and shows the queued
      // marker until its item starts.
      const queued = document.getCells()[insertAt];
      if (queued !== undefined && controller !== undefined) {
        await controller.runAsync([queued], document);
      }
    }),
    vscode.commands.registerCommand(
      "maieutics.attachFile",
      async (...args: unknown[]) => {
        // The cell status-bar item passes [cell]; the command is hidden from
        // the palette because a cell is its only meaningful target.
        const cell = args.find((arg): arg is vscode.NotebookCell =>
          typeof arg === "object" && arg !== null && "document" in arg && "notebook" in arg
        );
        if (cell === undefined || cell.notebook.notebookType !== NotebookType) return;

        // Objects are ingested into a session's library, so the notebook must
        // already be pinned to one: attaching before the first run would
        // reference a session the cell's eventual turn may never run against.
        const sessionId = readStoredSessionId(cell.notebook.metadata);
        if (sessionId === undefined) {
          await vscode.window.showInformationMessage(
            "Maieutics: run a cell first — attachments upload into the session this notebook runs against.",
          );
          return;
        }

        const picked = await vscode.window.showOpenDialog({
          canSelectMany: true,
          openLabel: "Attach",
          title: "Attach files to this cell",
        });
        if (picked === undefined || picked.length === 0) return;

        const client = await clientOf();
        const markers: string[] = [];
        const failures: string[] = [];
        await vscode.window.withProgress(
          {
            location: vscode.ProgressLocation.Notification,
            title: "Maieutics: uploading attachments…",
          },
          async () => {
            for (const uri of picked) {
              const name = uri.path.split("/").pop() ?? "file";
              try {
                const bytes = new Uint8Array(await vscode.workspace.fs.readFile(uri));
                const mediaType = guessAttachmentMediaType(name);
                const uploaded = await client.uploadObject(sessionId, bytes, mediaType, name);
                markers.push(
                  formatAttachmentMarker({ sha256: uploaded.sha256, mediaType, name }),
                );
              } catch (error) {
                // One bad pick must not sink the batch: the rest still attach
                // and the failure is reported with the file's name.
                failures.push(name);
                output?.appendLine(`attach ${name} failed: ${error}`);
              }
            }
          },
        );

        if (markers.length > 0) {
          // The markers ride the cell TEXT (the turn-reference grammar), so
          // they flow through the queue, the turn binding, and save/reopen
          // with no extra state — exactly what gets submitted is what shows.
          const text = cell.document.getText();
          const edit = new vscode.WorkspaceEdit();
          edit.replace(
            cell.document.uri,
            new vscode.Range(cell.document.positionAt(0), cell.document.positionAt(text.length)),
            appendAttachmentMarkers(text, markers),
          );
          await vscode.workspace.applyEdit(edit);
        }

        if (failures.length > 0) {
          await vscode.window.showErrorMessage(
            `Maieutics: ${failures.length} of ${picked.length} attachment(s) failed to upload ` +
              `(${failures.join(", ")}). The rest were attached.`,
          );
        }
      },
    ),
    vscode.commands.registerCommand("maieutics.mountSessionsFolder", async () => {
      const lens = await vscode.window.showQuickPick(
        [
          {
            label: "All Sessions",
            detail: "Every stored session",
            lens: "sessions" as SessionLens,
          },
          {
            label: "This Workspace",
            detail: "Sessions created in this workspace",
            lens: "workspace" as SessionLens,
          },
          {
            label: "Recent",
            detail: "The 20 most recently active sessions",
            lens: "recent" as SessionLens,
          },
        ],
        { placeHolder: "Which Maieutics sessions view should be mounted?" },
      );
      if (lens === undefined) return;

      // One Maieutics folder at a time: re-running the command with a different
      // lens replaces the mounted one instead of silently keeping it.
      const mountedIndex = vscode.workspace.workspaceFolders?.findIndex(
        (folder) => folder.uri.scheme === VfsScheme,
      ) ?? -1;
      if (mountedIndex >= 0) {
        vscode.workspace.updateWorkspaceFolders(mountedIndex, 1, {
          uri: lensFolderUri(lens.lens),
          name: `Maieutics ${lens.label}`,
        });
      } else {
        const insertAt = vscode.workspace.workspaceFolders?.length ?? 0;
        const added = vscode.workspace.updateWorkspaceFolders(insertAt, 0, {
          uri: lensFolderUri(lens.lens),
          name: `Maieutics ${lens.label}`,
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

  // A restored workspace that already contains a sessions folder must show its
  // files without the user running a command first. Best effort: an
  // unreachable server leaves the folder empty until a refresh.
  if (vscode.workspace.workspaceFolders?.some((folder) => folder.uri.scheme === VfsScheme)) {
    void syncSessions().catch((error) => output?.appendLine(`Session sync failed: ${error}`));
  }
}

export async function deactivate(): Promise<void> {
  if (connection) await connection.dispose();
  connection = undefined;
}

/** What session-scoped commands accept: an explicit id string (tree item
 * commands pass arguments explicitly) or a whole tree element (view/item/context
 * menus hand the element itself to the command). */
type CommandArgument = string | { session?: { id: string; title?: string } } | undefined;

/** The per-cell history surface: status bar badges for the commit frontier and
 * edited history, plus document-event warnings when a committed view drifts
 * from the conversation. Enforcement lives at the executeHandler and the
 * server; these stay advisory (no per-cell read-only exists upstream). */
function registerHistorySurface(): vscode.Disposable[] {
  const statusBarChanges = new vscode.EventEmitter<void>();
  const warned = new Set<string>();

  const statusProvider: vscode.NotebookCellStatusBarItemProvider = {
    provideCellStatusBarItems(cell: vscode.NotebookCell) {
      if (cell.notebook.notebookType !== NotebookType) return [];
      const state = cellHistoryState(taggedCell(cell));
      if (state === "pending") return [];

      const items: vscode.NotebookCellStatusBarItem[] = [];
      if (state === "stale") {
        const item = new vscode.NotebookCellStatusBarItem(
          "$(git-commit) edited",
          vscode.NotebookCellStatusBarAlignment.Left,
        );
        item.tooltip =
          "Edited history — running this cell continues from here on a new branch (fork).";
        items.push(item);
        return items;
      }

      const cells = cell.notebook.getCells();
      const isFrontier = frontierIndex(cells.map(taggedCell)) === cell.index;
      if (isFrontier) {
        const item = new vscode.NotebookCellStatusBarItem(
          "$(circle-filled)",
          vscode.NotebookCellStatusBarAlignment.Left,
        );
        item.tooltip = "History ends here. New cells below continue the conversation; " +
          "click to switch to a sibling branch, run this cell to fork from it.";
        item.command = {
          command: "maieutics.switchBranch",
          title: "Switch Branch",
          arguments: [cell],
        };
        items.push(item);
      }
      return items;
    },
    onDidChangeCellStatusBarItems: statusBarChanges.event,
  };

  const documents = vscode.workspace.onDidChangeNotebookDocument((event) => {
    const notebook = event.notebook;
    if (notebook.notebookType !== NotebookType) return;

    // A committed cell removed from the view: the conversation keeps it. The
    // next reopen materializes the truth from the snapshot/store.
    const removedCommitted = event.contentChanges
      .flatMap((change) => change.removedCells)
      .some((cell) => readTurnBinding(taggedCell(cell)) !== undefined);
    if (removedCommitted) {
      warnedOnce(
        warned,
        `${notebook.uri.toString()}:deleted`,
        "Maieutics: deleted cells were committed history — the conversation keeps its turns " +
          "and the saved notebook restores them.",
      );
    }

    // An edit that drifts a committed cell's text: badge via the status bar
    // (recomputed below) and warn once so the fork semantics are discoverable.
    for (const cellChange of event.cellChanges) {
      if (cellChange.document === undefined) continue;
      if (cellHistoryState(taggedCell(cellChange.cell)) !== "stale") continue;
      warnedOnce(
        warned,
        `${notebook.uri.toString()}:stale:${cellChange.cell.index}`,
        "Maieutics: edited history — running this cell continues from your edit on a new branch.",
      );
    }

    statusBarChanges.fire();
  });

  const registration = vscode.notebooks.registerNotebookCellStatusBarItemProvider(
    NotebookType,
    statusProvider,
  );
  return [statusBarChanges, registration, documents];
}

/** The queued-cells surface: per-cell "queued #N" status markers fed by the
 * projection of the server-owned session queue (never metadata, so queue
 * transitions never dirty the notebook), plus the dequeue/clear commands the
 * markers and the notebook toolbar expose. The projection holds only the
 * itemId ↔ cell map; the queue itself lives server-side. The map is rebuilt
 * from the server's snapshots for items this client enqueued and is empty
 * after an extension host reload — queued items keep running without markers
 * until the cells are run again. */
function registerQueueSurface(
  queueProjection: QueueProjection<vscode.NotebookCell>,
  queueChanges: vscode.EventEmitter<void>,
): vscode.Disposable[] {
  const statusProvider: vscode.NotebookCellStatusBarItemProvider = {
    provideCellStatusBarItems(cell: vscode.NotebookCell) {
      if (cell.notebook.notebookType !== NotebookType) return [];
      const position = queueProjection.positionOf(cell);
      if (position === undefined) return [];
      const item = new vscode.NotebookCellStatusBarItem(
        `$(clock) queued #${position}`,
        vscode.NotebookCellStatusBarAlignment.Left,
      );
      item.tooltip =
        "Waiting behind earlier cells of this notebook's queue. Click to remove it from the queue (the running cell keeps going).";
      item.command = {
        command: "maieutics.dequeueCell",
        title: "Remove from queue",
        arguments: [cell],
      };
      return [item];
    },
    onDidChangeCellStatusBarItems: queueChanges.event,
  };

  const notebookOf = (arg: unknown): vscode.NotebookDocument | undefined => {
    if (
      typeof arg === "object" && arg !== null && "notebookType" in arg &&
      "uri" in arg && "getCells" in arg
    ) return arg as vscode.NotebookDocument;
    if (arg instanceof vscode.Uri) {
      return vscode.workspace.notebookDocuments.find((document) =>
        document.uri.toString() === arg.toString()
      );
    }
    return vscode.window.activeNotebookEditor?.notebook;
  };

  return [
    queueChanges,
    vscode.notebooks.registerNotebookCellStatusBarItemProvider(
      NotebookType,
      statusProvider,
    ),
    vscode.commands.registerCommand(
      "maieutics.dequeueCell",
      async (cell?: vscode.NotebookCell) => {
        if (cell === undefined || cell.notebook.notebookType !== NotebookType) return;
        const outcome = (await controller?.dequeueQueuedCell(cell)) ?? "not-queued";
        if (outcome === "removed") return;
        if (outcome === "running") {
          await vscode.window.showInformationMessage(
            "Maieutics: this cell's turn already started — use the stop button to cancel it.",
          );
          return;
        }
        if (outcome === "failed") {
          await vscode.window.showErrorMessage(
            "Maieutics: the cell could not be removed from the queue.",
          );
          return;
        }
        await vscode.window.showInformationMessage(
          "Maieutics: this cell is not queued.",
        );
      },
    ),
    vscode.commands.registerCommand(
      "maieutics.retryTurn",
      async (arg?: unknown) => {
        // Args arrive from a failed cell's markdown command link:
        // [notebookUri, cellIndex]. Reveal the notebook, then run that one
        // cell through the normal execution pipeline (the failed cell stayed
        // pending, so this re-submits the same turn).
        const args = Array.isArray(arg) ? arg : [];
        const [uriValue, indexValue] = args as [unknown, unknown];
        if (typeof uriValue !== "string" || typeof indexValue !== "number") return;
        const document = await Promise.resolve(
          vscode.workspace.openNotebookDocument(vscode.Uri.parse(uriValue)),
        ).catch(() => undefined);
        if (document === undefined || document.notebookType !== NotebookType) return;
        await vscode.window.showNotebookDocument(document);
        const index = Math.min(indexValue, document.cellCount - 1);
        if (index < 0) return;
        await vscode.commands.executeCommand("notebook.cell.execute", {
          start: index,
          end: index + 1,
        });
      },
    ),
    vscode.commands.registerCommand(
      "maieutics.clearQueuedCells",
      async (arg?: unknown) => {
        const document = notebookOf(arg);
        if (document === undefined || document.notebookType !== NotebookType) {
          await vscode.window.showInformationMessage(
            "Maieutics: open a Maieutics notebook to clear its queue.",
          );
          return;
        }
        const removed = (await controller?.clearQueuedCells(document)) ?? 0;
        await vscode.window.showInformationMessage(
          removed === 0
            ? "Maieutics: no queued cells."
            : `Maieutics: removed ${removed} queued cell${
              removed === 1 ? "" : "s"
            }. The running cell keeps going.`,
        );
      },
    ),
  ];
}

/** The attachment surface: a per-cell "$(paperclip) Attach" status-bar item
 * on pending cells (the only cells that still submit a turn) and a read-only
 * chip counting the object-reference markers a committed cell's turn carries.
 * Purely derived from the cell text — attaching adds no metadata, so the
 * markers are exactly what gets submitted and what save/reopen restores. */
function registerAttachmentSurface(): vscode.Disposable[] {
  const statusBarChanges = new vscode.EventEmitter<void>();

  const statusProvider: vscode.NotebookCellStatusBarItemProvider = {
    provideCellStatusBarItems(cell: vscode.NotebookCell) {
      if (cell.notebook.notebookType !== NotebookType) return [];
      const attachments = parseAttachmentMarkers(cell.document.getText());
      if (cellHistoryState(taggedCell(cell)) !== "pending") {
        if (attachments.length === 0) return [];
        const item = new vscode.NotebookCellStatusBarItem(
          `$(paperclip) ${attachments.length}`,
          vscode.NotebookCellStatusBarAlignment.Left,
        );
        item.tooltip = attachmentListTooltip(attachments, "Committed with this turn:");
        return [item];
      }

      const item = new vscode.NotebookCellStatusBarItem(
        attachments.length === 0 ? "$(paperclip) Attach" : `$(paperclip) ${attachments.length}`,
        vscode.NotebookCellStatusBarAlignment.Left,
      );
      item.tooltip = attachments.length === 0
        ? "Attach files — they upload to this session's object library and the cell submits references to them."
        : attachmentListTooltip(attachments, "Attached:");
      item.command = {
        command: "maieutics.attachFile",
        title: "Attach Files",
        arguments: [cell],
      };
      return [item];
    },
    onDidChangeCellStatusBarItems: statusBarChanges.event,
  };

  const documents = vscode.workspace.onDidChangeNotebookDocument((event) => {
    if (event.notebook.notebookType !== NotebookType) return;
    // Text edits (markers added/removed, commits) and structural changes both
    // move the chips; metadata-only churn is ignored.
    if (event.contentChanges.length > 0 || event.cellChanges.length > 0) statusBarChanges.fire();
  });

  return [
    statusBarChanges,
    vscode.notebooks.registerNotebookCellStatusBarItemProvider(NotebookType, statusProvider),
    documents,
  ];
}

/** Plain-text attachment list for status-bar tooltips. */
function attachmentListTooltip(
  attachments: readonly AttachmentMarker[],
  header: string,
): string {
  return [
    header,
    ...attachments.map((attachment) => `• ${attachment.name} (${attachment.mediaType})`),
  ].join("\n");
}

/** Shows an informational toast once per key. */
function warnedOnce(warned: Set<string>, key: string, message: string): void {
  if (warned.has(key)) return;
  warned.add(key);
  void vscode.window.showInformationMessage(message);
}

/** The run-activity surface: one status-bar item while any session has a run
 * in flight (click opens the Sessions view) and a tree refresh whenever the
 * busy set changes so the spinner badges follow. */
function registerActivitySurface(
  activity: {
    busySessions(): readonly string[];
    onDidChange(listener: () => void): { dispose(): void };
  },
  tree: { refresh(): void },
): vscode.Disposable[] {
  const item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 95);
  item.name = "Maieutics Run Activity";
  item.command = "workbench.view.extension.maieutics";
  const refresh = () => {
    const busy = activity.busySessions();
    tree.refresh();
    if (busy.length === 0) {
      item.hide();
      return;
    }

    item.text = `$(sync~spin) Maieutics: ${busy.length} run${
      busy.length === 1 ? "" : "s"
    } in flight`;
    item.tooltip = "Maieutics: agent turn(s) running. Click to open the Sessions view.";
    item.show();
  };
  refresh();
  return [item, activity.onDidChange(refresh)];
}

/** The active-task quick pick (ADR 0028): lists the session's task-plane
 * resources — subagent child runs and timed-out one-shot terminal commands —
 * cancels working ones, and shows settled ones' bounded detail. Every action
 * re-opens the pick so the next screen is a fresh server read, not a local
 * guess; Esc leaves the surface. */
async function runTaskQuickPick(client: FrontendClient, sessionId: string): Promise<void> {
  while (true) {
    const tasks = await client.listTasks(sessionId).then(
      (found) => found,
      (error: unknown) => {
        output?.appendLine(`Task list failed: ${error}`);
        return undefined;
      },
    );
    if (tasks === undefined) {
      // Most likely a server without the task plane yet, but any transport
      // failure lands here too — the output channel carries the real cause.
      await vscode.window.showErrorMessage(
        "Maieutics: the task list is unavailable on this server.",
      );
      return;
    }
    if (tasks.length === 0) {
      await vscode.window.showInformationMessage(`Maieutics: no active tasks. ${TasksEmptyHint}`);
      return;
    }

    const picked = await vscode.window.showQuickPick(taskPicks(tasks), {
      placeHolder: `Active tasks of session ${
        sessionId.slice(0, 12)
      } — pick a working task to cancel it, a settled one for detail`,
    });
    if (picked === undefined) return;
    if (picked.action.kind === "detail") {
      await showTaskDetail(picked.action.task);
      continue;
    }
    await cancelPickedTask(client, sessionId, picked.action.task);
  }
}

/** Cancels one picked task. Agent children cancel through the run endpoint,
 * which already resolves live child runs; every other authority goes through
 * the task plane's uniform cancel, so an unknown future kind still cancels by
 * URI. The wait is cooperative and may be slow (a stuck child keeps the
 * progress toast up); the refresh happens when the pick loop re-lists. */
async function cancelPickedTask(
  client: FrontendClient,
  sessionId: string,
  task: TaskListEntry,
): Promise<void> {
  if (task.kind === "terminal") {
    // Cancelling a one-shot closes its terminal session for real — confirm.
    const answer = await vscode.window.showWarningMessage(
      `Cancel one-shot terminal ${shortTaskId(task)}? This closes its terminal session.`,
      { modal: true },
      "Cancel Task",
    );
    if (answer !== "Cancel Task") return;
  }

  const cancellation = task.kind === "agent" && task.agent?.runId !== undefined
    ? client.cancelRun(task.agent.runId)
    : client.cancelTask(sessionId, task.uri);
  try {
    await vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: `Maieutics: cancelling ${shortTaskId(task)}…`,
      },
      async () => {
        await cancellation;
      },
    );
  } catch (error) {
    const message = error instanceof FrontendError ? error.message : String(error);
    await vscode.window.showErrorMessage(`Maieutics: cancel failed — ${message}`);
  }
}

/** Shows one settled task's bounded detail: a report preview goes to the
 * output channel (modals choke on the 2000-character budget), short state
 * detail stays a modal. Never fetches a child transcript — the plane does
 * not expose one. */
async function showTaskDetail(task: TaskListEntry): Promise<void> {
  const text = taskDetailText(task);
  if (task.agent?.report !== undefined) {
    output?.appendLine(`— task detail —\n${text}`);
    output?.show(false);
    return;
  }
  await vscode.window.showInformationMessage(text, { modal: true });
}

/** The per-notebook token badge: sums the provider usage carried by the
 * committed cells' structured turn snapshots (input ↑ / output ↓). Clicking
 * opens the server status. Purely derived from already-fetched outputs. */
function registerUsageBadge(): vscode.Disposable[] {
  const item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 90);
  item.name = "Maieutics Token Usage";
  item.command = "maieutics.showStatus";

  const refresh = () => {
    const editor = vscode.window.activeNotebookEditor;
    if (editor === undefined || editor.notebook.notebookType !== NotebookType) {
      item.hide();
      return;
    }

    let inputTokens = 0;
    let outputTokens = 0;
    for (const cell of editor.notebook.getCells()) {
      for (const output of cell.outputs) {
        for (const bundleItem of output.items) {
          if (bundleItem.mime !== TurnOutputMime) continue;
          try {
            const snapshot = JSON.parse(new TextDecoder().decode(bundleItem.data)) as {
              usage?: { inputTokens?: number; outputTokens?: number };
            };
            inputTokens += snapshot.usage?.inputTokens ?? 0;
            outputTokens += snapshot.usage?.outputTokens ?? 0;
          } catch {
            // A malformed snapshot contributes nothing.
          }
        }
      }
    }

    if (inputTokens === 0 && outputTokens === 0) {
      item.hide();
      return;
    }

    item.text = `$(zap) ${compactCount(inputTokens)}\u2191 ${compactCount(outputTokens)}\u2193`;
    item.tooltip = "Maieutics: provider token usage of this notebook's committed turns " +
      "(input \u2191 / output \u2193). Click for server status.";
    item.show();
  };

  return [
    item,
    vscode.window.onDidChangeActiveNotebookEditor(() => refresh()),
    vscode.workspace.onDidChangeNotebookDocument((event) => {
      const active = vscode.window.activeNotebookEditor;
      if (active !== undefined && event.notebook === active.notebook) {
        refresh();
      }
    }),
  ];
}

/** 950 → "950", 1250 → "1.3k", 2400000 → "2.4M". */
function compactCount(value: number): string {
  if (value < 1000) return String(value);
  if (value < 1_000_000) return `${(value / 1000).toFixed(1)}k`;
  return `${(value / 1_000_000).toFixed(1)}M`;
}

function unwrapSessionRef(argument: CommandArgument): string | undefined {
  if (typeof argument === "string") return argument;
  return argument?.session?.id;
}

/** Picks a session when a command carries no tree argument: the stored list
 * with display labels in server order, most recently active first. */
async function pickSession(
  client: FrontendClient,
  sessionId: string | undefined,
  placeHolder: string,
): Promise<{ session: SessionLike } | undefined> {
  if (sessionId !== undefined) {
    const stored = await client.listSessions().catch(() => []);
    const match = stored.find((session) => session.id === sessionId);
    if (match) return { session: match };
    const active = await client.session().catch(() => undefined);
    return active && active.id === sessionId
      ? { session: { id: active.id, turns: active.turns, lastActivityAt: "", title: active.title } }
      : undefined;
  }

  const sessions = await client.listSessions().catch(() => []);
  if (sessions.length === 0) {
    await vscode.window.showInformationMessage("Maieutics: no stored sessions yet.");
    return undefined;
  }
  const picked = await vscode.window.showQuickPick(
    sessions.map((session) => ({
      label: sessionLabel(session),
      description: pickDescription(session),
      session,
    })),
    { placeHolder },
  );
  return picked ? { session: picked.session } : undefined;
}

function pickDescription(session: SessionLike): string {
  const root = session.workspaceRoot === undefined
    ? "no workspace"
    : session.workspaceRoot.split(/[\\/]/).pop();
  return `${session.turns} turn(s) · ${root} · last active ${session.lastActivityAt}`;
}

async function openSessionNotebookForUser(sessionId: string): Promise<void> {
  // The open path reads through the VFS, which needs the session in its
  // descriptor cache before it can materialize the notebook on first read.
  await fsProvider?.refresh().catch((error: unknown) =>
    output?.appendLine(`Descriptor refresh failed: ${error}`)
  );
  const uri = vscode.Uri.from({
    scheme: VfsScheme,
    path: sessionNotebookPath(sessionId),
  });
  await vscode.commands.executeCommand("vscode.openWith", uri, "maieutics-notebook");
}

/** Refreshes the descriptor cache and re-lists every mounted lens. Content
 * materializes on demand when a file is opened, so this stays one HTTP call. */
async function syncSessions(): Promise<void> {
  const client = await clientOf();
  const sessions = await client.listSessions();
  fsProvider?.syncSessions(sessions);
  treeRefresh?.();
}

function lensFolderUri(lens: SessionLens): vscode.Uri {
  return vscode.Uri.from({ scheme: VfsScheme, path: `/${lens}` });
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
  void warnWorkspaceMismatchAsync(connection.client);
  return connection.client;
}

/** Warns once per connection when the server serves a different workspace
 * than this window: the protocol exposes `workspaceRoot` exactly so frontends
 * can catch a stale executable started elsewhere. */
async function warnWorkspaceMismatchAsync(client: FrontendClient): Promise<void> {
  if (workspaceMismatchWarned) return;
  workspaceMismatchWarned = true;
  try {
    const capabilities = await client.capabilities();
    const serverRoot = capabilities.workspaceRoot;
    const windowRoot = vscode.workspace.workspaceFolders?.find((folder) =>
      folder.uri.scheme === "file"
    )?.uri.fsPath;
    if (serverRoot === undefined || windowRoot === undefined) return;
    const server = vscode.Uri.file(serverRoot).fsPath.replace(/\/$/, "");
    const window_ = vscode.Uri.file(windowRoot).fsPath.replace(/\/$/, "");
    if (server !== window_) {
      await vscode.window.showWarningMessage(
        `Maieutics: the server serves workspace \`${server}\` but this window is \`${window_}\`. ` +
          "File tools follow the server's workspace; restart the server here if that is not intended.",
      );
    }
  } catch {
    // Capability probing is advisory; the first real command surfaces errors.
  }
}

let workspaceMismatchWarned = false;

async function openConnection(): Promise<Connection> {
  const settings = vscode.workspace.getConfiguration();
  const discoveryFile = settings.get<string>(DiscoveryFileSetting);
  const executablePath = settings.get<string>(ExecutablePathSetting) ?? "maieutics";
  // Only a local folder is a meaningful workspace root for the executable;
  // the mounted sessions folder is virtual.
  const workspaceRoot = vscode.workspace.workspaceFolders?.find((folder) =>
    folder.uri.scheme === "file"
  )?.uri.fsPath;
  treeEnvironment.workspaceRoot = workspaceRoot;
  treeEnvironment.caseInsensitive = platform() === "win32" || platform() === "darwin";
  const handle = await connect({
    executablePath,
    workspaceRoot,
    discoveryFile: discoveryFile || undefined,
  });
  output?.appendLine(`Connected to ${handle.client.baseUrl}.`);
  return handle;
}
