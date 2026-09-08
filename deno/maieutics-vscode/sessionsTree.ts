/**
 * Session tree view: one flat level listing the New entry, the active
 * session, and stored sessions from the server. Clicking a session opens its
 * virtual notebook; the tree refreshes from the server on demand. The server
 * is the authoritative source — the tree holds no state of its own.
 */

import * as vscode from "vscode";
import type { FrontendClient } from "./client.ts";
import type { SessionInfo, StoredSession } from "./protocol.ts";

type Element =
  | { kind: "new" }
  | { kind: "active"; session: SessionInfo }
  | { kind: "stored"; session: StoredSession };

export class MaieuticsSessionsProvider implements vscode.TreeDataProvider<Element> {
  private readonly emitter = new vscode.EventEmitter<Element | undefined>();
  readonly onDidChangeTreeData = this.emitter.event;

  constructor(private readonly clientOf: () => Promise<FrontendClient>) {}

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
      case "active": {
        const item = new vscode.TreeItem(
          element.session.id.slice(0, 12),
          vscode.TreeItemCollapsibleState.None,
        );
        item.description = `active · ${element.session.turns} turn(s)`;
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
          element.session.id.slice(0, 12),
          vscode.TreeItemCollapsibleState.None,
        );
        item.description =
          `${element.session.turns} turn(s) · last active ${element.session.lastActivityAt}`;
        item.iconPath = new vscode.ThemeIcon("history");
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
    if (element !== undefined) return [];

    const elements: Element[] = [{ kind: "new" }];
    let client: FrontendClient;
    try {
      client = await this.clientOf();
    } catch {
      // Server unreachable (or not yet launched): the New node stays usable.
      return elements;
    }

    const active = await client.session().catch(() => undefined);
    if (active === undefined) return elements;

    elements.push({ kind: "active", session: active });

    const stored = await client.listSessions().catch(() => []);
    for (const session of stored) {
      if (session.id !== active.id) elements.push({ kind: "stored", session });
    }

    return elements;
  }
}
