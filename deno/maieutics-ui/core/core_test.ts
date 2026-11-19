import { assert, assertEquals, assertRejects, assertThrows } from "@std/assert";
import { FixtureConversation } from "../fixtures/conversation.ts";
import { ConversationRuntime } from "./runtime.ts";
import { OwnedTasks } from "./tasks.ts";
import { FrozenScrollLayout } from "./scroll_layout.ts";
import {
  compareRevision,
  type ConversationEntry,
  ConversationError,
  type ConversationPage,
  type ConversationRow,
  type ConversationSource,
} from "./types.ts";

function entry(ordinal: number, revision = "0"): ConversationEntry {
  return {
    id: `e${ordinal}`,
    ordinal,
    interactionId: "turn",
    kind: "message",
    contentRevision: revision,
    preview: `record:${ordinal}`,
    state: "open",
  };
}
function source(total = 100): ConversationSource {
  return {
    readPage(query, signal) {
      signal.throwIfAborted();
      const offset = query.offset ?? Math.max(0, total - query.limit);
      return Promise.resolve({
        sessionId: "s",
        epoch: "epoch",
        revision: "0",
        total,
        entries: Array.from(
          { length: Math.min(query.limit, total - offset) },
          (_, i) => entry(i + offset),
        ),
      });
    },
    readContent(id, revision, signal) {
      signal.throwIfAborted();
      return Promise.resolve({ entryId: id, revision, text: `record:${id.slice(1)}` });
    },
  };
}

Deno.test("held scroll geometry survives page splitting and measured entry eviction", () => {
  const rows: ConversationRow[] = [
    { type: "entry", key: "e0", ordinal: 0 },
    { type: "range", key: "gap", from: 1, count: 98 },
    { type: "entry", key: "e99", ordinal: 99 },
  ];
  const layout = new FrozenScrollLayout(rows, new Map([["e0", 80], ["e99", 240]]), 120, 12_080);
  const split: ConversationRow[] = [
    rows[0],
    { type: "range", key: "before", from: 1, count: 49 },
    ...Array.from({ length: 10 }, (_, i): ConversationRow => ({
      type: "entry",
      key: `e${50 + i}`,
      ordinal: 50 + i,
    })),
    { type: "range", key: "after", from: 60, count: 39 },
    rows[2],
  ];
  assertEquals(split.reduce((sum, row) => sum + layout.height(row), 0), layout.extent);
  const merged: ConversationRow = { type: "range", key: "evicted", from: 0, count: 100 };
  assertEquals(layout.height(merged), layout.extent);
  assertEquals(layout.locate(merged, 79), { ordinal: 0, inside: 79 });
  assertEquals(layout.locate(merged, 80), { ordinal: 1, inside: 0 });
  assertEquals(layout.locate(merged, 11_840), { ordinal: 99, inside: 0 });
  assertEquals(layout.locate(merged, 12_079), { ordinal: 99, inside: 239 });
  assertEquals(layout.locate(split[2], 73), { ordinal: 50, inside: 73 });
});

Deno.test("decimal revisions retain precision above Number.MAX_SAFE_INTEGER", () => {
  assert(compareRevision("9007199254740993", "9007199254740992") > 0);
  assert(compareRevision("100", "99") > 0);
  assertThrows(() => compareRevision("01", "0"), ConversationError, "invalid_revision");
});

Deno.test("mixed messages and tool updates preserve identity, order, and narrow subscriptions", async () => {
  const fixture = new FixtureConversation("s", 1);
  const runtime = new ConversationRuntime("s", fixture);
  await runtime.loadPage();
  const stop = fixture.subscribe((change) => runtime.apply(change));
  const id = fixture.submit("new input");
  const row = runtime.projection.rows.find((row) => row.key === id);
  let indexChanges = 0, blockChanges = 0;
  const stopIndex = runtime.subscribe(() => indexChanges++);
  const stopBlock = runtime.subscribeEntry(id, () => blockChanges++);
  const original = await runtime.readText(id, "1");
  fixture.append(id, "\ncontinued");
  assertEquals(runtime.projection.rows.find((item) => item.key === id), row);
  assertEquals(runtime.cachedText(id, "2"), original + "\ncontinued");
  assertEquals(indexChanges, 0);
  assertEquals(blockChanges, 1);
  fixture.seal(id);
  assertEquals(runtime.projection.find(id)?.state, "sealed");
  stop();
  stopIndex();
  stopBlock();
  await runtime.dispose();
});

