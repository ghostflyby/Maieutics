/**
 * The REPL's workspace file-system shim (ADR 0042 decision 12): when the
 * kernel serves the workspace repository through an elected IDE frontend,
 * the worker's async text APIs route workspace paths through the bridge so
 * TS cells read and write the IDE's authoritative text state — the same
 * arbitration the workspace tools get. The install follows the `patchFetch`
 * precedent: worker-level, before any cell runs, injected once.
 *
 * Declared scope, per the ADR: only the async text plane — `readTextFile`,
 * `writeTextFile` (string payloads), `readDir`. Paths outside the workspace
 * root, binary payloads, sync APIs, and the rest of the Deno fs surface pass
 * through unchanged. The bridge is checked per call: a typed
 * `workspace_bridge_inactive` (or a kernel predating the script tools) falls
 * back to the real Deno API, so attaching or closing the IDE flips the
 * behavior live without restarting the REPL.
 */

/** The env var the kernel sets at spawn (and the permission baseline
 * allowlists) with the workspace root: its absence keeps the native fs
 * surface entirely, its presence routes workspace paths through the bridge. */
export const WORKSPACE_ROOT_ENV = "MAIEUTICS_WORKSPACE_ROOT";

/** The Deno.DirEntry shape the shim answers from bridged listings. */
export interface BridgedDirEntry {
  readonly name: string;
  readonly isFile: boolean;
  readonly isDirectory: boolean;
  readonly isSymlink: boolean;
}

/** The kernel's failure envelope, flattened by the client layer into
 * `Error` messages shaped `"<code>: <message>"`. */
export function bridgeErrorCode(error: unknown): string | undefined {
  if (!(error instanceof Error)) return undefined;
  const colon = error.message.indexOf(":");
  return colon > 0 ? error.message.slice(0, colon) : undefined;
}

/** The codes that mean "the kernel is not serving this path through the
 * bridge": the bridge is inactive (no elected IDE, or the frontend predates
 * the local family) or the kernel predates the script tools entirely. Both
 * fall back to the real Deno API — exactly the pre-bridge behavior. */
export function isBridgeInactive(error: unknown): boolean {
  const code = bridgeErrorCode(error);
  return code === "workspace_bridge_inactive" || code === "tool_not_found";
}

/** Resolves one fs API input to a workspace://local URI, or null when the
 * path is not inside the workspace root (passthrough). Lexical normalization
 * only — no I/O; `..` segments cannot escape because the containment check
 * runs on the normalized path. */
export function toWorkspaceUri(
  path: string | URL,
  cwd: string,
  workspaceRoot: string,
): string | null {
  let raw: string;
  if (path instanceof URL) {
    if (path.protocol !== "file:") return null;
    raw = decodeURIComponent(path.pathname);
  } else {
    raw = path;
  }
  if (raw.includes("\u0000")) return null;

  const normalized = normalizePath(raw, cwd);
  const root = normalizePath(workspaceRoot, cwd);
  const rootWithSeparator = root.length > 0 && !root.endsWith("/") ? `${root}/` : root;
  let relative: string;
  if (normalized === root) {
    relative = "";
  } else if (normalized.startsWith(rootWithSeparator)) {
    relative = normalized.slice(rootWithSeparator.length);
  } else {
    return null;
  }

  if (relative.length === 0) return "workspace://local/";
  const segments = relative.split("/").filter((segment) => segment.length > 0);
  return `workspace://local/${segments.map((segment) => encodeURIComponent(segment)).join("/")}`;
}

/** Posix-style lexical resolution; Windows inputs arrive with backslashes
 * normalized first, and a case-insensitive root comparison matches the
 * platform's filesystem semantics. */
function normalizePath(path: string, cwd: string): string {
  const isWindowsAbsolute = /^[a-zA-Z]:[\\/]/.test(path) || path.startsWith("\\\\");
  const unified = path.replace(/\\/g, "/");
  const absolute = unified.startsWith("/") || isWindowsAbsolute
    ? unified
    : `${cwd.replace(/\\/g, "/")}/${unified}`;
  const windowsDrive = isWindowsAbsolute && /^[a-zA-Z]:/.test(absolute)
    ? absolute.slice(0, 2)
    : null;

  const segments: string[] = [];
  for (const segment of absolute.split("/")) {
    if (segment.length === 0 || segment === ".") continue;
    if (segment === "..") {
      // Pop without escaping the filesystem root (or a Windows drive root).
      if (segments.length > 0 && segments[segments.length - 1] !== "..") segments.pop();
      else if (windowsDrive === null) segments.push("..");
      continue;
    }
    segments.push(segment);
  }

  const joined = segments.join("/");
  if (windowsDrive !== null) return `${windowsDrive}/${joined}`;
  return `/${joined}`;
}

