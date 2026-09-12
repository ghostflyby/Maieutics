import { assert, assertEquals } from "@std/assert";
import { CellQueue } from "./queueState.ts";

/** Distinct cell identities (reference equality is the contract). */
function cell(): { id: number } {
  return { id: nextId++ };
}
let nextId = 0;

/** A queue whose transitions are counted, like the status bar refresh. */
function countedQueue(): { queue: CellQueue<{ id: number }>; changes: number[] } {
  const changes: number[] = [];
  const queue = new CellQueue<{ id: number }>();
  queue.onDidChange(() => changes.push(changes.length + 1));
  return { queue, changes };
}

Deno.test("enqueue tracks cells in batch order and reports 1-based positions", () => {
  const { queue } = countedQueue();
  const a = cell();
  const b = cell();
  const c = cell();
  queue.enqueue("nb", [a, b]);
  queue.enqueue("nb", [c]);

  assertEquals(queue.position("nb", a), 1);
  assertEquals(queue.position("nb", b), 2);
  assertEquals(queue.position("nb", c), 3);
  assertEquals(queue.size("nb"), 3);
});

Deno.test("positions are per notebook", () => {
  const { queue } = countedQueue();
  const a = cell();
  queue.enqueue("one", [a]);
  assertEquals(queue.position("two", a), undefined);
  assertEquals(queue.size("two"), 0);
});

Deno.test("remove takes the earliest occurrence, shifts positions, and excludes only its batch", () => {
  const { queue } = countedQueue();
  const a = cell();
  const b = cell();
  const first = queue.enqueue("nb", [a, b]);
  const second = queue.enqueue("nb", [a]);

  // The user dequeues the cell while it waits in the first batch.
  assert(queue.remove("nb", a));
  assertEquals(first.isExcluded(a), true);
  assertEquals(second.isExcluded(a), false);
  assertEquals(queue.position("nb", b), 1); // shifted up as the copy left
  assertEquals(queue.position("nb", a), 2); // now the second batch's copy
  assertEquals(queue.size("nb"), 2); // b plus the second batch's own copy of a

  // The first batch's loop must skip it; the second batch's must not.
  assert(first.isExcluded(a));
  assert(!first.isExcluded(b));
});

Deno.test("remove on an unqueued cell is a reported miss, not a state change", () => {
  const { queue, changes } = countedQueue();
  assert(!queue.remove("nb", cell()));
  assertEquals(changes.length, 0);
});

Deno.test("clear excludes and drops every batch of the notebook", () => {
  const { queue } = countedQueue();
  const a = cell();
  const b = cell();
  const first = queue.enqueue("nb", [a]);
  const second = queue.enqueue("nb", [b]);

  assertEquals(queue.clear("nb"), 2);
  assertEquals(queue.size("nb"), 0);
  assertEquals(queue.position("nb", a), undefined);
  assert(first.isExcluded(a));
  assert(second.isExcluded(b));
  assertEquals(queue.clear("nb"), 0);
});

Deno.test("disposeRemaining drops only its own batch", () => {
  const { queue } = countedQueue();
  const a = cell();
  const b = cell();
  const first = queue.enqueue("nb", [a]);
  const second = queue.enqueue("nb", [b]);

  first.disposeRemaining();
  assert(first.isExcluded(a));
  assertEquals(queue.position("nb", b), 1); // moved up as the batch drained
  assertEquals(queue.size("nb"), 1);
  second.disposeRemaining();
  assertEquals(queue.size("nb"), 0);
});

Deno.test("notifications fire once per membership change and never on no-ops", () => {
  const { queue, changes } = countedQueue();
  const a = cell();

  queue.enqueue("nb", []); // empty batch: no membership change
  assertEquals(changes.length, 0);

  const batch = queue.enqueue("nb", [a]);
  assertEquals(changes.length, 1);

  queue.remove("nb", cell()); // miss
  assertEquals(changes.length, 1);

  assert(queue.remove("nb", a));
  assertEquals(changes.length, 2);

  queue.clear("nb"); // already empty after the remove dropped the key
  assertEquals(changes.length, 2);

  batch.disposeRemaining(); // nothing left to dispose
  assertEquals(changes.length, 2);
});

Deno.test("empty notebooks keys are dropped as batches drain", () => {
  const { queue } = countedQueue();
  const batch = queue.enqueue("nb", [cell()]);
  batch.disposeRemaining();
  assertEquals(queue.size("nb"), 0);
  assertEquals(queue.position("nb", cell()), undefined);
});

Deno.test("dequeue of a duplicate queued twice removes one turn per action", () => {
  const { queue } = countedQueue();
  const a = cell();
  queue.enqueue("nb", [a]);
  queue.enqueue("nb", [a]);
  assertEquals(queue.size("nb"), 2);

  assert(queue.remove("nb", a));
  assertEquals(queue.size("nb"), 1);
  assert(queue.remove("nb", a));
  assertEquals(queue.size("nb"), 0);
  assert(!queue.remove("nb", a));
});
