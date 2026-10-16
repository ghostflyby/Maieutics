/** IDE capability bridge (ADR 0042 stage A) — pure half.
 *
 * The kernel announces an `ide.request` as an `application/json` tool-progress
 * item on the run's event stream (`{ideRequest: {requestId, method, params}}`),
 * and answers travel back through `POST /v1/agent/ide/{requestId}`. The URIs
 * arriving here are already physical `file://` addresses and positions are
 * already LSP 0-based — the kernel is the translation point for the agent's
 * `workspace://local` / 1-based conventions — so this side dispatches, filters
 * Problems to the workspace, normalizes hover contents, and returns data
 * verbatim. Pure: no vscode imports, so the dispatch table and normalizers are
 * unit-testable; the executor that touches `vscode.*` lives in controller.ts. */

import type { EventFrame } from "./protocol.ts";

/** The IDE capability version announced on the events upgrade and accepted
 * kernel-side; announced versions must match exactly. */
export const IdeCapabilityVersion = 1;

/** One kernel-announced IDE request pulled out of a tool-progress frame. */
export interface IdeRequestPayload {
  readonly requestId: string;
  readonly method: string;
  readonly params: Record<string, unknown>;
}

/** The read-group catalog (ADR 0042 decision 3) mapped to the VS Code built-in
 * provider commands. Write methods are deliberately absent: executing them here
 * would bypass the kernel's gated edit path. Forwarding raw command names is a
 * red line — only this fixed table dispatches. */
export const ideMethodCommands: Readonly<Record<string, string>> = {
  "textDocument/hover": "vscode.executeHoverProvider",
  "textDocument/definition": "vscode.executeDefinitionProvider",
  "textDocument/typeDefinition": "vscode.executeTypeDefinitionProvider",
  "textDocument/declaration": "vscode.executeDeclarationProvider",
  "textDocument/implementation": "vscode.executeImplementationProvider",
  "textDocument/references": "vscode.executeReferenceProvider",
  "textDocument/documentHighlight": "vscode.executeDocumentHighlightProvider",
  "textDocument/documentSymbol": "vscode.executeDocumentSymbolProvider",
  "textDocument/signatureHelp": "vscode.executeSignatureHelpProvider",
  "textDocument/foldingRange": "vscode.executeFoldingRangeProvider",
  "textDocument/inlayHint": "vscode.executeInlayHintProvider",
  "textDocument/selectionRange": "vscode.executeSelectionRangeProvider",
  "textDocument/semanticTokens/full": "vscode.executeDocumentSemanticTokensProvider",
  "workspace/symbol": "vscode.executeWorkspaceSymbolProvider",
  "callHierarchy/prepare": "vscode.prepareCallHierarchy",
  "callHierarchy/incomingCalls": "vscode.provideIncomingCalls",
  "callHierarchy/outgoingCalls": "vscode.provideOutgoingCalls",
  "typeHierarchy/prepare": "vscode.prepareTypeHierarchy",
  "typeHierarchy/supertypes": "vscode.provideSupertypes",
  "typeHierarchy/subtypes": "vscode.provideSubtypes",
  // Write group (ADR 0042 stage C): compute-only — the commands answer with
  // data (WorkspaceEdit/CodeAction/TextEdit shapes) and this side never
  // applies anything; the kernel's gated write path is the only writer.
  "textDocument/rename": "vscode.executeDocumentRenameProvider",
  "textDocument/codeAction": "vscode.executeCodeActionProvider",
  "textDocument/formatting": "vscode.executeFormatDocumentProvider",
};

/** Whether the method dispatches without the special-cased Problems path. */
export function isIdeMethodSupported(method: string): boolean {
  return method === "maieutics/problems" || method in ideMethodCommands;
}

/** Builds one provider command's argument list from the LSP-shaped params.
 * URI values stay strings and hierarchy items stay plain objects — the
 * vscode-side executor materializes them into vscode.Uri / provider items. */
