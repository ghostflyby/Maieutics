/// <reference lib="deno.window" />

import { assert, assertEquals } from "@std/assert";
import { createCompletionGate } from "./completionCore.ts";
import type { CommandCompletion } from "./protocol.ts";

/** A start function whose answers are resolved manually. Like fetch, a call
 * rejects when its signal aborts. */
function manualStart() {
  const calls: {
    text: string;
    cursor: number;
    signal: AbortSignal;
    resolve: (matches: CommandCompletion) => void;
    reject: (error: unknown) => void;
  }[] = [];
  const start = (text: string, cursor: number, signal: AbortSignal) =>
    new Promise<CommandCompletion>((resolve, reject) => {
      const call = { text, cursor, signal, resolve, reject };
      calls.push(call);
      signal.addEventListener("abort", () => reject(signal.reason));
    });
  return { calls, start };
}

Deno.test("a newer call supersedes the in-flight request and answers fresh", async () => {
  const { calls, start } = manualStart();
  const logs: string[] = [];
  const gate = createCompletionGate(start, (message) => logs.push(message));

  const superseded = gate("%", 1);
  const newest = gate("%mo", 3);
  assertEquals(calls.length, 2);
  // The superseded call is aborted so it never lands on the wire; the newest
  // one is untouched.
  assertEquals(calls[0].signal.aborted, true);
  assertEquals(calls[1].signal.aborted, false);
  assertEquals(calls[1].text, "%mo");
  assertEquals(calls[1].cursor, 3);

  const answer = { matches: ["%model", "%mcp"], tokenStart: 0, tokenEnd: 3 };
  calls[1].resolve(answer);
  assertEquals(await newest, answer);
  // The aborted call degrades silently to no completions.
  assertEquals(await superseded, undefined);
  assertEquals(logs, []);
});

Deno.test("the gate releases after completion so later calls run again", async () => {
  const { calls, start } = manualStart();
  const gate = createCompletionGate(start, () => {});

  const first = gate("%", 1);
  const empty = { matches: [], tokenStart: 0, tokenEnd: 1 };
  calls[0].resolve(empty);
  assertEquals(await first, empty);

  const second = gate("%s", 2);
  assertEquals(calls.length, 2);
  assertEquals(calls[1].signal.aborted, false);
  const answer = { matches: ["%session"], tokenStart: 0, tokenEnd: 2 };
  calls[1].resolve(answer);
  assertEquals(await second, answer);
});

Deno.test("failures degrade silently to no completions with one log line", async () => {
  const logs: string[] = [];
  const gate = createCompletionGate(
    () => Promise.reject(new Error("server offline")),
    (message) => logs.push(message),
  );

  assertEquals(await gate("%", 1), undefined);
  assertEquals(logs.length, 1);
  assert(logs[0].includes("server offline"));
});

Deno.test("a superseded answer is discarded even if connection startup ignored its abort", async () => {
  const answers: ((answer: CommandCompletion) => void)[] = [];
  const gate = createCompletionGate(
    () => new Promise<CommandCompletion>((resolve) => answers.push(resolve)),
    () => {},
  );
  const first = gate("%", 1);
  const second = gate("%s", 2);
  const answer = { matches: ["%session"], tokenStart: 0, tokenEnd: 2 };
  answers[1](answer);
  assertEquals(await second, answer);
  answers[0]({ matches: ["%model"], tokenStart: 0, tokenEnd: 1 });
  assertEquals(await first, undefined);
});

Deno.test("leaving command context or disposing cancels the outstanding request", async () => {
  const { calls, start } = manualStart();
  const logs: string[] = [];
  const gate = createCompletionGate(start, (message) => logs.push(message));
  const pending = gate("%", 1);
  gate.cancel();
  gate.cancel();
  assertEquals(calls[0].signal.aborted, true);
  assertEquals(await pending, undefined);
  assertEquals(logs, []);
});
