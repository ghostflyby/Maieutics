/**
 * Pure frame attribution for one session's event stream: decides which
 * in-flight run a frame belongs to before any rendering happens. Subagent
 * child runs (ADR 0030) share the parent run's session stream under the
 * child's own runId, and the wire carries no parentRunId, so attribution
 * leans on the session's single-run gate (invariant 4: at most one top-level
 * run executes per session) plus a child registry keyed by arrival order.
 * No vscode or network imports, so the decisions are unit-testable.
 */

/** The owner one routed frame resolves to. */
export type FrameOwner =
  /** A run-less frame (REPL presentation): it belongs to the session's
   * single in-flight run. */
  | { kind: "session" }
  /** The frame's runId is an owned top-level run. */
  | { kind: "owned"; runId: string }
  /** The frame's runId is a registered child whose parent still runs. */
  | { kind: "child"; parentRunId: string }
  /** The frame's runId was never seen and exactly one top-level run is in
   * flight: it is adopted as that run's child. */
  | { kind: "adopt"; parentRunId: string }
  /** Nothing owns the frame (a late replay of a retired run, an unknown id
   * with zero or several candidate parents): it drops, as before the child
   * timeline existed, and must never disturb a settled run. */
  | { kind: "drop" };

/** Resolves the owner of one frame. `ownedRuns` is the stream's live
 * top-level run executions; `childRuns` maps registered child run ids onto
 * their parent's run id. */
export function resolveFrameOwner(
  frameRunId: string | undefined,
  ownedRuns: ReadonlyMap<string, unknown>,
  childRuns: ReadonlyMap<string, string>,
): FrameOwner {
  if (frameRunId === undefined) return { kind: "session" };
  if (ownedRuns.has(frameRunId)) return { kind: "owned", runId: frameRunId };

  const registered = childRuns.get(frameRunId);
  if (registered !== undefined) {
    // The parent may have retired since registration (a late child frame
    // racing the parent's terminal frame): a retired parent renders nothing.
    return ownedRuns.has(registered)
      ? { kind: "child", parentRunId: registered }
      : { kind: "drop" };
  }

  // First-seen id: the single-run gate makes one in-flight run the only
  // candidate parent. Zero candidates (frames racing the run's registration)
  // and several (two notebooks sharing the session stream both registered
  // their runs) cannot attribute — drop rather than guess.
  if (ownedRuns.size === 1) {
    for (const parentRunId of ownedRuns.keys()) return { kind: "adopt", parentRunId };
  }
  return { kind: "drop" };
}

/** The child registrations to drop when a top-level run retires, so a late
 * frame of an old child can never be adopted by the NEXT run. */
export function childRunsOfParent(
  childRuns: ReadonlyMap<string, string>,
  parentRunId: string,
): string[] {
  const children: string[] = [];
  for (const [child, parent] of childRuns) {
    if (parent === parentRunId) children.push(child);
  }
  return children;
}
