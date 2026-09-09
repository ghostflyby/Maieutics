/**
 * Pure completion-request gate, kept free of the `vscode` module so it is
 * unit-testable under Deno: at most one completion round trip is on the wire,
 * and every failure path degrades to "no completions".
 */

/** Wire cursor semantics: the text of the whole cell plus the caret offset in
 * UTF-16 code units (the same units as VSCode document positions). */
export type CompletionRequest = (text: string, cursor: number) => Promise<string[]>;

/**
 * Wraps one client-bound completion call with a single-in-flight gate. VSCode
 * re-fires the provider on every keystroke; a newer call supersedes (aborts)
 * the outstanding one instead of queueing, so overlapping stale answers never
 * reach the wire and the newest keystroke always gets a fresh answer.
 * Every failure path — including the expected supersession aborts — degrades
 * to no completions; only unexpected failures leave a log line.
 */
export function createCompletionGate(
  start: (text: string, cursor: number, signal: AbortSignal) => Promise<string[]>,
  log: (message: string) => void,
): CompletionRequest {
  let inFlight: AbortController | null = null;
  return async (text, cursor) => {
    inFlight?.abort();
    const controller = new AbortController();
    inFlight = controller;
    try {
      return await start(text, cursor, controller.signal);
    } catch (error) {
      // A superseded call's abort is expected keystroke churn, not a
      // failure: stay silent; anything else degrades with one log line.
      if (!controller.signal.aborted) {
        log(`completion failed: ${error}`);
      }

      return [];
    } finally {
      if (inFlight === controller) inFlight = null;
    }
  };
}