export function ideCommandArgs(
  method: string,
  params: Record<string, unknown>,
): unknown[] {
  const uri = stringOf(params.uri) ?? stringOf(textDocumentOf(params)?.uri);
  const position = objectOf(params.position);
  switch (method) {
    case "textDocument/foldingRange":
    case "textDocument/semanticTokens/full":
    case "textDocument/documentSymbol":
      return [uri];
    case "textDocument/hover":
    case "textDocument/definition":
    case "textDocument/typeDefinition":
    case "textDocument/declaration":
    case "textDocument/implementation":
    case "textDocument/documentHighlight":
    case "textDocument/signatureHelp":
    case "callHierarchy/prepare":
      return [uri, position];
    case "textDocument/inlayHint":
      return [uri, objectOf(params.range)];
    case "textDocument/references":
      return [uri, position, objectOf(params.context) ?? { includeDeclaration: true }];
    case "textDocument/selectionRange":
      return [uri, Array.isArray(params.positions) ? params.positions : []];
    case "workspace/symbol":
      return [stringOf(params.query) ?? ""];
    case "callHierarchy/incomingCalls":
    case "callHierarchy/outgoingCalls":
    case "typeHierarchy/supertypes":
    case "typeHierarchy/subtypes":
      return [objectOf(params.item)];
    case "textDocument/rename":
      return [uri, position, stringOf(params.newName) ?? ""];
    case "textDocument/codeAction":
      return [uri, position, objectOf(params.context) ?? { diagnostics: [] }];
    case "textDocument/formatting":
      return [uri, objectOf(params.options) ?? { tabSize: 4, insertSpaces: true }];
    default:
      return [];
  }
}

/** Pulls the `ide.request` payload out of its two announced forms: the
 * sequenced `tool.progress` content item an agent run publishes, and the
 * standalone `ide.request` frame the kernel pushes for script/plugin
 * calls (unordered, not replayed — stage D). Undefined for every other
 * frame or shape. */
export function extractIdeRequest(frame: EventFrame): IdeRequestPayload | undefined {
  if (frame.type === "ide.request") {
    const data = frame.data;
    if (typeof data !== "object" || data === null) return undefined;
    const record = data as Record<string, unknown>;
    if (typeof record.requestId !== "string" || typeof record.method !== "string") return undefined;
    const params = typeof record.params === "object" && record.params !== null
      ? record.params as Record<string, unknown>
      : {};
    return { requestId: record.requestId, method: record.method, params };
  }

  if (frame.type !== "tool.progress") return undefined;
  const content = frame.content;
  if (content === undefined || content.kind !== "json") return undefined;
  const value = content.value;
  if (typeof value !== "object" || value === null) return undefined;
  const request = (value as Record<string, unknown>).ideRequest;
  if (typeof request !== "object" || request === null) return undefined;
  const record = request as Record<string, unknown>;
  if (typeof record.requestId !== "string" || typeof record.method !== "string") return undefined;
  const params = typeof record.params === "object" && record.params !== null
    ? record.params as Record<string, unknown>
    : {};
  return { requestId: record.requestId, method: record.method, params };
}

/** LSP MarkupContent shape the kernel contract normalizes hover contents to. */
export interface MarkupContent {
  readonly kind: "markdown" | "plaintext";
  readonly value: string;
}

/** Normalizes one hover's `contents` — VS Code accepts a MarkdownString, a
 * MarkedString, or an array of both — into a single MarkupContent (ADR 0042
 * decision 4). Untyped payloads pass through as markdown. */
export function normalizeHoverContents(contents: unknown): MarkupContent {
  const parts = Array.isArray(contents) ? contents : [contents];
  const markdown: string[] = [];
  const plaintext: string[] = [];
  for (const part of parts) {
    if (typeof part === "string") {
      plaintext.push(part);
    } else if (typeof part === "object" && part !== null) {
      const record = part as Record<string, unknown>;
      if (typeof record.value === "string") {
        if (typeof record.language === "string" && record.language.length > 0) {
          markdown.push(`\`\`\`${record.language}\n${record.value}\n\`\`\``);
        } else {
          markdown.push(record.value);
        }
      }
    }
  }

  if (markdown.length > 0) return { kind: "markdown", value: markdown.join("\n\n") };
  if (plaintext.length > 0) return { kind: "plaintext", value: plaintext.join("\n\n") };
  return { kind: "plaintext", value: "" };
}

/** The normalized wire shape of one hover answer (ADR 0042 decision 4):
 * LSP-shaped single hover with MarkupContent contents. */
