/**
 * Session tree view: the New entry plus workspace groups of stored sessions
 * (current workspace first and expanded, others collapsed, "Without
 * workspace" last for rows written before roots were stamped). The active
 * session renders inside its group and is marked active; the tree holds no
 * state of its own — grouping lives in the pure `sessionGroups` module and
 * data comes from the server on demand.
 */

import * as vscode from "vscode";
import type { FrontendClient } from "./client.ts";
import type { SessionInfo, StoredSession } from "./protocol.ts";
import {
  groupSessions,
  type SessionGroup,
  sessionLabel,
  type SessionLike,
} from "./sessionGroups.ts";

/** Window-local facts the grouping needs; the extension owns the values. */
export interface TreeEnvironment {
  /** The current workspace root (undefined when no real folder is open). */
  workspaceRoot?: string;
  /** Whether local paths compare case-insensitively (Windows/macOS). */
  caseInsensitive: boolean;
  /** Whether the "current workspace only" filter is engaged. */
  currentOnly: boolean;
}

type Element =
  | { kind: "new" }
  | { kind: "group"; group: SessionGroup }
  | { kind: "active"; session: SessionInfo }
  | { kind: "stored"; session: StoredSession };

export class MaieuticsSessionsProvider implements vscode.TreeDataProvider<Element> {
  private readonly emitter = new vscode.EventEmitter<Element | undefined>();
  readonly onDidChangeTreeData = this.emitter.event;

  constructor(
    private readonly clientOf: () => Promise<FrontendClient>,
    private readonly environment: () => TreeEnvironment,
  ) {}

  refresh(): void {
    this.emitter.fire(undefined);
  }

  getTreeItem(element: Element): vscode.TreeItem {
    switch (element.kind) {
      case "new": {
        const item = new vscode.TreeItem("New session", vscode.TreeItemCollapsibleState.None);
        item.command = { command: "maieutics.newSession", title: "New Session" };
        item.iconPath = new vscode.ThemeIcon("add");
        item.contextValue = "maieutics.newSession";
        return item;
      }
      case "group": {
        const item = new vscode.TreeItem(
          element.group.label,
          element.group.current
            ? vscode.TreeItemCollapsibleState.Expanded
            : vscode.TreeItemCollapsibleState.Collapsed,
        );
        item.description = `${element.group.sessions.length}`;
        item.tooltip = element.group.key ?? "Sessions without a recorded workspace";
        item.iconPath = new vscode.ThemeIcon(element.group.current ? "folder-active" : "folder");
        item.contextValue = "maieutics.workspaceGroup";
        return item;
      }
      case "active": {
        const item = new vscode.TreeItem(
          element.session.title ?? element.session.id.slice(0, 12),
          vscode.TreeItemCollapsibleState.None,
        );
        item.description = `active · ${element.session.turns} turn(s)`;
        item.tooltip = element.session.id;
        item.iconPath = new vscode.ThemeIcon("circle-filled");
        item.contextValue = "maieutics.activeSession";
        item.command = {
          command: "maieutics.openSessionNotebook",
          title: "Open Session Notebook",
          arguments: [element.session.id],
        };
        return item;
      }
      case "stored": {
        const item = new vscode.TreeItem(
          sessionLabel(element.session),
          vscode.TreeItemCollapsibleState.None,
        );
        // Fork heads carry their lineage in the description; the auto-title
        // already names the branch point in the label itself.
        const lineage = element.session.parentSessionId === undefined
          ? ""
          : `↳ branch @ turn ${(element.session.forkPointSeq ?? 0) + 1} · `;
        item.description =
          `${lineage}${element.session.turns} turn(s) · last active ${element.session.lastActivityAt}`;
        item.tooltip = element.session.parentSessionId === undefined
          ? element.session.id
          : `${element.session.id} — fork of ${element.session.parentSessionId}`;
        item.iconPath = new vscode.ThemeIcon(
          element.session.parentSessionId === undefined ? "history" : "git-branch",
        );
        item.contextValue = "maieutics.storedSession";
        item.command = {
          command: "maieutics.resumeSessionFromTree",
          title: "Resume Session",
          arguments: [element.session.id],
        };
        return item;
      }
    }
  }

  async getChildren(element?: Element): Promise<Element[]> {
    if (element === undefined) return [{ kind: "new" }, ...await this.groupElements()];
    if (element.kind !== "group") return [];

    // The active session is the group's first child when it belongs here; it
    // may have zero stored turns and thus no stored element at all.
    const active = await this.fetchActive();
    const children: Element[] = [];
    if (
      active !== undefined && element.group.sessions.some((session) => session.id === active.id)
    ) {
      children.push({ kind: "active", session: active });
    }

    for (const session of element.group.sessions) {
      if (session.id === active?.id) continue;
      children.push({
        kind: "stored",
        session: session as StoredSession,
      });
    }
    return children;
  }

  /** Fetches stored sessions and folds them into workspace groups. The active
   * session joins its group (even with zero stored turns) and renders as the
   * active entry; a server failure keeps the New node usable. */
  private async groupElements(): Promise<Element[]> {
    let client: FrontendClient;
    try {
      client = await this.clientOf();
    } catch {
      return [];
    }

    const [active, stored] = await Promise.all([
      this.fetchActive(),
      client.listSessions().catch(() => [] as StoredSession[]),
    ]);
    if (active === undefined && stored.length === 0) return [];

    const environment = this.environment();
    const rows: SessionLike[] = [...stored];
    if (active !== undefined && !rows.some((session) => session.id === active.id)) {
      // The active session may have no stored row yet; stamping the current
      // root keeps it in its workspace group (and visible under the
      // current-only filter) instead of stranding it under "Without workspace".
      rows.push({
        id: active.id,
        turns: active.turns,
        lastActivityAt: new Date().toISOString(),
        title: active.title,
        workspaceRoot: environment.workspaceRoot,
      });
    }

    const groups = groupSessions(
      rows,
      environment.workspaceRoot,
      environment.caseInsensitive,
      environment.currentOnly,
    );
    // Graceful degradation: against servers whose sessions carry no roots at
    // all, the current-only filter would hide everything — show all groups.
    const effective = groups.length > 0 && groups.every((group) => !group.current)
      ? groupSessions(rows, environment.workspaceRoot, environment.caseInsensitive)
      : groups;
    return effective.map((group): Element => ({ kind: "group", group }));
  }

  private async fetchActive(): Promise<SessionInfo | undefined> {
    try {
      return await (await this.clientOf()).session();
    } catch {
      return undefined;
    }
  }
}
