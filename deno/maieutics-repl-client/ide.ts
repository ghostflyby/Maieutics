/** LSP-shaped IDE query facade (ADR 0042 stage D) over the kernel's
 * script-callable IDE tools. Reads like a minimal LSP client: 0-based
 * positions and `textDocument/…` methods in, LSP shapes out — the facade
 * converts positions to the kernel's 1-based conventions on the way in and
 * converts answers back to 0-based on the way out, so callers see LSP
 * positions end to end. Read-only by construction: the write path (format/
 * rename/apply) is reachable only from an agent session, not from scripts.
 *
 * Requires an ide-capable frontend to be connected (the `ide=1` events
 * announcement); without one every call fails with the typed
 * {@link IdeUnavailableError}. */

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

/** Hover at one position. The answer's `contents` is MarkupContent-shaped
 * (the kernel normalizes); positions are LSP 0-based. A document nothing
 * hovered on still answers — as a typed failure, never a null. */
export function hover(uri: string, position: LspPosition): Promise<unknown> {
  return invokeReadTool(tools.invoke.bind(tools), "ide_hover", {
    uri,
    select: toSelector(position),
  });
}

/** Navigation: kind is one of definition, type_definition, declaration,
 * implementation, references, highlights; positions are LSP 0-based. */
export function navigate(
  kind: string,
  uri: string,
  position: LspPosition,
): Promise<unknown> {
  return invokeReadTool(tools.invoke.bind(tools), "ide_navigate", {
    kind,
    uri,
    select: toSelector(position),
  }).then((result) => shiftPositions(result, -1) as unknown);
}

export function documentSymbols(uri: string): Promise<unknown> {
  return invokeReadTool(tools.invoke.bind(tools), "ide_document_symbols", { uri })
    .then((result) => shiftPositions(result, -1) as unknown);
}

export function workspaceSymbols(query: string): Promise<unknown> {
  return invokeReadTool(tools.invoke.bind(tools), "ide_workspace_symbols", { query })
    .then((result) => shiftPositions(result, -1) as unknown);
}

/** The workspace's Problems, aggregate view; drill into one file with
 * `documentDiagnostics`. An empty result means "nothing reported", not
 * "no problems". */
export function diagnostics(): Promise<unknown> {
  return invokeReadTool(tools.invoke.bind(tools), "ide_diagnostics", {});
}

/** One file's diagnostics; positions are LSP 0-based. */
export function documentDiagnostics(uri: string, minSeverity?: string): Promise<unknown> {
  return invokeReadTool(
    tools.invoke.bind(tools),
    "ide_diagnostics",
    minSeverity === undefined ? { uri } : { uri, minSeverity },
  );
}

/** Low-level escape hatch: one read-catalog method with LSP-shaped params.
 * Positions are LSP 0-based on both sides — the facade shifts
 * `{line, character}` objects; bare-number fields (folding ranges) pass
 * through as provider values. */
export function request(method: string, params: Record<string, unknown>): Promise<unknown> {
  return invokeReadTool(
    tools.invoke.bind(tools),
    "ide_request",
    shiftPositions({ method, parameters: params }, 0) as Record<
      string,
      unknown
    >,
  ).then((result) => shiftPositions(result, 0) as unknown);
}

/** Creates the facade over an injected invoke — for tests and hosts that
 * already hold a client. The module-level export is the default instance. */
export function createIde(
  invoke: (tool: string, args?: Record<string, unknown>) => Promise<unknown>,
): {
  hover: typeof hover;
  navigate: typeof navigate;
  documentSymbols: typeof documentSymbols;
  workspaceSymbols: typeof workspaceSymbols;
  diagnostics: typeof diagnostics;
  documentDiagnostics: typeof documentDiagnostics;
  request: typeof request;
} {
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

/** Creates the facade over an injected invoke — for tests and hosts that
 * already hold a client. The module-level `ide` export is the default
 * instance over the default client. */

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

/** The default facade over the module's default REPL client. */
export const ide: IdeFacade = createIde((tool, args) => tools.invoke(tool, args));