export interface HoverAnswer {
  readonly contents: MarkupContent;
  readonly range?: unknown;
}

/** Folds the raw `vscode.executeHoverProvider` result into the wire answer.
 * The provider command answers `Hover[]` (one entry per registered provider);
 * a lone hover-shaped object is accepted too. The answer is the first hover
 * carrying contents, normalized; `undefined` when nothing carries a payload
 * (no provider has answered yet), so the caller passes the raw result through
 * and polling callers keep seeing "not yet". */
export function normalizedHoverAnswer(result: unknown): HoverAnswer | undefined {
  const hover = Array.isArray(result)
    ? result.find((entry): entry is Record<string, unknown> =>
      typeof entry === "object" && entry !== null && "contents" in entry
    )
    : typeof result === "object" && result !== null && "contents" in result
    ? result as Record<string, unknown>
    : undefined;
  if (hover === undefined) return undefined;
  return {
    contents: normalizeHoverContents(hover.contents),
    ...(hover.range !== undefined ? { range: hover.range } : {}),
  };
}

/** Plain-diagnostic shape the Problems answer carries (LSP-compatible: severity
 * number, 0-based range, message, optional source and code). */
export interface DiagnosticJson {
  severity: number;
  range: {
    start: { line: number; character: number };
    end: { line: number; character: number };
  };
  message: string;
  source?: string;
  code?: string;
}

/** The `maieutics/problems` answer: per-document diagnostics, filtered to the
 * workspace (ADR 0042 decision 4 — the kernel re-checks containment, but the
 * out-of-workspace entries must never reach it), plus how many documents the
 * editor currently tracks diagnostics for: zero means no provider has reported
 * anything yet, which the kernel surfaces as "unknown state", not "clean". */
export interface ProblemsAnswer {
  documents: { uri: string; diagnostics: DiagnosticJson[] }[];
  analyzedDocuments: number;
}

type DiagnosticEntry = [uri: { fsPath?: string; toString(): string }, diagnostics: unknown];

/** Filters the editor's diagnostics map to workspace-contained documents and
 * flattens it to the wire shape. `wanted` narrows to one document. */
export function collectProblems(
  entries: readonly DiagnosticEntry[],
  wanted: string | undefined,
  workspaceRoots: readonly string[],
): ProblemsAnswer {
  const documents: ProblemsAnswer["documents"] = [];
  for (const [uri, diagnostics] of entries) {
    const text = uri.toString();
    if (wanted !== undefined && text !== wanted) continue;
    if (!isInWorkspace(uri, workspaceRoots)) continue;
    documents.push({
      uri: text,
      diagnostics: Array.isArray(diagnostics)
        ? diagnostics.map(diagnosticOf).filter((d): d is DiagnosticJson => d !== undefined)
        : [],
    });
  }

  return { documents, analyzedDocuments: entries.length };
}

function isInWorkspace(uri: { fsPath?: string }, workspaceRoots: readonly string[]): boolean {
  const path = uri.fsPath;
  if (typeof path !== "string" || workspaceRoots.length === 0) return false;
  return workspaceRoots.some((root) =>
    path === root ||
    path.startsWith(root.endsWith("/") || root.endsWith("\\") ? root : `${root}/`) ||
    path.startsWith(root.endsWith("/") || root.endsWith("\\") ? root : `${root}\\`)
  );
}

function diagnosticOf(value: unknown): DiagnosticJson | undefined {
  if (typeof value !== "object" || value === null) return undefined;
  const record = value as Record<string, unknown>;
  const range = objectOf(record.range);
  const start = objectOf(range?.start);
  const end = objectOf(range?.end);
  if (
    typeof record.message !== "string" || range === undefined || start === undefined ||
    end === undefined || typeof start.line !== "number" || typeof start.character !== "number" ||
    typeof end.line !== "number" || typeof end.character !== "number"
  ) {
    return undefined;
  }

  return {
    severity: typeof record.severity === "number" ? record.severity : 2,
    range: {
      start: { line: start.line, character: start.character },
      end: { line: end.line, character: end.character },
    },
    message: record.message,
    ...(typeof record.source === "string" ? { source: record.source } : {}),
    ...(record.code !== undefined && record.code !== null ? { code: String(record.code) } : {}),
  };
}

