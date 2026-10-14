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
    default:
      return [];
  }
}

/** Pulls the `ide.request` payload out of a `tool.progress` frame's json
 * content; undefined for every other frame or shape. */
export function extractIdeRequest(frame: EventFrame): IdeRequestPayload | undefined {
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
