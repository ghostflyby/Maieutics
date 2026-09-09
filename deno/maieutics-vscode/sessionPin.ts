/**
 * Session pinning for notebooks: one notebook remembers which server session
 * it ran against (persisted in the `.maieuticsnb` metadata), and the next
 * execution re-attaches to it. The decision runs before every execution batch
 * and depends on the server's capability:
 *
 * - multi-session server (`capabilities.multiSession`): every session-addressed
 *   route is served directly, so a pinned notebook just targets its session and
 *   an unpinned notebook creates its own instead of stealing the foreground;
 * - legacy single-active server: turns are served for the one active session
 *   only, so a differing stored id is resumed (switching the server), and a
 *   resume failure pins the active session with a warning.
 *
 * Pure decision logic — no VSCode imports — so the alternation of two open
 * notebooks is unit-testable.
 */

import type { FrontendClient } from "./client.ts";
import type { SessionInfo } from "./protocol.ts";

export interface PinDecision {
  kind: "ok" | "pin" | "resume";
  /** The session every turn in this batch must target. */
  session: SessionInfo;
  /** When set, the notebook metadata must be updated to this id. */
  pinId?: string;
  /** A non-blocking warning to show once (session gone / persistence off). */
  warning?: string;
}

export async function resolveSessionPin(
  storedSessionId: string | undefined,
  client: FrontendClient,
): Promise<PinDecision> {
  const capabilities = await client.capabilities().catch(() => undefined);

  if (capabilities?.multiSession === true) {
    const active = await client.session();

    // With persistence disabled nothing is stored server-side (listSessions is
    // empty by design), so a pin can never be verified: fall back to the legacy
    // foreground semantics.
    if (!active.persistenceEnabled) {
      if (storedSessionId === undefined || storedSessionId === active.id) {
        return {
          kind: storedSessionId === undefined ? "pin" : "ok",
          session: active,
          pinId: storedSessionId === undefined ? active.id : undefined,
        };
      }

      return {
        kind: "pin",
        session: active,
        pinId: active.id,
        warning: "The stored session could not be resumed (transcript persistence " +
          "is disabled). Continuing with the active session.",
      };
    }

    if (storedSessionId === undefined) {
      const created = await client.newSession();
      return { kind: "pin", session: created, pinId: created.id };
    }

    // A listing failure is transient: keep targeting the pinned id — the turn
    // route fails typed if the session is really gone.
    const sessions = await client.listSessions().catch(() => undefined);
    if (sessions === undefined) {
      return { kind: "ok", session: { id: storedSessionId, turns: 0, persistenceEnabled: true } };
    }

    const stored = sessions.find((session) => session.id === storedSessionId);
    if (stored !== undefined) {
      // Target the pinned session directly; no resume, no foreground move.
      return {
        kind: "ok",
        session: {
          id: stored.id,
          turns: stored.turns,
          persistenceEnabled: true,
          title: stored.title,
        },
      };
    }

    // The pin references an unknown session (another server, or pruned):
    // continue with a fresh session rather than silently dropping the pin.
    const created = await client.newSession();
    return {
      kind: "pin",
      session: created,
      pinId: created.id,
      warning: `The stored session ${storedSessionId.slice(0, 12)} was not found. ` +
        "Continuing with a new session.",
    };
  }

  const active = await client.session();

  if (storedSessionId === undefined || storedSessionId === active.id) {
    return {
      kind: storedSessionId === undefined ? "pin" : "ok",
      session: active,
      pinId: storedSessionId === undefined ? active.id : undefined,
    };
  }

  try {
    const resumed = await client.resumeSession(storedSessionId);
    return { kind: "resume", session: resumed };
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return {
      kind: "pin",
      session: active,
      pinId: active.id,
      warning: `The stored session could not be resumed (${message}). ` +
        "Continuing with the active session.",
    };
  }
}