function textDocumentOf(params: Record<string, unknown>): Record<string, unknown> | undefined {
  return objectOf(params.textDocument);
}

function objectOf(value: unknown): Record<string, unknown> | undefined {
  return typeof value === "object" && value !== null ? value as Record<string, unknown> : undefined;
}

function stringOf(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

/** The `maieutics/dirty` answer entry: the requested document with its
 * unsaved-buffer flag as the editor reports it. */
export interface DirtyEntry {
  readonly uri: string;
  readonly dirty: boolean;
}

/** Pure shape for one open text document the executor sees. */
export interface DirtyDocument {
  readonly fsPath: string;
  readonly isDirty: boolean;
}

/** Answers the dirty query (ADR 0042 stage C, decision 7): one entry per
 * requested URI; a document the editor doesn't hold open is on-disk state
 * and therefore clean by construction. Matching is by fsPath so the kernel's
 * physical file URI form meets the editor's canonical form. */
export function collectDirty(
  documents: readonly DirtyDocument[],
  requestedUris: readonly string[],
  fsPathOf: (uri: string) => string,
): DirtyEntry[] {
  return requestedUris.map((uri) => {
    const path = fsPathOf(uri);
    const document = documents.find((candidate) => candidate.fsPath === path);
    return { uri, dirty: document?.isDirty === true };
  });
}

/** LSP wire shape of one text edit (the kernel contract's 0-based form). */
export interface WireTextEdit {
  readonly range: {
    readonly start: { line: number; character: number };
    readonly end: { line: number; character: number };
  };
  readonly newText: string;
}

/** Flattens a vscode.WorkspaceEdit into the LSP `changes` map. File-level
 * operations (create/rename file entries) have no public reader in the
 * editor's API and are absent here — a documented stage-C limitation
 * (structural refactors that move code into new files lose those ops). */
export function workspaceEditToWire(
  edit: { entries(): readonly (readonly [unknown, unknown])[] },
): { changes: Record<string, WireTextEdit[]> } {
  const changes: Record<string, WireTextEdit[]> = {};
  for (const [uri, edits] of edit.entries()) {
    const list = Array.isArray(edits) ? edits : [];
    changes[String(uri)] = list.map((entry) => {
      const textEdit = entry as { range?: { start?: unknown; end?: unknown }; newText?: unknown };
      const range = textEdit.range as { start?: unknown; end?: unknown } | undefined;
      return {
        range: {
          start: { ...(range?.start as { line: number; character: number }) },
          end: { ...(range?.end as { line: number; character: number }) },
        },
        newText: typeof textEdit.newText === "string" ? textEdit.newText : "",
      };
    });
  }
  return { changes };
}

/** Flattens one compute answer's code actions into plain data: title, kind,
 * the carried edit (data only), and the command's title. Commands stay
 * unexecutable — the kernel applies edits, never commands. */
export function codeActionsToWire(actions: readonly unknown[]): unknown[] {
  return actions.map((action) => {
    if (typeof action !== "object" || action === null) return { title: String(action) };
    const record = action as Record<string, unknown>;
    const kind = record.kind as { value?: unknown } | undefined;
    const command = record.command as { title?: unknown } | undefined;
    const edit = record.edit as { entries(): readonly (readonly [unknown, unknown])[] } | undefined;
    return {
      title: typeof record.title === "string" ? record.title : String(record.title),
      ...(kind && typeof kind.value === "string" ? { kind: kind.value } : {}),
      ...(edit ? { edit: workspaceEditToWire(edit) } : {}),
      ...(command && typeof command.title === "string" ? { commandTitle: command.title } : {}),
    };
  });
}

/** Flattens the formatting family's TextEdit[] into the wire shape. */
export function textEditsToWire(edits: readonly unknown[]): WireTextEdit[] {
  return edits.map((entry) => {
    const textEdit = entry as { range?: { start?: unknown; end?: unknown }; newText?: unknown };
    const range = textEdit.range as { start?: unknown; end?: unknown } | undefined;
    return {
      range: {
        start: { ...(range?.start as { line: number; character: number }) },
        end: { ...(range?.end as { line: number; character: number }) },
      },
      newText: typeof textEdit.newText === "string" ? textEdit.newText : "",
    };
  });
}

// ---------- stage E: virtual file systems (decision 11) ----------

/** One registered workspace folder: the opaque id the facade addresses, plus
 * the folder's platform URI (audit/orientation — never an addressing key). */
export interface IdeFolderEntry {
  readonly id: string;
  readonly uri: string;
}

/** The folder-id registry: ids are `<nonce><counter>` — an activation nonce
 * makes different activations' id spaces disjoint (a foreign id fails typed
 * instead of silently resolving elsewhere), the counter never reuses retired
 * values, and an id binds to its folder for life at first listing. */
export class IdeFolderRegistry {
  private readonly nonce: string;
  private counter = 0;
  private readonly byId = new Map<string, string>();

  constructor(nonce?: string) {
    this.nonce = nonce ?? IdeFolderRegistry.randomNonce();
  }

  private static randomNonce(): string {
    const alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    const cryptoObject =
      (globalThis as { crypto?: { getRandomValues(values: Uint32Array): void } }).crypto;
    if (cryptoObject) {
      const values = new Uint32Array(8);
      cryptoObject.getRandomValues(values);
      let random = "";
      for (const value of values) random += alphabet[value % alphabet.length];
      return random;
    }

    // Hosts without webcrypto: a time-mixed nonce is weaker than webcrypto
    // but still collides only across activations started in the same
    // millisecond with identical counters.
    let mixed = Date.now().toString(36);
    mixed = mixed.slice(-8).padStart(8, "0");
    return mixed;
  }

  /** Binds one folder URI to a fresh id at first listing; ids never renumber
   * and retired ids are never reused. Returns the assigned id. */
  register(uriString: string): string {
    for (const [id, known] of this.byId) {
      if (known === uriString) return id;
    }
    this.counter += 1;
    const id = `${this.nonce}${this.counter.toString(36)}`;
    this.byId.set(id, uriString);
    return id;
  }

  /** The current id+uri listing (root-call answer). */
  list(): IdeFolderEntry[] {
    return [...this.byId.entries()].map(([id, uri]) => ({ id, uri }));
  }

  /** The folder URI an id was bound to; undefined for unknown ids. */
  resolve(id: string): string | undefined {
    return this.byId.get(id);
  }
} /** The 36-base id shape the frontend assigns: activation nonce + counter. */
export function isValidIdeFolderId(id: string): boolean {
  return /^[a-z0-9]{3,24}$/.test(id);
}

/** Validates one relative segment with the workspace-path rules the kernel
 * enforces on the idefs domain (defense in depth before Uri.joinPath, which
 * normalizes dot segments — an unvalidated ".." would escape the folder). */
export function isValidIdeFsSegment(segment: string): boolean {
  return (
    segment.length > 0 &&
    segment !== "." && segment !== ".." &&
    !segment.includes("/") && !segment.includes("\\") &&
    !segment.includes("\u0000") &&
    ![...segment].some((character) => character < " ")
  );
}

/** The fs/list answer for the root call. */
export function fsRootsAnswer(registrations: readonly IdeFolderEntry[]): unknown {
  return { folders: registrations };
}

/** One entry of a directory listing, already flattened to the wire shape. */
export interface FsEntry {
  readonly name: string;
  readonly isDirectory: boolean;
}

/** The fs/list answer for a folder call: entries sorted by name with the
 * keyset cursor applied (strictly-after `cursor`), so pagination is stable
 * across concurrent modifications of the same folder. */
export function fsEntriesAnswer(
  entries: readonly FsEntry[],
  cursor: string | undefined,
): { entries: FsEntry[]; nextCursor?: string } {
  const sorted = [...entries].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const effective = cursor !== undefined ? sorted.filter((entry) => entry.name > cursor) : sorted;
  const page = effective.slice(0, 200);
  const next = page.length === 200 ? page[page.length - 1]?.name : undefined;
  return next === undefined ? { entries: page } : { entries: page, nextCursor: next };
}

// ---------- stage F: workspace file-system forwarding (decision 12) ----------

/** The vscode.FileType bit values, mirrored here so the pure half never
 * imports vscode: File=1, Directory=2, SymbolicLink=64. */
export const LocalFileType = { File: 1, Directory: 2, SymbolicLink: 64 } as const;

/** Maps a vscode.FileType bit set onto the workspace tools' kind string;
 * directory wins over the symbolic-link bit, matching the kernel's local
 * listing (a link to a directory lists as a directory it refuses to traverse). */
export function mapLocalFileTypeKind(fileType: number): string {
  if ((fileType & LocalFileType.Directory) !== 0) return "directory";
  if ((fileType & LocalFileType.SymbolicLink) !== 0) return "symbolic_link";
  if ((fileType & LocalFileType.File) !== 0) return "file";
  return "other";
}

/** One entry of a bridged workspace directory listing. */
export interface LocalFsEntry {
  readonly name: string;
  readonly kind: string;
}

/** The fs/local/list answer: names sorted, bounded by maximumEntries, with
 * the overflow reported as `truncated` — the kernel pages locally and turns
 * the truncation into its own typed directory-too-large failure. */
export function localFsEntriesAnswer(
  listed: readonly (readonly [string, number])[],
  maximumEntries: number,
): { entries: LocalFsEntry[]; truncated: boolean } {
  const bound = Math.max(0, Math.floor(maximumEntries));
  const sorted = [...listed].sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0));
  return {
    entries: sorted.slice(0, bound).map(([name, fileType]) => ({
      name,
      kind: mapLocalFileTypeKind(fileType),
    })),
    truncated: sorted.length > bound,
  };
}

