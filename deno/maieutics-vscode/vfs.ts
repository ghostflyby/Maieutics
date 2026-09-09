/**
 * Virtual workspace for Maieutics notebooks: lens directories over one
 * canonical per-session view.
 *
 * The server is the authoritative source of conversation history; a notebook
 * document opened inside VS Code is a *view* of one server session, not a
 * disk file. The namespace is a small catalog of derived, overlapping lenses
 * over the same session set — the session id inside the filename is the
 * canonical handle (`vfsPaths.ts` carries the pure rules):
 *
 *     maieutics:/sessions/<id>.maieuticsnb   every stored session (legacy shape)
 *     maieutics:/workspace/<id>.maieuticsnb  sessions of the current workspace
 *     maieutics:/recent/<id>.maieuticsnb     the 20 most recently active
 *
 * One session appearing under several lenses is hardlink semantics, so the
 * content map is keyed by session id (one byte sequence per session) and lens
 * URIs are aliases. Listings come from the descriptor cache (refreshed by
 * sync); validity keys on the known session id, not lens membership, so an
 * entry that drops out of a lens keeps its open editor working. Content
 * materializes on demand at first read — a mount costs one `listSessions`
 * call, not one transcript fetch per session. Only lens-rooted well-formed
 * entries are backed; anything else is refused so the store cannot accumulate
 * entries no listing will ever show.
 */

import * as vscode from "vscode";
import { type MaieuticsNotebook, serializeNotebook } from "./notebookFormat.ts";
import type { StoredSession } from "./protocol.ts";
import {
  isLensDirectory,
  lensMembers,
  parseSessionEntry,
  type SessionLens,
  SessionLenses,
} from "./vfsPaths.ts";

/** The virtual filesystem scheme backing Maieutics session notebooks. */
export const VfsScheme = "maieutics";

export type { SessionLens };
export { SessionLenses };

/** The virtual document path for one session (the canonical handle; the
 * caller wraps it in vscode.Uri). */
export function sessionNotebookPath(sessionId: string): string {
  return `/sessions/${sessionId}.maieuticsnb`;
}

/** Host callbacks the provider needs from the extension. */
export interface VfsHost {
  listSessions(): Promise<StoredSession[]>;
  /** Builds the notebook view of one session (a server transcript fetch). */
  fetchTranscript(sessionId: string): Promise<MaieuticsNotebook>;
  /** The current window's workspace facts for the workspace lens. */
  currentWorkspace(): { root?: string; caseInsensitive: boolean };
}

function entryUri(lens: SessionLens, sessionId: string): vscode.Uri {
  return vscode.Uri.from({ scheme: VfsScheme, path: `/${lens}/${sessionId}.maieuticsnb` });
}

function lensUri(lens: SessionLens): vscode.Uri {
  return vscode.Uri.from({ scheme: VfsScheme, path: `/${lens}` });
}

function directoryStat(): vscode.FileStat {
  return { type: vscode.FileType.Directory, ctime: 0, mtime: 0, size: 0 };
}

export class MaieuticsFileSystemProvider implements vscode.FileSystemProvider {
  private readonly emitter = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
  readonly onDidChangeFile = this.emitter.event;

  /** Canonical content, keyed by session id: lens URIs are aliases of these
   * bytes, never independent copies. */
  private readonly content = new Map<string, Uint8Array>();
  /** Known sessions from the last sync, by id. */
  private sessions = new Map<string, StoredSession>();
  /** Server-stamped descriptors cannot advance on local writes; this records
   * when a session's canonical content last changed so `stat` mtime moves and
   * editors trust change events. */
  private readonly writtenAt = new Map<string, number>();

  constructor(private readonly host: VfsHost) {}

  watch(): vscode.Disposable {
    // In-memory store: changes are pushed by the extension itself; no external
    // events to watch.
    return { dispose: () => {} };
  }

  stat(uri: vscode.Uri): vscode.FileStat {
    if (isLensDirectory(uri.path)) return directoryStat();

    const entry = parseSessionEntry(uri.path);
    if (entry === null || !this.sessions.has(entry.sessionId)) {
      throw vscode.FileSystemError.FileNotFound(uri);
    }
    const descriptor = this.sessions.get(entry.sessionId);
    const written = this.writtenAt.get(entry.sessionId);
    return {
      type: vscode.FileType.File,
      ctime: descriptor === undefined ? 0 : Date.parse(descriptor.createdAt),
      mtime: written ?? (descriptor === undefined ? 0 : Date.parse(descriptor.lastActivityAt)),
      size: this.content.get(entry.sessionId)?.byteLength ?? 0,
    };
  }

  readDirectory(uri: vscode.Uri): [string, vscode.FileType][] {
    if (uri.path === "/") {
      return SessionLenses.map((lens) => [lens, vscode.FileType.Directory]);
    }

    const lens = uri.path.slice(1) as SessionLens;
    if (!SessionLenses.includes(lens)) return [];

    return this.members(lens)
      .map((session) => [`${session.id}.maieuticsnb`, vscode.FileType.File]);
  }

