/**
 * Virtual workspace for Maieutics notebooks.
 *
 * The server is the authoritative source of conversation history; a notebook
 * document opened inside VS Code is a *view* of one server session, not a
 * disk file. The virtual filesystem exposes one virtual document per session
 * (`maieutics:/sessions/<id>.maieuticsnb`), and the tree view lists the
 * sessions so the user can open one. Disk files (`.maieuticsnb` on the real
 * filesystem) remain openable as optional local snapshots — they are not the
 * primary source.
 *
 * Views are materialized from the server transcript when a session is opened
 * or when the sessions folder is mounted/synced — nothing streams live
 * transcript changes into existing files, and the server stays authoritative.
 * The `/sessions` directory can also be mounted as a workspace folder
 * (`maieutics.mountSessionsFolder`) so sessions appear in the Explorer as
 * ordinary files; deleting a file there only discards the local view (and the
 * next sync re-materializes it while the session exists on the server).
 * Only files under `/sessions` are backed; other paths are refused so the
 * store cannot accumulate entries no listing will ever show.
 */

import * as vscode from "vscode";
import { type MaieuticsNotebook, parseNotebook, serializeNotebook } from "./notebookFormat.ts";

/** The virtual filesystem scheme backing Maieutics session notebooks. */
export const VfsScheme = "maieutics";

/** The virtual workspace-folder URI that lists sessions as ordinary files
 * (mounted by the `maieutics.mountSessionsFolder` command). */
export function sessionsFolderUri(): vscode.Uri {
  return vscode.Uri.from({ scheme: VfsScheme, path: "/sessions" });
}

/** The virtual document URI for one session (pure string form; the caller
 * wraps it in vscode.Uri). */
export function sessionNotebookPath(sessionId: string): string {
  return `/sessions/${sessionId}.maieuticsnb`;
}

/** In-memory store backing the virtual filesystem. */
const files = new Map<string, Uint8Array>();

export class MaieuticsFileSystemProvider implements vscode.FileSystemProvider {
  private readonly emitter = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
  readonly onDidChangeFile = this.emitter.event;

  watch(): vscode.Disposable {
    // In-memory store: changes are pushed by the extension itself; no external
    // events to watch.
    return { dispose: () => {} };
  }

  stat(uri: vscode.Uri): vscode.FileStat {
    // The implicit directories must stat so the sessions folder can mount as
    // a workspace folder.
    if (uri.path === "/" || uri.path === "/sessions") {
      return { type: vscode.FileType.Directory, ctime: 0, mtime: 0, size: 0 };
    }

    const data = files.get(uri.toString());
    if (data === undefined) throw vscode.FileSystemError.FileNotFound(uri);
    return { type: vscode.FileType.File, ctime: 0, mtime: 0, size: data.byteLength };
  }

  readDirectory(uri: vscode.Uri): [string, vscode.FileType][] {
    if (uri.path === "/sessions") {
      return [...files.keys()]
        .filter((key) => key.startsWith(`${VfsScheme}:/sessions/`))
        .map((key) => [key.split("/").pop() ?? "", vscode.FileType.File]);
    }

    if (uri.path === "/") return [["sessions", vscode.FileType.Directory]];
    return [];
  }

  createDirectory(uri: vscode.Uri): void {
    // The two implicit directories are the only valid targets; VS Code also
    // calls this as a parent-ensure before writes, so it must not throw for
    // them.
    if (uri.path !== "/" && uri.path !== "/sessions") {
      throw vscode.FileSystemError.NoPermissions(
        "The Maieutics virtual filesystem has a fixed shape: only /sessions exists.",
      );
    }
  }

  readFile(uri: vscode.Uri): Uint8Array {
    const data = files.get(uri.toString());
    if (data === undefined) throw vscode.FileSystemError.FileNotFound(uri);
    return data;
  }

  writeFile(uri: vscode.Uri, content: Uint8Array): void {
    requireSessionEntry(uri);
    const existed = files.has(uri.toString());
    files.set(uri.toString(), content);
    this.emitter.fire([{
      uri,
      type: existed ? vscode.FileChangeType.Changed : vscode.FileChangeType.Created,
    }]);
  }

  delete(uri: vscode.Uri): void {
    if (!files.delete(uri.toString())) throw vscode.FileSystemError.FileNotFound(uri);
    this.emitter.fire([{ uri, type: vscode.FileChangeType.Deleted }]);
  }

  rename(): void {
    throw vscode.FileSystemError.NoPermissions("Renaming virtual notebooks is not supported.");
  }

  /** Convenience for the controller: replaces a session's virtual file. */
  writeSessionNotebook(sessionId: string, notebook: MaieuticsNotebook): void {
    this.writeFile(
      vscode.Uri.from({ scheme: VfsScheme, path: sessionNotebookPath(sessionId) }),
      serializeNotebook(notebook),
    );
  }

  readSessionNotebook(sessionId: string): MaieuticsNotebook | undefined {
    const data = files.get(
      vscode.Uri.from({ scheme: VfsScheme, path: sessionNotebookPath(sessionId) }).toString(),
    );
    if (data === undefined) return undefined;
    return parseNotebook(data);
  }

  copy(source: vscode.Uri, destination: vscode.Uri): void {
    const data = files.get(source.toString());
    if (data === undefined) throw vscode.FileSystemError.FileNotFound(source);
    requireSessionEntry(destination);
    const existed = files.has(destination.toString());
    files.set(destination.toString(), data);
    this.emitter.fire([{
      uri: destination,
      type: existed ? vscode.FileChangeType.Changed : vscode.FileChangeType.Created,
    }]);
  }
}

/** Session views live only under `/sessions`; entries elsewhere would be
 * invisible (readDirectory never lists them). */
function requireSessionEntry(uri: vscode.Uri): void {
  if (!uri.path.startsWith("/sessions/")) {
    throw vscode.FileSystemError.NoPermissions(
      "Only files under /sessions are backed by the Maieutics virtual filesystem.",
    );
  }
}
