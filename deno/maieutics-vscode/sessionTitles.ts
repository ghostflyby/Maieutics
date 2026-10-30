import type { SessionInfo } from "./protocol.ts";

/** Bounded display metadata cache. Run replays may carry an older title than hello. */
export class SessionTitles {
  private readonly sessions = new Map<string, SessionInfo>();

  get(id: string): SessionInfo | undefined {
    return this.sessions.get(id);
  }

  apply(session: SessionInfo): boolean {
    const before = this.sessions.get(session.id);
    if (before && (before.titleRevision ?? 0) > (session.titleRevision ?? 0)) return false;
    if (
      before?.title === session.title && before?.titleRevision === session.titleRevision &&
      before?.turns === session.turns && before?.titleMode === session.titleMode &&
      before?.titleLocked === session.titleLocked && before?.titleSource === session.titleSource
    ) return false;
    this.sessions.delete(session.id);
    this.sessions.set(session.id, session);
    if (this.sessions.size > 128) this.sessions.delete(this.sessions.keys().next().value as string);
    return true;
  }
}