  createDirectory(uri: vscode.Uri): void {
    // The lens roots are the only valid targets; VS Code also calls this as a
    // parent-ensure before writes, so it must not throw for them.
    if (!isLensDirectory(uri.path)) {
      throw vscode.FileSystemError.NoPermissions(
        "The Maieutics virtual filesystem has a fixed shape: only the session lens directories exist.",
      );
    }
  }

  async readFile(uri: vscode.Uri): Promise<Uint8Array> {
    const entry = parseSessionEntry(uri.path);
    if (entry === null) throw vscode.FileSystemError.FileNotFound(uri);

    const cached = this.content.get(entry.sessionId);
    if (cached !== undefined) return cached;

    // Lazy materialization: opening a file costs exactly the transcript it
    // renders, never a speculative fetch. A session absent from the cache (a
    // just-created session with no stored row yet) still gets its one server
    // chance — the server, not the cache, decides what exists.
    try {
      const notebook = await this.host.fetchTranscript(entry.sessionId);
      const bytes = serializeNotebook(notebook);
      this.content.set(entry.sessionId, bytes);
      if (!this.sessions.has(entry.sessionId)) {
        const now = new Date().toISOString();
        this.sessions.set(entry.sessionId, {
          id: entry.sessionId,
          turns: 0,
          createdAt: now,
          lastActivityAt: now,
        });
      }
      return bytes;
    } catch (error) {
      // Fetch failures (unknown session, unreachable server) surface as the
      // filesystem's own not-found error, keeping the cause in the message.
      const cause = error instanceof Error ? error.message : String(error);
      throw vscode.FileSystemError.FileNotFound(`${uri.path} (${cause})`);
    }
  }

  writeFile(uri: vscode.Uri, content: Uint8Array): void {
    const entry = parseSessionEntry(uri.path);
    if (entry === null || !this.sessions.has(entry.sessionId)) {
      throw vscode.FileSystemError.NoPermissions(
        "Only files naming a known Maieutics session under a lens directory are backed.",
      );
    }
    this.content.set(entry.sessionId, content);
    this.writtenAt.set(entry.sessionId, Date.now());
    // One canonical view, several lens aliases: editors open on any of them
    // must see the same bytes, so every alias hears the change.
    this.fireSessionChange(entry.sessionId, vscode.FileChangeType.Changed);
  }

  delete(uri: vscode.Uri): void {
    const entry = parseSessionEntry(uri.path);
    if (entry === null || !this.sessions.has(entry.sessionId)) {
      throw vscode.FileSystemError.FileNotFound(uri);
    }

    // Deleting only discards the local view (the server stays authoritative);
    // the content cache evicts for every lens at once.
    this.content.delete(entry.sessionId);
    this.writtenAt.delete(entry.sessionId);
    this.fireSessionChange(entry.sessionId, vscode.FileChangeType.Deleted);
  }

  rename(): void {
    throw vscode.FileSystemError.NoPermissions(
      "Renaming is refused: the session id in the filename is the canonical handle (v1).",
    );
  }

  copy(): void {
    throw vscode.FileSystemError.NoPermissions(
      "Copying is refused: sessions are created on the server, and duplicated ids are meaningless.",
    );
  }

  /** Replaces the descriptor cache and re-lists every lens: one Changed event
   * per lens directory is what makes Explorer refresh its listing. */
  syncSessions(sessions: StoredSession[]): void {
    this.sessions = this.mergeDescriptors(sessions);
    this.emitter.fire(
      SessionLenses.map((lens) => ({
        uri: lensUri(lens),
        type: vscode.FileChangeType.Changed,
      })),
    );
  }

  /** Refreshes the descriptor cache without firing lens events; the open path
   * awaits this so readFile finds the session even before any mount synced. */
  async refresh(): Promise<void> {
    this.sessions = this.mergeDescriptors(await this.host.listSessions());
  }

  /** Drops cached descriptors and views after a server restart; the next sync
   * or open rebuilds them against the new process. */
  reset(): void {
    this.sessions.clear();
    this.content.clear();
    this.writtenAt.clear();
  }

  /** The fresh server list wins, but sessions that only exist locally (a
   * just-created active session registered by readFile) survive until the
   * server knows them — dropping them would break open editors' stat. */
  private mergeDescriptors(sessions: StoredSession[]): Map<string, StoredSession> {
    const merged = new Map(sessions.map((session) => [session.id, session]));
    for (const [id, descriptor] of this.sessions) {
      if (!merged.has(id) && this.content.has(id)) merged.set(id, descriptor);
    }
    return merged;
  }

  /** Fires one event per lens alias of the session (at most three). */
  private fireSessionChange(sessionId: string, type: vscode.FileChangeType): void {
    this.emitter.fire(
      SessionLenses.map((lens) => ({ uri: entryUri(lens, sessionId), type })),
    );
  }

  /** The descriptor list one lens shows, newest first. */
  private members(lens: SessionLens): StoredSession[] {
    return lensMembers(lens, [...this.sessions.values()], this.host.currentWorkspace());
  }
}
