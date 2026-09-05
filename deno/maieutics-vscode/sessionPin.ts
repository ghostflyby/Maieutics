/**
 * Session pinning for notebooks: one notebook remembers which server session
 * it ran against (persisted in the `.maieuticsnb` metadata), and the next
 * execution re-attaches to it. The server serves turns for its single active
 * session only, so the decision here runs before every execution batch:
 *
 * - no stored id (new notebook or fresh server): pin the active session;
 * - stored id matches the active session: nothing to do;
 * - stored id differs: resume it (the server switches its active session);
 * - resume fails (session gone, or persistence disabled): pin the active
 *   session and surface a warning once.
 *
 * Pure decision logic — no VSCode or network imports — so the alternation of
 * two open notebooks is unit-testable.
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
