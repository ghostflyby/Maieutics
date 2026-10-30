/** Document-only editing. The kernel authorizes targets and computes the edit;
 * the editor owns buffer versions, undo, and the single text-edit transaction. */
import * as vscode from "vscode";
import { isCoveredLocalTarget, sha256Hex } from "./ide.ts";
import {
  dirtySaveTargets,
  type DocumentSaveTarget,
  MaximumSaveDocuments,
  type SaveableDocument,
  saveWorkspaceDocuments,
} from "./documentSaving.ts";

const MaximumDocuments = 64;
const MaximumBytes = 8 * 1024 * 1024;
const fail = (code: string, message: string) => Object.assign(new Error(message), { code });

function documentUri(value: unknown): vscode.Uri {
  if (typeof value !== "string") throw fail("ide_invalid_arguments", "A document URI is required.");
  const uri = vscode.Uri.parse(value);
  if (
    uri.scheme !== "file" || !isCoveredLocalTarget(
      uri.fsPath,
      (vscode.workspace.workspaceFolders ?? []).filter((f) => f.uri.scheme === "file").map((f) =>
        f.uri.fsPath
      ),
    )
  ) {
    throw fail(
      "ide_edit_rejected",
      "The document is outside this editor's local workspace folders.",
    );
  }
  return uri;
}

export async function executeDocumentMethod(
  method: string,
  params: Record<string, unknown>,
  signal?: AbortSignal,
): Promise<unknown> {
  if (method === "maieutics/documents/dirty") {
    signal?.throwIfAborted();
    return { documents: dirtySaveTargets(workspaceSaveDocuments()) };
  }
  if (method === "maieutics/documents/save") {
    const targets = params.documents;
    if (
      !Array.isArray(targets) || targets.length > MaximumSaveDocuments ||
      new Set(targets.map((target) => target?.uri)).size !== targets.length ||
      targets.some((target) =>
        typeof target?.uri !== "string" || !Number.isSafeInteger(target.version) ||
        target.version < 1 || !["text", "notebook"].includes(target.kind)
      )
    ) {
      throw fail(
        "ide_invalid_arguments",
        "Expected at most 256 unique document save targets with uri, version and kind.",
      );
    }
    for (const target of targets) documentUri(target.uri);
    return await saveWorkspaceDocuments(
      targets as DocumentSaveTarget[],
      workspaceSaveDocuments,
      signal,
    );
  }
  const entries = method === "maieutics/documents/read" ? params.uris : params.documents;
  if (!Array.isArray(entries) || entries.length === 0 || entries.length > MaximumDocuments) {
    throw fail("ide_invalid_arguments", "Expected 1 to 64 documents.");
  }
  let totalBytes = 0;
  const seen = new Set<string>();
  const documents = [];
  for (const entry of entries) {
    signal?.throwIfAborted();
    const uri = documentUri(method === "maieutics/documents/read" ? entry : entry?.uri);
    if (seen.has(uri.toString())) throw fail("ide_invalid_arguments", "Duplicate document target.");
    seen.add(uri.toString());
    const document = await vscode.workspace.openTextDocument(uri);
    signal?.throwIfAborted();
    const content = document.getText();
    totalBytes += new TextEncoder().encode(content).byteLength;
    if (method === "maieutics/documents/apply") {
      if (
        typeof entry.content !== "string" || !Number.isSafeInteger(entry.version) ||
        typeof entry.expectedHash !== "string"
      ) {
        throw fail("ide_invalid_arguments", "An edit requires content, version, and expectedHash.");
      }
      totalBytes += new TextEncoder().encode(entry.content).byteLength;
    }
    if (totalBytes > MaximumBytes) {
      throw fail("ide_edit_rejected", "Document transaction exceeds 8 MiB.");
    }
    documents.push({ document, content, version: document.version, entry });
  }
  if (method === "maieutics/documents/read") {
    return {
      documents: documents.map(({ entry, content, version }) => ({
        uri: entry,
        content,
        version,
      })),
    };
  }
  const edit = new vscode.WorkspaceEdit();
  for (const { document, content, version, entry } of documents) {
    if (version !== entry.version || await sha256Hex(content) !== entry.expectedHash) {
      throw fail(
        "ide_document_conflict",
        "A document changed since the edit was computed; re-read and retry.",
      );
    }
    if (content !== entry.content) {
      edit.replace(
        document.uri,
        new vscode.Range(document.positionAt(0), document.positionAt(content.length)),
        entry.content,
      );
    }
  }
  signal?.throwIfAborted();
  // No await between this final check and applyEdit. VS Code also serializes
  // the current document version into its text-only workspace transaction.
  for (const { document, version } of documents) {
    if (document.isClosed || document.version !== version) {
      throw fail(
        "ide_document_conflict",
        "A document changed while preparing the edit; re-read and retry.",
      );
    }
  }
  if (edit.size > 0 && !await vscode.workspace.applyEdit(edit)) {
    throw fail("ide_edit_rejected", "VS Code rejected the document transaction.");
  }
  return { applied: true };
}

function workspaceSaveDocuments(): SaveableDocument[] {
  const roots = (vscode.workspace.workspaceFolders ?? []).filter((folder) =>
    folder.uri.scheme === "file"
  ).map((folder) => folder.uri.fsPath);
  const covered = (uri: vscode.Uri) =>
    uri.scheme === "file" && isCoveredLocalTarget(uri.fsPath, roots);
  const documents: SaveableDocument[] = [];
  for (
    const [items, kind] of [[vscode.workspace.textDocuments, "text"], [
      vscode.workspace.notebookDocuments,
      "notebook",
    ]] as const
  ) {
    for (const document of items) {
      if (!covered(document.uri)) continue;
      documents.push({
        uri: document.uri.toString(),
        kind,
        get version() {
          return document.version;
        },
        get isDirty() {
          return document.isDirty;
        },
        get isClosed() {
          return document.isClosed;
        },
        save: () => document.save(),
      });
    }
  }
  return documents;
}
