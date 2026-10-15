/** LSP-shaped IDE query facade (ADR 0042 stage D) over the kernel's
 * script-callable IDE tools. Reads like a minimal LSP client: 0-based
 * positions and `textDocument/…` methods in, the provider's shapes out —
 * the kernel translates to its 1-based conventions and translates the
 * answers back. Read-only by construction: the write path (format/rename/
 * apply) is reachable only from an agent session, not from scripts.
 *
 * Requires an ide-capable frontend to be connected (the `ide=1` events
 * announcement); without one every call fails typed `ide_unavailable`. */

import { tools } from "./mod.ts";

/** LSP text document identifier. */
export interface TextDocumentId {
  readonly uri: string;
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

/** The kernel's gate answer for the IDE tools when no frontend announced. */
export class IdeUnavailableError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "IdeUnavailableError";
  }
}

function toSelector(position: LspPosition): { line: number; character: number } {
  // LSP 0-based in; the kernel's agent convention is 1-based.
  return { line: position.line + 1, character: position.character + 1 };
}

async function invokeReadTool<T>(tool: string, args: Record<string, unknown>): Promise<T> {
  try {
    return await tools.invoke(tool, args) as T;
  } catch (error) {
    const code = (error as { code?: unknown }).code;
    if (code === "ide_unavailable") {
      throw new IdeUnavailableError((error as Error).message);
    }
    throw error;
  }
}

/** Hover at one position; the answer's `contents` is MarkupContent-shaped
 * (the kernel normalizes), `range` 1-based. Null when nothing hovered. */
export function hover(uri: string, position: LspPosition): Promise<unknown> {
  return invokeReadTool("ide_hover", { uri, select: toSelector(position) });
}

/** Navigation: kind is one of definition, type_definition, declaration,
 * implementation, references, highlights. */
export function navigate(
  kind: string,
  uri: string,
  position: LspPosition,
): Promise<unknown> {
  return invokeReadTool("ide_navigate", { kind, uri, select: toSelector(position) });
}

export function documentSymbols(uri: string): Promise<unknown> {
  return invokeReadTool("ide_document_symbols", { uri });
}

export function workspaceSymbols(query: string): Promise<unknown> {
  return invokeReadTool("ide_workspace_symbols", { query });
}

/** The workspace's Problems, aggregate view; drill into one file with
 * `documentDiagnostics`. An empty result means "nothing reported", not
 * "no problems". */
export function diagnostics(): Promise<unknown> {
  return invokeReadTool("ide_diagnostics", {});
}

/** One file's diagnostics, 1-based positions. */
export function documentDiagnostics(uri: string, minSeverity?: string): Promise<unknown> {
  return invokeReadTool(
    "ide_diagnostics",
    minSeverity === undefined ? { uri } : { uri, minSeverity },
  );
}

/** Low-level escape hatch: one read-catalog method with LSP-shaped params
 * (0-based positions converted kernel-side; foldingRange's bare numbers are
 * provider 0-based lines passed verbatim). */
export function request(method: string, params: Record<string, unknown>): Promise<unknown> {
  return invokeReadTool("ide_request", { method, parameters: params });
}