/** Maps the editor's FileSystemError family onto the typed fs codes for both
 * fs families; the physical target never appears in the message. The stage-F
 * superset adds the two codes the local family's writes and listings need. */
export function mapLocalFsErrorCode(code: unknown): string {
  return code === "FileNotFound"
    ? "fs_not_found"
    : code === "FileIsADirectory"
    ? "fs_is_directory"
    : code === "FileNotADirectory"
    ? "fs_not_directory"
    : code === "FileExists"
    ? "fs_exists"
    : code === "NoPermissions"
    ? "fs_access_denied"
    : code === "Unavailable"
    ? "fs_scheme_unreadable"
    : "fs_failed";
}

/** Builds the typed error a fs handler throws: the mapped code, the physical
 * target stripped from the message. */
export function localFsError(error: unknown): Error {
  const mapped = mapLocalFsErrorCode((error as { code?: unknown }).code);
  return Object.assign(new Error(`${mapped}: (target stripped)`), { code: mapped });
}

/** The lowercase-hex SHA-256 the write precondition carries: both sides hash
 * the same text — exactly what a read of the current state answers — so a
 * state the kernel never saw fails `fs_conflict` instead of overwriting. */
export async function sha256Hex(text: string): Promise<string> {
  const cryptoObject = globalThis as {
    crypto: { subtle: { digest(algorithm: string, data: Uint8Array): Promise<ArrayBuffer> } };
  };
  const digest = await cryptoObject.crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(text),
  );
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

