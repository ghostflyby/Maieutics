import { assertEquals } from "@std/assert";
import {
  cellHistoryState,
  type CellLike,
  frontierIndex,
  isRunAboveSelection,
  partitionByHistory,
  readTurnBinding,
  TurnBindingMetadataKey,
  withoutTurnBinding,
  withTurnBinding,
} from "./cellHistory.ts";

function cell(
  text: string,
  metadata: { [key: string]: unknown } = {},
): CellLike {
  return { metadata, text };
}

function committedCell(text: string, runId = "a".repeat(32)): CellLike {
  return cell(text, { [TurnBindingMetadataKey]: { runId, input: text } });
}

Deno.test("pending cells have no binding or a malformed one", () => {
  assertEquals(cellHistoryState(cell("hello")), "pending");
  assertEquals(cellHistoryState(cell("hello", { maieuticsTurn: "nope" })), "pending");
  assertEquals(
    cellHistoryState(cell("hello", { maieuticsTurn: { runId: "", input: "hello" } })),
    "pending",
  );
  // Bindings written before `input` existed (older builds) degrade to pending.
  assertEquals(
    cellHistoryState(cell("hello", { maieuticsTurn: { runId: "abc" } })),
    "pending",
  );
});

Deno.test("matching text is committed; drifted text is stale", () => {
  const binding = { runId: "a".repeat(32), input: "original" };
  assertEquals(cellHistoryState(cell("original", { maieuticsTurn: binding })), "committed");
  assertEquals(cellHistoryState(cell("edited", { maieuticsTurn: binding })), "stale");
});

Deno.test("readTurnBinding round-trips through withTurnBinding/withoutTurnBinding", () => {
  const tagged = withTurnBinding({ other: 1 }, { runId: "r1", input: "text" });
  assertEquals(tagged.other, 1);
  const read = readTurnBinding(cell("text", tagged));
  assertEquals(read, { runId: "r1", input: "text" });

  const cleared = withoutTurnBinding(tagged);
  assertEquals("maieuticsTurn" in cleared, false);
  assertEquals(cleared.other, 1);
  assertEquals(readTurnBinding(cell("text", cleared)), undefined);
});

Deno.test("partitionByHistory keeps document order per side", () => {
  const pending1 = cell("one");
  const committed1 = committedCell("two");
  const stale1 = cell("two (edited)", { maieuticsTurn: { runId: "r", input: "two" } });
  const pending2 = cell("three");

  const partition = partitionByHistory([pending1, committed1, stale1, pending2]);
  assertEquals(partition.pending, [pending1, pending2]);
  assertEquals(partition.committed, [committed1, stale1]);
});

Deno.test("frontierIndex is the last non-pending cell", () => {
  assertEquals(frontierIndex([cell("a"), cell("b")]), -1);
  const cells = [committedCell("a"), committedCell("b"), cell("c")];
  assertEquals(frontierIndex(cells), 1);
});

Deno.test("run-above detection compares document positions, not object identity", () => {
  // The committed cells sit at the document's leading positions.
  assertEquals(isRunAboveSelection([0, 1]), true);
  assertEquals(isRunAboveSelection([0, 1, 2]), true);

  // A single committed cell stays forkable (explicit run wins over Run Above).
  assertEquals(isRunAboveSelection([0]), false);
  // A non-prefix selection is not Run Above.
  assertEquals(isRunAboveSelection([1, 2]), false);
  assertEquals(isRunAboveSelection([0, 2]), false);
});
