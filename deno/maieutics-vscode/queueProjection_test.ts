import { assert, assertEquals } from "@std/assert";
import { FrontendClient } from "./client.ts";
import {
  isCommandCellText,
  planBatchSteps,
  QueueProjection,
  QueueWatcher,
  readQueueState,
} from "./queueProjection.ts";
import type { QueueState } from "./protocol.ts";

/** Builds a snapshot the parser accepts (tests mutate individual fields; the
 * overrides stay untyped so malformed fixtures are expressible). */
function snapshot(overrides: Record<string, unknown> = {}): unknown {
  return {
    sessionId: "s",
    running: null,
    items: [],
    capacity: 8,
    ...overrides,
  };
}

/** A projection whose transitions are counted, like the status bar refresh. */
function countedProjection(): { projection: QueueProjection<{ id: string }>; changes: number[] } {
  const changes: number[] = [];
  const projection = new QueueProjection<{ id: string }>();
  projection.onDidChange(() => changes.push(changes.length + 1));
  return { projection, changes };
}

Deno.test("queue snapshots parse and tolerate unknown fields", () => {
  const parsed = readQueueState({
    sessionId: "s",
    running: { itemId: "i1", runId: "r1" },
    items: [{ id: "i2", text: "hello", enqueuedAt: "t", future: 1 }],
    capacity: 4,
    future: "field",
  });
  assertEquals(parsed, {
    sessionId: "s",
    running: { itemId: "i1", runId: "r1" },
    items: [{ id: "i2", text: "hello", enqueuedAt: "t" }],
    capacity: 4,
  });
  // A missing running field is the same as an idle queue.
  assertEquals(readQueueState({ sessionId: "s", items: [], capacity: 1 })?.running, null);
});

Deno.test("malformed queue snapshots are rejected, not partially applied", () => {
  assertEquals(readQueueState(undefined), undefined);
  assertEquals(readQueueState("nope"), undefined);
  assertEquals(readQueueState({ items: [], capacity: 1 }), undefined); // no session id
  assertEquals(readQueueState(snapshot({ capacity: "8" })), undefined); // capacity not a number
  assertEquals(readQueueState(snapshot({ running: { itemId: "i1" } })), undefined); // no runId
  assertEquals(readQueueState(snapshot({ running: "busy" })), undefined);
  assertEquals(readQueueState(snapshot({ items: "many" })), undefined);
  assertEquals(readQueueState(snapshot({ items: [{ text: "no id" }] })), undefined);
  assertEquals(readQueueState(snapshot({ items: ["i1"] })), undefined);
});

Deno.test("command detection mirrors the server's first-token rule", () => {
  assertEquals(isCommandCellText("%status"), true);
  assertEquals(isCommandCellText("  %SESSION current"), true);
  assertEquals(isCommandCellText("%model use fast"), true);
  assertEquals(isCommandCellText("%maieutics"), true);
  // Not command tokens: an unknown %word is agent turn text for the server.
  assertEquals(isCommandCellText("%unknown"), false);
  assertEquals(isCommandCellText("run %status please"), false);
  assertEquals(isCommandCellText(""), false);
});

Deno.test("batch planning groups consecutive turn cells and keeps commands inline", () => {
  const steps = planBatchSteps(
    ["hello", "world", "%status", "next", "", "%session current", "last"],
    (text) => text,
  );
  assertEquals(steps, [
    { kind: "queue", cells: ["hello", "world"] },
    { kind: "inline", cell: "%status" },
    { kind: "queue", cells: ["next"] },
    { kind: "inline", cell: "" },
    { kind: "inline", cell: "%session current" },
    { kind: "queue", cells: ["last"] },
  ]);
});

Deno.test("a pure-pending batch plans as a single queue run", () => {
  const steps = planBatchSteps(["one", "two", "three"], (text) => text);
  assertEquals(steps, [{ kind: "queue", cells: ["one", "two", "three"] }]);
  assertEquals(planBatchSteps([], (text) => text), []);
});

