/**
 * Pure lens-path rules for the Maieutics virtual filesystem (no VSCode
 * imports, so the rules are unit-testable under deno test).
 *
 * The session id inside the filename is the canonical handle; lens
 * directories are derived views over the same session set. Validity keys on
 * the known session id, never on lens membership — an entry that drops out of
 * a lens keeps answering stat/read so open editors survive.
 */

/** The lens directories; every backed entry lives directly under one. */
export type SessionLens = "sessions" | "workspace" | "recent";

export const SessionLenses: readonly SessionLens[] = ["sessions", "workspace", "recent"];

/** How many entries the recent lens keeps. */
export const RecentLensSize = 20;

/** The subset of the wire's StoredSession the lens rules need. */
export interface SessionDescriptorLike {
  id: string;
  lastActivityAt: string;
  workspaceRoot?: string;
}

/** Parses a backed entry path into its lens and session id; null when the
 * path is not a well-formed `<lens>/<id32>.maieuticsnb` entry. */
export function parseSessionEntry(path: string): { lens: SessionLens; sessionId: string } | null {
  const segments = path.split("/");
  if (segments.length !== 3) return null;
  const lens = segments[1] as SessionLens;
  if (!SessionLenses.includes(lens)) return null;
  const match = /^([0-9a-f]{32})\.maieuticsnb$/.exec(segments[2] ?? "");
  if (match === null) return null;
  return { lens, sessionId: match[1] };
}

/** Whether a path is exactly the scheme root or one lens directory. */
export function isLensDirectory(path: string): boolean {
  if (path === "/") return true;
  return SessionLenses.includes(path.slice(1) as SessionLens);
}

/** The descriptor list one lens shows, newest first. */
export function lensMembers<T extends SessionDescriptorLike>(
  lens: SessionLens,
  sessions: T[],
  currentWorkspace: { root?: string; caseInsensitive: boolean },
): T[] {
  const known = [...sessions].sort((a, b) =>
    Date.parse(b.lastActivityAt) - Date.parse(a.lastActivityAt)
  );
  if (lens === "sessions") return known;
  if (lens === "recent") return known.slice(0, RecentLensSize);

  const root = currentWorkspace.root;
  if (root === undefined) return [];
  return known.filter((session) =>
    matchesRoot(session.workspaceRoot, root, currentWorkspace.caseInsensitive)
  );
}

/** Normalizes a root for comparison: trailing separators dropped, and case
 * folded when the platform filesystem is case-insensitive. */
function normalizeRoot(root: string, caseInsensitive: boolean): string {
  const trimmed = root.replace(/[\\/]+$/, "");
  return caseInsensitive ? trimmed.toLowerCase() : trimmed;
}

function matchesRoot(
  sessionRoot: string | undefined,
  currentRoot: string,
  caseInsensitive: boolean,
): boolean {
  if (sessionRoot === undefined) return false;
  return normalizeRoot(sessionRoot, caseInsensitive) ===
    normalizeRoot(currentRoot, caseInsensitive);
}
