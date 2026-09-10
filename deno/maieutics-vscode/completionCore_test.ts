/// <reference lib="deno.window" />

import { assert, assertEquals } from "@std/assert";
import { createCompletionGate } from "./completionCore.ts";

/** A start function whose answers are resolved manually. Like fetch, a call
 * rejects when its signal aborts. */
function manualStart() {
  const calls: {
    text: string;
    cursor: number;
    signal: AbortSignal;
    resolve: (matches: string[]) => void;
    reject: (error: unknown) => void;
  }[] = [];
  const start = (text: string, cursor: number, signal: AbortSignal) =>
    new Promise<string[]>((resolve, reject) => {
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

  calls[1].resolve(["%model", "%mcp"]);
  assertEquals(await newest, ["%model", "%mcp"]);
  // The aborted call degrades silently to no completions.
  assertEquals(await superseded, []);
  assertEquals(logs, []);
});

Deno.test("the gate releases after completion so later calls run again", async () => {
  const { calls, start } = manualStart();
  const gate = createCompletionGate(start, () => {});

  const first = gate("%", 1);
  calls[0].resolve([]);
  assertEquals(await first, []);

  const second = gate("%s", 2);
  assertEquals(calls.length, 2);
  assertEquals(calls[1].signal.aborted, false);
  calls[1].resolve(["%session"]);
  assertEquals(await second, ["%session"]);
});

Deno.test("failures degrade silently to no completions with one log line", async () => {
  const logs: string[] = [];
  const gate = createCompletionGate(
    () => Promise.reject(new Error("server offline")),
    (message) => logs.push(message),
  );

  assertEquals(await gate("%", 1), []);
  assertEquals(logs.length, 1);
  assert(logs[0].includes("server offline"));
});
