/**
 * Pure completion-request gate, kept free of the `vscode` module so it is
 * unit-testable under Deno: at most one completion round trip is on the wire,
 * and every failure path degrades to "no completions".
 */

import type { CommandCompletion } from "./protocol.ts";

/** Wire cursor semantics: the text of the whole cell plus the caret offset in
 * UTF-16 code units (the same units as VSCode document positions). */
export interface CompletionRequest {
  (text: string, cursor: number): Promise<CommandCompletion | undefined>;
  cancel(): void;
}

/**
 * Wraps one client-bound completion call with a single-in-flight gate. VSCode
 * re-fires the provider on every keystroke; a newer call supersedes (aborts)
 * the outstanding one instead of queueing, so overlapping stale answers never
 * reach the wire and the newest keystroke always gets a fresh answer.
 * Every failure path — including the expected supersession aborts — degrades
 * to no completions; only unexpected failures leave a log line.
 */
export function createCompletionGate(
  start: (text: string, cursor: number, signal: AbortSignal) => Promise<CommandCompletion>,
  log: (message: string) => void,
): CompletionRequest {
  let inFlight: AbortController | null = null;
  const request = async (text: string, cursor: number): Promise<CommandCompletion | undefined> => {
    inFlight?.abort();
    const controller = new AbortController();
    inFlight = controller;
    try {
      const answer = await start(text, cursor, controller.signal);
      return controller.signal.aborted ? undefined : answer;
    } catch (error) {
      // A superseded call's abort is expected keystroke churn, not a
      // failure: stay silent; anything else degrades with one log line.
      if (!controller.signal.aborted) {
        log(`completion failed: ${error}`);
      }

      return undefined;
    } finally {
      if (inFlight === controller) inFlight = null;
    }
  };
  return Object.assign(request, { cancel: () => inFlight?.abort() });
}