export interface FsBridgeDeno {
  cwd(): string;
  readTextFile(path: string | URL, options?: { signal?: AbortSignal }): Promise<string>;
  writeTextFile(
    path: string | URL,
    data: string | Uint8Array,
    options?: { append?: boolean; create?: boolean; signal?: AbortSignal },
  ): Promise<void>;
  readDir(path: string | URL): AsyncIterable<BridgedDirEntry>;
}

/** Installs the shim on the given Deno namespace (pass the real `Deno` from
 * the worker; tests pass a stub). No-op when the kernel did not pass its
 * workspace root — the process keeps its native fs surface entirely. */
export function installFsBridge(
  denoObject: FsBridgeDeno,
  workspaceRoot: string,
  invoke: (tool: string, args: Record<string, unknown>) => Promise<unknown>,
): void {
  if (workspaceRoot.length === 0) return;

  const originalReadTextFile = denoObject.readTextFile.bind(denoObject);
  const originalWriteTextFile = denoObject.writeTextFile.bind(denoObject);
  const originalReadDir = denoObject.readDir.bind(denoObject);

  const bridgeUri = (path: string | URL): string | null =>
    toWorkspaceUri(path, denoObject.cwd(), workspaceRoot);

  denoObject.readTextFile = async (path, options) => {
    const uri = bridgeUri(path);
    if (uri === null) return originalReadTextFile(path, options);
    try {
      const answer = await invoke("workspace_fs_read", { uri }) as { content?: unknown };
      return typeof answer?.content === "string" ? answer.content : "";
    } catch (error) {
      if (isBridgeInactive(error)) return originalReadTextFile(path, options);
      throw error;
    }
  };

  denoObject.writeTextFile = async (path, data, options) => {
    // The binary overload and the non-workspace paths keep the native API;
    // the bridge is a text plane (ADR 0042 decision 12).
    if (typeof data !== "string") return originalWriteTextFile(path, data, options);
    const uri = bridgeUri(path);
    if (uri === null) return originalWriteTextFile(path, data, options);
    try {
      await invoke("workspace_fs_write", { uri, content: data });
    } catch (error) {
      if (isBridgeInactive(error)) return originalWriteTextFile(path, data, options);
      throw error;
    }
  };

  denoObject.readDir = (path) => {
    const uri = bridgeUri(path);
    if (uri === null) return originalReadDir(path);
    return bridgedList(invoke, uri, originalReadDir, path);
  };
}

/** Reads one bridged listing lazily; an inactive bridge falls back to the
 * native iterator before anything is yielded. */
async function* bridgedList(
  invoke: (tool: string, args: Record<string, unknown>) => Promise<unknown>,
  uri: string,
  originalReadDir: (path: string | URL) => AsyncIterable<BridgedDirEntry>,
  path: string | URL,
): AsyncGenerator<BridgedDirEntry> {
  let answer: unknown;
  try {
    answer = await invoke("workspace_fs_list", { uri });
  } catch (error) {
    if (isBridgeInactive(error)) {
      yield* originalReadDir(path);
      return;
    }
    throw error;
  }
  const entries = (answer as { entries?: unknown })?.entries;
  if (!Array.isArray(entries)) {
    throw new Error(
      `ide_result_invalid: the bridged listing for ${uri} is not the documented shape.`,
    );
  }
  for (const entry of entries) {
    const kind = (entry as { kind?: unknown })?.kind;
    const name = (entry as { name?: unknown })?.name;
    if (typeof name !== "string" || typeof kind !== "string") continue;
    yield {
      name,
      isFile: kind === "file",
      isDirectory: kind === "directory",
      isSymlink: kind === "symbolic_link",
    };
  }
}