/** The coverage ruling's frontend half (ADR 0042 decision 12): the local fs
 * family serves only paths inside the workspace folders this window actually
 * opens — a path outside them answers typed `fs_not_covered`, and the kernel
 * serves it locally where its state claims are true. Comparison is
 * case-insensitive: a case mismatch still serves the requested file itself,
 * so the gate may over-match harmlessly but never under-serves a real folder. */
export function isCoveredLocalTarget(fsPath: string, roots: readonly string[]): boolean {
  const normalized = normalizeLocalPath(fsPath);
  for (const root of roots) {
    const prefix = normalizeLocalPath(root);
    if (normalized === prefix) return true;
    const withSeparator = prefix === "/" || /^[a-zA-Z]:$/.test(prefix) ? prefix : `${prefix}/`;
    if (normalized.startsWith(withSeparator)) return true;
  }

  return false;
}

function normalizeLocalPath(path: string): string {
  const unified = path.replace(/\\/g, "/").replace(/\/+$/, "");
  return unified.length === 0 ? "/" : unified.toLowerCase();
}

// ---------- stage D: the facade and position translation ----------

/** The kernel's gate answer for the IDE tools when no frontend announced.
 * The client layer flattens failed tool envelopes to `Error` messages
 * shaped `"<code>: <message>"`; this type restores the code. */
