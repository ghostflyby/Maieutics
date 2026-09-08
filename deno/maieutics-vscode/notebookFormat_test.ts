/// <reference lib="deno.window" />

import { assertEquals, assertThrows } from "@std/assert";
import {
  emptyNotebook,
  NotebookFormatError,
  parseNotebook,
  serializeNotebook,
} from "./notebookFormat.ts";

Deno.test("empty notebook round-trips", () => {
  const notebook = emptyNotebook();
  const parsed = parseNotebook(serializeNotebook(notebook));
  assertEquals(parsed.maieutics, "maieutics-notebook");
  assertEquals(parsed.version, 1);
  assertEquals(parsed.cells.length, 1);
  assertEquals(parsed.cells[0].kind, "agent");
});

Deno.test("structured outputs survive the round trip", () => {
  const notebook = emptyNotebook();
  notebook.cells.push({
    kind: "agent",
    text: "list files",
    output: {
      text: "here they are",
      truncated: true,
      tools: [{ tool: "workspace_list", status: "ok" }],
      repl: [{ displayId: "d1", data: { "text/html": "<b>t</b>" } }],
    },
  });
  notebook.cells.push({ kind: "markdown", text: "# notes" });

  const parsed = parseNotebook(serializeNotebook(notebook));
  assertEquals(parsed.cells[1].output?.text, "here they are");
  assertEquals(parsed.cells[1].output?.truncated, true);
  assertEquals(parsed.cells[1].output?.tools?.[0].tool, "workspace_list");
  assertEquals(parsed.cells[1].output?.repl?.[0].data, { "text/html": "<b>t</b>" });
  assertEquals(parsed.cells[2].kind, "markdown");
});

Deno.test("foreign documents are rejected with a typed error", () => {
  const bytes = new TextEncoder().encode(JSON.stringify({ cells: [] }));
  assertThrows(() => parseNotebook(bytes), NotebookFormatError);
});

Deno.test("newer documents are rejected instead of misread", () => {
  const bytes = new TextEncoder().encode(
    JSON.stringify({ maieutics: "maieutics-notebook", version: 99, cells: [] }),
  );
  assertThrows(() => parseNotebook(bytes), NotebookFormatError);
});

Deno.test("broken JSON is rejected with a typed error", () => {
  const bytes = new TextEncoder().encode("{ not json");
  assertThrows(() => parseNotebook(bytes), NotebookFormatError);
});

Deno.test("unknown fields and missing output fields are tolerated", () => {
  const bytes = new TextEncoder().encode(JSON.stringify({
    maieutics: "maieutics-notebook",
    version: 1,
    futureField: { arbitrary: true },
    cells: [
      { kind: "agent", text: "hi", unknownCellField: 1 },
      { text: "no kind" },
    ],
  }));
  const parsed = parseNotebook(bytes);
  assertEquals(parsed.cells.length, 2);
  assertEquals(parsed.cells[0].text, "hi");
  assertEquals(parsed.cells[0].output, undefined);
  assertEquals(parsed.cells[1].kind, "agent");
  assertEquals(parsed.cells[1].text, "no kind");
});