Deno.test("an invalid atomic change cannot publish its valid prefix", async () => {
  const runtime = new ConversationRuntime("s", source(4));
  await runtime.loadPage();
  assertThrows(
    () =>
      runtime.apply({
        epoch: "epoch",
        baseRevision: "0",
        revision: "1",
        total: 4,
        operations: [
          { type: "upsert", entry: { ...entry(0, "1"), preview: "new" } },
          { type: "append", id: "e1", baseRevision: "9", revision: "10", text: "wrong base" },
        ],
      }),
    ConversationError,
    "content_revision_gap",
  );
  assertEquals(runtime.projection.find("e0")?.preview, "record:0");
  assertEquals(runtime.projection.revision, "0");
  await runtime.dispose();
});

Deno.test("duplicate delivery is idempotent and gaps/epochs are explicit", async () => {
  const runtime = new ConversationRuntime("s", source(2));
  await runtime.loadPage();
  const change = {
    epoch: "epoch",
    baseRevision: "0",
    revision: "1",
    total: 2,
    operations: [{ type: "upsert" as const, entry: entry(0, "1") }],
  };
  assert(runtime.apply(change));
  assertEquals(runtime.apply(change), false);
  assertThrows(
    () => runtime.apply({ ...change, revision: "3" }),
    ConversationError,
    "projection_gap",
  );
  assertThrows(
    () => runtime.apply({ ...change, epoch: "replacement" }),
    ConversationError,
    "epoch_changed",
  );
  await runtime.dispose();
});

Deno.test("a delayed page replays its strict successors and cannot resurrect removed entries", async () => {
  const base = source();
  const captured = Promise.withResolvers<void>(), release = Promise.withResolvers<void>();
  let delayed = false;
  const port: ConversationSource = {
    ...base,
    async readPage(query, token) {
      const page = await base.readPage(query, token);
      if (delayed) {
        captured.resolve();
        await release.promise;
      }
      return page;
    },
  };
  const runtime = new ConversationRuntime("s", port, { entries: 20, pageSize: 10 });
  await runtime.loadPage();
  delayed = true;
  const pending = runtime.loadPage(0);
  await captured.promise;
  runtime.apply({
    epoch: "epoch",
    baseRevision: "0",
    revision: "1",
    total: 100,
    operations: [
      { type: "upsert", entry: { ...entry(0, "1"), preview: "newest" } },
      { type: "remove", id: "e1", ordinal: 1 },
    ],
  });
  release.resolve();
  await pending;
  assertEquals(runtime.projection.get(0)?.preview, "newest");
  assertEquals(runtime.projection.get(1)?.kind, "unavailable");
  await runtime.dispose();
});

Deno.test("overflowing snapshot reconciliation fails explicitly without applying a stale page", async () => {
  const base = source(),
    captured = Promise.withResolvers<void>(),
    release = Promise.withResolvers<void>();
  let delayed = false;
  const runtime = new ConversationRuntime("s", {
    ...base,
    async readPage(query, token) {
      const page = await base.readPage(query, token);
      if (delayed) {
        captured.resolve();
        await release.promise;
      }
      return page;
    },
  }, { entries: 20, pageSize: 10, reconciliationBytes: 16 });
  await runtime.loadPage();
  delayed = true;
  const pending = runtime.loadPage(0);
  const rejected = assertRejects(() => pending, ConversationError, "snapshot_gap");
  await captured.promise;
  runtime.apply({
    epoch: "epoch",
    baseRevision: "0",
    revision: "1",
    total: 100,
    operations: [{ type: "upsert", entry: entry(99, "1") }],
  });
  release.resolve();
  await rejected;
  assertEquals(runtime.projection.get(0), undefined);
  assertEquals(runtime.projection.get(99)?.contentRevision, "1");
  await runtime.dispose();
});