export class IdeUnavailableError extends Error {
  /** The stable kernel code, always "ide_unavailable". */
  readonly code = "ide_unavailable";

  constructor(message: string) {
    super(message);
    this.name = "IdeUnavailableError";
  }
}

function toIdeUnavailable(error: unknown): unknown {
  const message = error instanceof Error ? error.message : String(error);
  if (message.startsWith("ide_unavailable:")) {
    return new IdeUnavailableError(message.slice("ide_unavailable:".length).trim());
  }
  return error;
}

/** LSP position: 0-based line and UTF-16 character offset. */
export interface LspPosition {
  readonly line: number;
  readonly character: number;
}

/** LSP range. */
export interface LspRange {
  readonly start: LspPosition;
  readonly end: LspPosition;
}

function toSelector(position: LspPosition): { line: number; character: number } {
  // LSP 0-based in; the kernel's agent convention is 1-based.
  return { line: position.line + 1, character: position.character + 1 };
}

/** True for the LSP position shape — `{line, character}`, both numbers —
 * the only shape the kernel's position translation touches. */
function isPosition(value: unknown): value is { line: number; character: number } {
  if (typeof value !== "object" || value === null) return false;
  const record = value as Record<string, unknown>;
  return typeof record.line === "number" && typeof record.character === "number";
}

/** Recursively converts every LSP position between the facade's 0-based
 * convention and the kernel's 1-based one (delta +1 in, −1 out). Bare
 * numbers (folding ranges, counts) and other shapes pass through. */
export function shiftPositions(value: unknown, delta: number, depth = 0): unknown {
  if (depth > 32) return value;
  if (Array.isArray(value)) return value.map((entry) => shiftPositions(entry, delta, depth + 1));
  if (typeof value !== "object" || value === null) return value;
  if (isPosition(value)) {
    return {
      line: value.line + delta,
      character: value.character + delta,
    };
  }
  const out: Record<string, unknown> = {};
  for (const [key, entry] of Object.entries(value as Record<string, unknown>)) {
    out[key] = shiftPositions(entry, delta, depth + 1);
  }
  return out;
}

async function invokeReadTool<T>(
  invoke: (tool: string, args?: Record<string, unknown>) => Promise<unknown>,
  tool: string,
  args: Record<string, unknown>,
): Promise<T> {
  try {
    return await invoke(tool, args) as T;
  } catch (error) {
    throw toIdeUnavailable(error);
  }
}

/** The IdeFacade shape: the read-only IDE queries the kernel offers scripts. */
export interface IdeFacade {
  hover(uri: string, position: LspPosition): Promise<unknown>;
  navigate(kind: string, uri: string, position: LspPosition): Promise<unknown>;
  documentSymbols(uri: string): Promise<unknown>;
  workspaceSymbols(query: string): Promise<unknown>;
  diagnostics(): Promise<unknown>;
  documentDiagnostics(uri: string, minSeverity?: string): Promise<unknown>;
  request(method: string, params: Record<string, unknown>): Promise<unknown>;
}

/** Creates the facade over an injected invoke — for tests and hosts that
 * already hold a client. The module-level `ide` export is the default
 * instance over the default client. */
export function createIde(
  invoke: (tool: string, args?: Record<string, unknown>) => Promise<unknown>,
): IdeFacade {
  const read = <T>(tool: string, args: Record<string, unknown>) =>
    invokeReadTool<T>(invoke, tool, args)
      .then((result) => shiftPositions(result, -1) as unknown);
  return {
    hover: (uri, position) => read("ide_hover", { uri, select: toSelector(position) }),
    navigate: (kind, uri, position) =>
      read("ide_navigate", { kind, uri, select: toSelector(position) }),
    documentSymbols: (uri) => read("ide_document_symbols", { uri }),
    workspaceSymbols: (query) => read("ide_workspace_symbols", { query }),
    diagnostics: () => read("ide_diagnostics", {}),
    documentDiagnostics: (uri, minSeverity) =>
      read("ide_diagnostics", minSeverity === undefined ? { uri } : { uri, minSeverity }),
    request: (method, params) =>
      read(
        "ide_request",
        shiftPositions({ method, parameters: params }, 1) as Record<string, unknown>,
      ),
  };
}
