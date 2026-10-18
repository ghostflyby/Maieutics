/// <reference lib="deno.ns" />
import { assertEquals, assertRejects } from "@std/assert";
import { createIde, createIdeDebug, IdeUnavailableError, shiftPositions } from "./ide.ts";

Deno.test("shiftPositions converts LSP positions and leaves everything else", () => {
  const input = {
    range: { start: { line: 0, character: 2 }, end: { line: 3, character: 1 } },
    folding: { startLine: 5 },
    counts: [1, 2],
    text: "keep",
  };
  assertEquals(shiftPositions(input, 1), {
    range: { start: { line: 1, character: 3 }, end: { line: 4, character: 2 } },
    folding: { startLine: 5 },
    counts: [1, 2],
    text: "keep",
  });
  assertEquals(shiftPositions(shiftPositions(input, 1), -1), input, "the round trip is identity");
});

Deno.test("createIde converts 0-based facade calls to 1-based kernel args", async () => {
  const calls: { tool: string; args: Record<string, unknown> }[] = [];
  const ide = createIde((tool, args) => {
    calls.push({ tool, args: args ?? {} });
    if (tool === "ide_hover") {
      return Promise.resolve({
        contents: { kind: "markdown", value: "x" },
        range: { start: { line: 1, character: 1 }, end: { line: 1, character: 2 } },
      });
    }
    return Promise.resolve(null);
  });

  const result = await ide.hover("workspace://local/a.cs", { line: 0, character: 3 });
  assertEquals(calls[0].tool, "ide_hover");
  assertEquals(
    calls[0].args.select,
    { line: 1, character: 4 },
    "0-based facade position converts to 1-based kernel selector",
  );
  const hover = result as { range: { start: { line: number; character: number } } };
  assertEquals(
    hover.range.start,
    { line: 0, character: 0 },
    "the answer converts back to LSP 0-based",
  );
});

Deno.test("createIde surfaces typed unavailable and shifts request params", async () => {
  const calls: { tool: string; args: Record<string, unknown> }[] = [];
  const ide = createIde((tool, args) => {
    calls.push({ tool, args: args ?? {} });
    if (tool === "ide_request") {
      // The kernel answers with 0-based shapes; the facade converts back.
      return Promise.resolve({ start: { line: 1, character: 1 } });
    }
    const failure = new Error("ide_unavailable: no announcing frontend");
    (failure as { code?: string }).code = "ide_unavailable";
    return Promise.reject(failure);
  });

  await assertRejects(() => ide.diagnostics(), IdeUnavailableError);

  const result = await ide.request("textDocument/selectionRange", {
    textDocument: { uri: "workspace://local/a.cs" },
    positions: [{ line: 2, character: 3 }],
  });
  assertEquals(calls[1].args.parameters, {
    textDocument: { uri: "workspace://local/a.cs" },
    positions: [{ line: 3, character: 4 }],
  }, "outgoing params shift +1");
  assertEquals(result, { start: { line: 0, character: 0 } }, "incoming shifts -1");
});

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

  const breakpoints = await debug.breakpoints("dbg-9", "file:///w/a.ts", [7]);
  assertEquals(
    calls.at(-1),
    ["ide_debug_breakpoints", { sessionId: "dbg-9", uri: "file:///w/a.ts", lines: [8] }],
    "the facade takes 0-based and sends the kernel's 1-based contract",
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