Deno.test("100,000 entries retain only bounded pages, ranges and exact content bodies", async () => {
  const fixture = new FixtureConversation("s", 10_000);
  const runtime = new ConversationRuntime("s", fixture, {
    entries: 160,
    pageSize: 40,
    contentBytes: 2048,
  });
  await runtime.loadPage();
  assertEquals(runtime.projection.total, 100_000);
  assertEquals(runtime.statistics.entries, 40);
  assertEquals(fixture.contentReads, 0);
  for (const offset of [0, 5000, 10_000, 50_000, 80_000]) {
    await runtime.loadPage(offset);
    for (let i = offset; i < offset + 3; i++) await runtime.readText(`entry-${i}`, "0");
    assert(runtime.statistics.entries <= 160);
    assert(runtime.statistics.ranges <= runtime.limits.ranges);
    assert(runtime.statistics.contentBytes <= 2048);
  }
  await runtime.dispose();
  assertEquals(runtime.statistics.entries, 0);
  assertEquals(runtime.statistics.contentBytes, 0);
});

Deno.test("a page predating request admission cannot resurrect evicted changed state", async () => {
  const runtime = new ConversationRuntime("s", source(), { entries: 10, pageSize: 10 });
  await runtime.loadPage();
  runtime.apply({
    epoch: "epoch",
    baseRevision: "0",
    revision: "1",
    total: 100,
    operations: [{ type: "remove", id: "e0", ordinal: 0 }],
  });
  await assertRejects(() => runtime.loadPage(0), ConversationError, "snapshot_gap");
  assertEquals(runtime.projection.get(0), undefined);
  await runtime.dispose();
});

Deno.test("page capacity refuses eviction of a view's active entries", async () => {
  const runtime = new ConversationRuntime("s", source(), { entries: 10, pageSize: 10 });
  await runtime.loadPage(0);
  const view = runtime.attachView("one");
  view.setVisible(Array.from({ length: 10 }, (_, i) => i));
  await runtime.loadPage(90);
  for (let i = 0; i < 10; i++) assertEquals(runtime.projection.get(i)?.id, `e${i}`);
  view.dispose();
  await runtime.dispose();
});

Deno.test("two views keep independent drafts and late acceptance preserves newer edits", async () => {
  const runtime = new ConversationRuntime("s", source());
  const first = runtime.attachView("first"), second = runtime.attachView("second");
  first.setDraft("submitted");
  second.setDraft("other view");
  const submitted = first.captureSubmission();
  first.setDraft("new typing");
  runtime.confirmSubmission(first.id, submitted.revision);
  assertEquals(first.draft, "new typing");
  assertEquals(second.draft, "other view");
  first.dispose();
  assertEquals(runtime.statistics.views, 1);
  second.setDraft("still editable");
  await runtime.dispose();
  assert(second.signal.aborted);
});

Deno.test("exact content cannot substitute a different source revision", async () => {
  const port = source();
  const runtime = new ConversationRuntime("s", {
    ...port,
    readContent: (id) => Promise.resolve({ entryId: id, revision: "8", text: "newer text" }),
  });
  await assertRejects(() => runtime.readText("e0", "0"), ConversationError, "content_revision_gap");
  assertEquals(runtime.statistics.contentBytes, 0);
  await runtime.dispose();
});