Deno.test("tracked items report 1-based positions and refresh from snapshots", () => {
  const { projection } = countedProjection();
  const a = { id: "a" };
  const b = { id: "b" };
  projection.track("s", [
    { id: "i1", position: 1, cell: a, text: "a" },
    { id: "i2", position: 2, cell: b, text: "b" },
  ]);
  assertEquals(projection.positionOf(a), 1);
  assertEquals(projection.positionOf(b), 2);

  // Positions come from the snapshot order: an item enqueued by another
  // client ahead of ours shifts ours down.
  const withOther = readQueueState(
    snapshot({ items: [{ id: "other" }, { id: "i1" }, { id: "i2" }] }),
  );
  assert(withOther !== undefined);
  projection.apply("s", withOther);
  assertEquals(projection.positionOf(a), 2);
  assertEquals(projection.positionOf(b), 3);
});

Deno.test("positions are per session and untracked sessions are untouched", () => {
  const { projection } = countedProjection();
  const a = { id: "a" };
  projection.track("one", [{ id: "i1", position: 1, cell: a, text: "a" }]);
  assertEquals(projection.positionOf(a), 1);
  const other = readQueueState(snapshot({ sessionId: "two", items: [{ id: "i1" }] }));
  assert(other !== undefined);
  projection.apply("two", other); // same item id, different session
  assertEquals(projection.positionOf(a), 1);
});

Deno.test("take removes the running item's entry and returns its binding", () => {
  const { projection, changes } = countedProjection();
  const a = { id: "a" };
  projection.track("s", [{ id: "i1", position: 1, cell: a, text: "input" }]);

  const taken = projection.take("s", "i1");
  assertEquals(taken, { cell: a, text: "input", position: 1 });
  assertEquals(projection.positionOf(a), undefined);
  assertEquals(changes.length, 2); // track + take

  assertEquals(projection.take("s", "i1"), undefined); // already taken
  assertEquals(projection.take("other", "i1"), undefined);
});

Deno.test("apply drops finished entries and keeps waiting ones", () => {
  const { projection } = countedProjection();
  const a = { id: "a" };
  const b = { id: "b" };
  projection.track("s", [
    { id: "i1", position: 1, cell: a, text: "a" },
    { id: "i2", position: 2, cell: b, text: "b" },
  ]);

  const finished = readQueueState(snapshot({ items: [{ id: "i2" }] }));
  assert(finished !== undefined);
  projection.apply("s", finished);
  assertEquals(projection.positionOf(a), undefined); // i1 finished
  assertEquals(projection.positionOf(b), 1); // i2 moved up

  const drained = readQueueState(snapshot());
  assert(drained !== undefined);
  projection.apply("s", drained);
  assertEquals(projection.positionOf(b), undefined);
});

Deno.test("drop removes one entry and notifications fire only on change", () => {
  const { projection, changes } = countedProjection();
  const a = { id: "a" };
  projection.drop("s", "i1"); // nothing tracked: no change
  projection.track("s", [{ id: "i1", position: 1, cell: a, text: "a" }]);
  assertEquals(changes.length, 1);

  projection.drop("s", "i1");
  assertEquals(projection.positionOf(a), undefined);
  assertEquals(changes.length, 2);
  projection.drop("s", "i1"); // already gone
  assertEquals(changes.length, 2);
});

Deno.test("entriesWhere and clearCells scope to the matching cells", () => {
  const { projection } = countedProjection();
  const mine = { id: "mine" };
  const other = { id: "other" };
  projection.track("one", [{ id: "i1", position: 1, cell: mine, text: "m" }]);
  projection.track("two", [{ id: "i2", position: 1, cell: other, text: "o" }]);

  const entries = projection.entriesWhere((cell) => cell.id === "mine");
  assertEquals(entries, [{ sessionId: "one", itemId: "i1", text: "m" }]);

  assertEquals(projection.clearCells((cell) => cell.id === "mine"), 1);
  assertEquals(projection.positionOf(mine), undefined);
  assertEquals(projection.positionOf(other), 1);
  assertEquals(projection.clearCells((cell) => cell.id === "mine"), 0);
});

Deno.test("failWaits resolves pending drain waits as false", async () => {
  // The client is never reached: the wait is failed before any reconcile.
  const client = FrontendClient.fromDiscovery({
    version: 1,
    url: "http://127.0.0.1:9",
    token: "t",
    pid: 0,
  });
  const watcher = new QueueWatcher(client, "s", () => {}, () => {});
  const wait = watcher.awaitDrainedAsync(0, ["item-1"]);
  watcher.failWaits();
  assertEquals(await wait, false);
});
