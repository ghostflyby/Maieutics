/// <reference lib="deno.ns" />
import { assertEquals } from "@std/assert";
import { testIdeFacade } from "../test/ide_facade_contract.ts";
import { createIde, createIdeDebug, IdeUnavailableError, shiftPositions } from "./ide.ts";

testIdeFacade("REPL facade", { createIde, IdeUnavailableError, shiftPositions });

Deno.test("createIdeDebug passes DAP shapes through and shifts only breakpoints", async () => {
  const calls: Array<[string, Record<string, unknown>]> = [];
  const invoke = (tool: string, args?: Record<string, unknown>) => {
    calls.push([tool, args ?? {}]);
    if (tool === "ide_debug_start") {
      return Promise.resolve({ sessionId: "dbg-9", state: "running" });
    }
    if (tool === "ide_debug_configurations") {
      return Promise.resolve({
        configs: [{ name: "probe", type: "node", request: "launch" }],
      });
    }
    if (tool === "ide_debug_stack") {
      return Promise.resolve({
        stackFrames: [{ id: 3, name: "main", line: 9, column: 0 }],
      });
    }
    if (tool === "ide_debug_variables") {
      return Promise.resolve({
        scopes: [{ name: "Locals", variables: [{ name: "a", value: "1" }] }],
      });
    }
    if (tool === "ide_debug_evaluate") {
      return Promise.resolve({ result: "42", type: "number" });
    }
    if (tool === "ide_debug_state") {
      return Promise.resolve({
        sessionId: "dbg-9",
        version: 2,
        state: "paused",
        reason: "breakpoint",
        threadId: 5,
      });
    }
    if (tool === "ide_debug_breakpoints") {
      return Promise.resolve({ breakpoints: [{ line: 8, verified: true }] });
    }
    return Promise.resolve({});
  };
  const debug = createIdeDebug(invoke);

  const start = await debug.start({ launchName: "probe", stopOnEntry: true });
  assertEquals(start, { sessionId: "dbg-9", state: "running" });
  assertEquals(calls.at(-1), ["ide_debug_start", { launchName: "probe", stopOnEntry: true }]);

  const inline = await debug.start({ configuration: { type: "node", request: "launch" } });
  assertEquals(inline.sessionId, "dbg-9");
  assertEquals(calls.at(-1), [
    "ide_debug_start",
    { configuration: { type: "node", request: "launch" } },
  ]);

  const stack = await debug.stack("dbg-9");
  assertEquals(stack.stackFrames[0].line, 9, "DAP lines pass through 0-based, unshifted");

  await debug.variables("dbg-9", { frameId: 3 });
  assertEquals(calls.at(-1), ["ide_debug_variables", { sessionId: "dbg-9", frameId: 3 }]);
  await debug.variables("dbg-9", { variablesReference: 77 });
  assertEquals(calls.at(-1), [
    "ide_debug_variables",
    { sessionId: "dbg-9", variablesReference: 77 },
  ]);

  await debug.evaluate("dbg-9", "a + b", 3);
  assertEquals(calls.at(-1), [
    "ide_debug_evaluate",
    { sessionId: "dbg-9", expression: "a + b", frameId: 3 },
  ]);

  const breakpoints = await debug.breakpoints("dbg-9", "file:///w/a.ts", [7, {
    anchor: ["const a = 1;", "const b = 2;"],
  }]);
  assertEquals(
    calls.at(-1),
    [
      "ide_debug_breakpoints",
      {
        sessionId: "dbg-9",
        uri: "file:///w/a.ts",
        points: [{ line: 8 }, { anchor: ["const a = 1;", "const b = 2;"] }],
      },
    ],
    "the facade takes 0-based lines (converted to the kernel's 1-based) and passes anchors through",
  );
  assertEquals(breakpoints.breakpoints[0].line, 7, "and maps the answer back to 0-based");

  const state = await debug.state("dbg-9", 4);
  assertEquals(state.sessionId, "dbg-9");
  assertEquals(state.state, "paused");
  assertEquals(state.threadId, 5);
  assertEquals(calls.at(-1), ["ide_debug_state", { sessionId: "dbg-9", waitSeconds: 4 }]);

  await debug.control("dbg-9", "stepOver", 5);
  assertEquals(calls.at(-1), [
    "ide_debug_control",
    { sessionId: "dbg-9", action: "stepOver", threadId: 5 },
  ]);

  const configs = await debug.configurations();
  assertEquals(configs.configs, [{ name: "probe", type: "node", request: "launch" }]);
});