Deno.test("queued requests cancel before start and capacity is bounded", async () => {
  const tasks = new OwnedTasks(1, 1),
    started = Promise.withResolvers<void>(),
    release = Promise.withResolvers<void>();
  const first = tasks.run(async () => {
    started.resolve();
    await release.promise;
    return 1;
  });
  await started.promise;
  const cancel = new AbortController();
  let ran = false;
  const second = tasks.run(() => {
    ran = true;
    return Promise.resolve(2);
  }, cancel.signal);
  const rejected = assertRejects(() => second, DOMException);
  await assertRejects(
    () => tasks.run(() => Promise.resolve(3)),
    ConversationError,
    "request_capacity",
  );
  cancel.abort();
  await rejected;
  assertEquals(ran, false);
  release.resolve();
  assertEquals(await first, 1);
  await tasks.dispose();
});

Deno.test("disposal joins an ignoring source and fences its late reply", async () => {
  const port = source(),
    started = Promise.withResolvers<void>(),
    release = Promise.withResolvers<void>();
  let updates = 0;
  const runtime = new ConversationRuntime("s", {
    ...port,
    async readContent(id, revision) {
      started.resolve();
      await release.promise;
      return { entryId: id, revision, text: "late" };
    },
  });
  const pending = runtime.readText("e0", "0");
  const rejected = assertRejects(() => pending, DOMException);
  await started.promise;
  runtime.subscribe(() => updates++);
  let disposed = false;
  const completion = runtime.dispose();
  assertEquals(runtime.dispose(), completion);
  const observed = completion.then(() => {
    disposed = true;
  });
  assertEquals(disposed, false);
  release.resolve();
  await rejected;
  await observed;
  assertEquals(runtime.statistics.contentBytes, 0);
  assertEquals(updates, 0);
});

Deno.test("logical copying spans evicted rows without expanding resident metadata", async () => {
  const runtime = new ConversationRuntime("s", source(1000), { entries: 10, pageSize: 10 });
  await runtime.loadPage();
  const view = runtime.attachView("copy");
  view.setSelection({
    epoch: "epoch",
    projectionRevision: "0",
    anchor: { id: "e0", ordinal: 0, revision: "0", offset: 0 },
    focus: { id: "e79", ordinal: 79, revision: "0", offset: 9 },
  });
  const copied = await view.copySelection();
  assertEquals(copied, Array.from({ length: 80 }, (_, i) => `record:${i}`).join("\n\n"));
  assertEquals(runtime.statistics.entries, 10);
  assertEquals(view.pins.size, 2);
  await runtime.dispose();
});

Deno.test("selection capacity and stale historical snapshots never return partial/newer copy", async () => {
  const runtime = new ConversationRuntime("s", source(), { selectionBytes: 10 });
  await runtime.loadPage();
  const selection = {
    epoch: "epoch",
    projectionRevision: "0",
    anchor: { id: "e0", ordinal: 0, revision: "0", offset: 0 },
    focus: { id: "e1", ordinal: 1, revision: "0", offset: 8 },
  };
  await assertRejects(
    () => runtime.copySelection(selection, new Set()),
    ConversationError,
    "selection_capacity",
  );
  const base = source();
  const changed = new ConversationRuntime("s", {
    ...base,
    async readPage(query, token): Promise<ConversationPage> {
      return {
        ...await base.readPage(query, token),
        revision: query.revision === undefined ? "0" : "1",
      };
    },
  });
  await changed.loadPage();
  await assertRejects(
    () => changed.copySelection(selection, new Set()),
    ConversationError,
    "selection_unavailable",
  );
  await runtime.dispose();
  await changed.dispose();
});

Deno.test("an old snapshot cannot collide two stable entry identities", async () => {
  const base = source(2);
  const runtime = new ConversationRuntime("s", {
    ...base,
    async readPage(query, token) {
      const page = await base.readPage(query, token);
      return { ...page, entries: [{ ...entry(0), id: "same" }, { ...entry(1), id: "same" }] };
    },
  });
  await assertRejects(() => runtime.loadPage(), ConversationError, "invalid_page");
  assertEquals(runtime.statistics.entries, 0);
  await runtime.dispose();
});
