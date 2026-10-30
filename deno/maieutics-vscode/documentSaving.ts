/** A bounded save barrier. Saving may succeed for some files before a later
 * failure; a failed barrier never authorizes the dependent build/test. */
export interface DocumentSaveTarget {
  readonly uri: string;
  readonly version: number;
  readonly kind: "text" | "notebook";
}

export interface SaveableDocument extends DocumentSaveTarget {
  readonly isDirty: boolean;
  readonly isClosed: boolean;
  save(): PromiseLike<boolean>;
}

export const MaximumSaveDocuments = 256;

export interface DocumentSaveResult {
  readonly status: "saved" | "failed";
  readonly saved: string[];
  readonly failedUri?: string;
  readonly reason?: "conflict" | "save_failed";
  readonly pending?: string[];
}

export function dirtySaveTargets(documents: readonly SaveableDocument[]): DocumentSaveTarget[] {
  const dirty = documents.filter((document) => document.isDirty && !document.isClosed);
  if (dirty.length > MaximumSaveDocuments) {
    throw Object.assign(new Error("Saving is bounded at 256 dirty workspace documents."), {
      code: "ide_save_failed",
    });
  }
  return dirty.map(({ uri, version, kind }) => ({ uri, version, kind }));
}

export async function saveWorkspaceDocuments(
  targets: readonly DocumentSaveTarget[],
  current: () => readonly SaveableDocument[],
  signal?: AbortSignal,
): Promise<DocumentSaveResult> {
  signal?.throwIfAborted();
  const saved: string[] = [];
  const failed = (uri: string, reason: "conflict" | "save_failed"): DocumentSaveResult => ({
    status: "failed",
    saved,
    failedUri: uri,
    reason,
    pending: [
      ...new Set([
        ...targets.map((target) => target.uri).filter((uri) => !saved.includes(uri)),
        uri,
      ]),
    ],
  });
  const documents = current();
  const dirty = dirtySaveTargets(documents);
  const requested = new Map(targets.map((target) => [target.uri, target]));
  // All targets and newly dirty files are checked before any save starts.
  for (const target of dirty) {
    const expected = requested.get(target.uri);
    if (
      expected === undefined || expected.version !== target.version || expected.kind !== target.kind
    ) return failed(target.uri, "conflict");
  }
  for (const target of targets) {
    const document = documents.find((document) =>
      document.uri === target.uri && document.kind === target.kind
    );
    if (document === undefined || document.isClosed || document.version !== target.version) {
      return failed(target.uri, "conflict");
    }
  }
  for (const target of targets) {
    signal?.throwIfAborted();
    const document = current().find((document) =>
      document.uri === target.uri && document.kind === target.kind
    );
    if (document === undefined || document.isClosed || document.version !== target.version) {
      return failed(target.uri, "conflict");
    }
    if (document.isDirty) {
      let success: boolean;
      try {
        success = await document.save();
      } catch {
        signal?.throwIfAborted();
        return failed(target.uri, "save_failed");
      }
      signal?.throwIfAborted();
      if (!success) return failed(target.uri, "save_failed");
      saved.push(target.uri);
      if (document.isDirty || document.isClosed) return failed(target.uri, "conflict");
      continue;
    }
    saved.push(target.uri);
  }
  // Save participants can edit other buffers, and users can edit during a save.
  // Neither case may silently turn a successful barrier into a stale build.
  const remaining = dirtySaveTargets(current());
  return remaining.length === 0 ? { status: "saved", saved } : failed(remaining[0].uri, "conflict");
}
