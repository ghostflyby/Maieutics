/**
 * Pure grouping and naming rules for the sessions tree (and any other session
 * picker). No VSCode imports: the tree provider fetches data and renders; this
 * module decides what the hierarchy looks like.
 *
 * Display-name fallback chain: title → preview (one line, truncated) → id
 * prefix. Workspace grouping sorts the current workspace first and expanded,
 * then other workspaces by most recent activity, with the "without workspace"
 * group last.
 */

/** The subset of the wire's StoredSession the grouping needs. */
export interface SessionLike {
  id: string;
  turns: number;
  lastActivityAt: string;
  title?: string;
  preview?: string;
  workspaceRoot?: string;
  /** The session this one forked from; absent for root sessions. */
  parentSessionId?: string;
  /** How many of the parent's turns the fork keeps; absent for roots. */
  forkPointSeq?: number;
}

/** One collapsible workspace group; `key: null` is the "Without workspace"
 * group (old rows carry no root). */
export interface SessionGroup {
  key: string | null;
  label: string;
  current: boolean;
  sessions: SessionLike[];
}

/** The displayed name of a session: title, else preview, else id prefix.
 * Newlines in stored titles (the wire keeps them verbatim) collapse to spaces
 * so tree and quick-pick labels stay single-line. */
export function sessionLabel(session: SessionLike, previewLimit = 60): string {
  const raw = session.title || session.preview;
  if (!raw) return session.id.slice(0, 12);
  const oneLine = raw.replace(/\s+/g, " ").trim();
  if (oneLine.length === 0) return session.id.slice(0, 12);
  return oneLine.length <= previewLimit ? oneLine : `${oneLine.slice(0, previewLimit)}…`;
}

/** The tree label for a workspace group: the root's basename, or the whole
 * root when it has no usable basename. */
export function workspaceGroupLabel(root: string): string {
  const normalized = root.replace(/[\\/]+$/, "");
  const base = normalized.split(/[\\/]/).pop() ?? "";
  return base.length > 0 ? base : root;
}

/** Normalizes a root for comparison: trailing separators dropped, and case
 * folded when the platform filesystem is case-insensitive. */
function normalizeRoot(root: string, caseInsensitive: boolean): string {
  const trimmed = root.replace(/[\\/]+$/, "");
  return caseInsensitive ? trimmed.toLowerCase() : trimmed;
}

/** Whether a session's stamped root matches the current workspace root. A
 * session without a root never matches. */
export function matchesWorkspace(
  sessionRoot: string | undefined,
  currentRoot: string,
  caseInsensitive: boolean,
): boolean {
  if (sessionRoot === undefined) return false;
  return normalizeRoot(sessionRoot, caseInsensitive) ===
    normalizeRoot(currentRoot, caseInsensitive);
}

function byRecency(a: SessionLike, b: SessionLike): number {
  const delta = Date.parse(b.lastActivityAt) - Date.parse(a.lastActivityAt);
  return delta !== 0 ? delta : (a.id < b.id ? -1 : 1);
}

/**
 * Orders one group's sessions with lineage adjacency (Phase C): every session
 * is followed immediately by its fork descendants (depth first, most recent
 * first within each level), so a branch renders under its root. The auto-title
 * ("… · branch @ turn N") carries the lineage in the label itself.
 */
export function orderWithLineage(sessions: SessionLike[]): SessionLike[] {
  const byId = new Map(sessions.map((session) => [session.id, session]));
  const children = new Map<string, SessionLike[]>();
  for (const session of sessions) {
    const parent = session.parentSessionId;
    if (parent === undefined || !byId.has(parent)) continue;
    const siblings = children.get(parent) ?? [];
    siblings.push(session);
    children.set(parent, siblings);
  }

  for (const siblings of children.values()) siblings.sort(byRecency);

  const ordered: SessionLike[] = [];
  const visited = new Set<string>();
  const visit = (session: SessionLike): void => {
    if (visited.has(session.id)) return;
    visited.add(session.id);
    ordered.push(session);
    for (const child of children.get(session.id) ?? []) visit(child);
  };

  // Roots first (unreachable parents render as roots at their own recency).
  for (const session of [...sessions].sort(byRecency)) {
    if (session.parentSessionId === undefined || !byId.has(session.parentSessionId)) {
      visit(session);
    }
  }
  for (const session of [...sessions].sort(byRecency)) visit(session);
  return ordered;
}

/**
 * Groups stored sessions for the tree. Groups order: the current workspace,
 * other workspaces by their most recent session, and the null-root group last.
 * Sessions inside a group are most recent first. With `currentOnly`, only the
 * current group survives (an empty list when the current root matches nothing).
 */
export function groupSessions(
  sessions: SessionLike[],
  currentRoot: string | undefined,
  caseInsensitive: boolean,
  currentOnly = false,
): SessionGroup[] {
  const groups = new Map<string, SessionGroup>();
  for (const session of sessions) {
    const key = session.workspaceRoot ?? null;
    let group = groups.get(key ?? "\u0000null");
    if (group === undefined) {
      group = {
        key,
        label: key === null ? "Without workspace" : workspaceGroupLabel(key),
        current: false,
        sessions: [],
      };
      groups.set(key ?? "\u0000null", group);
    }
    group.sessions.push(session);
  }

  const all = [...groups.values()];
  for (const group of all) {
    group.sessions = orderWithLineage(group.sessions);
    group.current = currentRoot !== undefined && group.key !== null &&
      matchesWorkspace(group.key, currentRoot, caseInsensitive);
  }

  const current = all.filter((group) => group.current);
  const named = all.filter((group) => !group.current && group.key !== null)
    .sort((a, b) => byRecency(a.sessions[0], b.sessions[0]));
  const anonymous = all.filter((group) => group.key === null);
  const ordered = [...current, ...named, ...anonymous];

  return currentOnly ? ordered.filter((group) => group.current) : ordered;
}
