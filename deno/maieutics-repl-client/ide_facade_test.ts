/// <reference lib="deno.ns" />
import { assert, assertEquals, assertRejects } from "@std/assert";
import { createIde, IdeUnavailableError, shiftPositions } from "./ide.ts";

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
