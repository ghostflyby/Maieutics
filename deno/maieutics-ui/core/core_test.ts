import { assert, assertEquals, assertRejects, assertThrows } from "@std/assert";
import { FixtureConversation } from "../fixtures/conversation.ts";
import { ConversationRuntime } from "./runtime.ts";
import { OwnedTasks } from "./tasks.ts";
import { FrozenScrollLayout } from "./scroll_layout.ts";
import { ConversationProjection } from "./projection.ts";
import {
  compareRevision,
  type ConversationEntry,
  ConversationError,
  type ConversationPage,
  type ConversationRow,
  type ConversationSource,
  defaultLimits,
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
  const rows = runtime.projection.rows;
  await assertRejects(() => runtime.loadPage(90), ConversationError, "index_capacity");
  assertEquals(runtime.projection.rows, rows);
  for (let i = 0; i < 10; i++) assertEquals(runtime.projection.get(i)?.id, `e${i}`);
  view.dispose();
  await runtime.dispose();
});

Deno.test("fragment pruning reports range_capacity atomically through loadPage", async () => {
  const runtime = new ConversationRuntime("s", source(), { entries: 40, pageSize: 10, ranges: 1 });
  try {
    await runtime.loadPage(0);
    await runtime.loadPage(90);
    const view = runtime.attachView("isolated-records");
    view.setVisible([5, 95]);
    const before = runtime.projection.rows;
    await assertRejects(() => runtime.loadPage(40), ConversationError, "range_capacity");
    assert(runtime.projection.rows === before, "fragment pruning published its failed candidate");
    assertEquals(runtime.projection.get(40), undefined);
    assertEquals(runtime.projection.get(5)?.id, "e5");
    assertEquals(runtime.projection.get(95)?.id, "e95");
  } finally {
    await runtime.dispose();
  }
});

Deno.test("an HTTP page ahead of the stream waits for an append-only reducer watermark", async () => {
  const base = source();
  const delivery = Promise.withResolvers<ConversationPage>();
  const runtime = new ConversationRuntime("s", {
    ...base,
    readPage: (query, signal) =>
      query.offset === 0 ? delivery.promise : base.readPage(query, signal),
  }, { entries: 20, pageSize: 10 });
  try {
    await runtime.loadPage();
    const loading = runtime.loadPage(0);
    const completion = loading.then(() => undefined, (error) => error);
    const page = await base.readPage({ offset: 0, limit: 10 }, new AbortController().signal);
    delivery.resolve({ ...page, revision: "1" });
    await delivery.promise;
    assertEquals(runtime.projection.revision, "0", "HTTP must not advance the stream cursor");
    assertEquals(runtime.projection.get(0), undefined, "future rows published before the stream");
    runtime.apply({
      epoch: "epoch",
      baseRevision: "0",
      revision: "1",
      total: 100,
      operations: [{ type: "append", id: "e99", baseRevision: "0", revision: "1", text: "next" }],
    });
    assertEquals(await completion, undefined, "a caught-up page must not fail with projection_gap");
    assertEquals(runtime.projection.get(0)?.id, "e0");
    assertEquals(runtime.projection.get(99)?.contentRevision, "1");
    assertEquals(runtime.projection.revision, "1");
  } finally {
    await runtime.dispose();
  }
});

Deno.test("one stale HTTP snapshot retries a fresh page instead of poisoning its range", async () => {
  const base = source();
  let headReads = 0;
  const runtime = new ConversationRuntime("s", {
    ...base,
    async readPage(query, signal) {
      const page = await base.readPage(query, signal);
      if (query.offset !== 0 || headReads++ === 0) return page;
      return {
        ...page,
        revision: "1",
        entries: page.entries.map((record) =>
          record.ordinal === 0
            ? {
              ...record,
              kind: "unavailable" as const,
              contentRevision: "1",
              state: "sealed" as const,
            }
            : record
        ),
      };
    },
  }, { entries: 20, pageSize: 10 });
  try {
    await runtime.loadPage();
    runtime.apply({
      epoch: "epoch",
      baseRevision: "0",
      revision: "1",
      total: 100,
      operations: [{ type: "remove", id: "e0", ordinal: 0 }],
    });
    await runtime.loadPage(0);
    assertEquals(headReads, 2);
    assertEquals(runtime.projection.get(0)?.kind, "unavailable");
  } finally {
    await runtime.dispose();
  }
});

Deno.test("concurrent first pages choose one baseline before waiting for a newer page", async () => {
  const base = source();
  const first = Promise.withResolvers<ConversationPage>(),
    second = Promise.withResolvers<ConversationPage>();
  const runtime = new ConversationRuntime("s", {
    ...base,
    readPage: (query) => query.offset === 0 ? first.promise : second.promise,
  }, { entries: 20, pageSize: 10 });
  const headPage = await base.readPage({ offset: 0, limit: 10 }, runtime.signal);
  const tailPage = await base.readPage({ offset: 90, limit: 10 }, runtime.signal);
  const head = runtime.loadPage(0), tail = runtime.loadPage(90);
  const observed = tail.then(() => undefined, (error) => error);
  try {
    first.resolve(headPage);
    second.resolve({ ...tailPage, revision: "1" });
    await head;
    runtime.apply({ epoch: "epoch", baseRevision: "0", revision: "1", total: 100, operations: [] });
    assertEquals(await observed, undefined, "concurrent initialization rejected its newer page");
    assertEquals(runtime.projection.get(0)?.id, "e0");
    assertEquals(runtime.projection.get(99)?.id, "e99");
  } finally {
    await runtime.dispose();
  }
});

Deno.test("a concurrent initial page older than the chosen baseline must fetch fresh records", async () => {
  const base = source();
  const first = Promise.withResolvers<ConversationPage>(),
    second = Promise.withResolvers<ConversationPage>();
  let tailReads = 0;
  const runtime = new ConversationRuntime("s", {
    ...base,
    readPage(query, signal) {
      if (query.offset === 0) return first.promise;
      if (tailReads++ === 0) return second.promise;
      return base.readPage(query, signal).then((page) => ({ ...page, revision: "1" }));
    },
  }, { entries: 20, pageSize: 10 });
  const head = runtime.loadPage(0), tail = runtime.loadPage(90);
  const observed = tail.then(() => undefined, (error) => error);
  try {
    first.resolve({
      ...await base.readPage({ offset: 0, limit: 10 }, runtime.signal),
      revision: "1",
    });
    second.resolve(await base.readPage({ offset: 90, limit: 10 }, runtime.signal));
    await head;
    assertEquals(await observed, undefined);
    assertEquals(
      tailReads,
      2,
      "pre-baseline page was admitted without its missing change evidence",
    );
    assertEquals(runtime.projection.revision, "1");
  } finally {
    await runtime.dispose();
  }
});

Deno.test("aborted pages never publish even when the source returns after ignoring cancellation", async () => {
  const base = source();
  const delivery = Promise.withResolvers<ConversationPage>();
  const runtime = new ConversationRuntime("s", {
    ...base,
    readPage: (query, signal) =>
      query.offset === 0 ? delivery.promise : base.readPage(query, signal),
  });
  await runtime.loadPage();
  const before = runtime.projection.rows;
  const caller = new AbortController();
  let publications = 0;
  runtime.subscribe(() => publications++);
  const loading = runtime.loadPage(0, caller.signal);
  const rejected = assertRejects(() => loading, DOMException);
  caller.abort();
  delivery.resolve(await base.readPage({ offset: 0, limit: 10 }, runtime.signal));
  await rejected;
  assertEquals(publications, 0, "cancelled page emitted a projection publication");
  assertEquals(runtime.projection.rows, before);
  assertEquals(runtime.projection.get(0), undefined);
  await runtime.dispose();
});

Deno.test("abort after HTTP delivery but before commit leaves projection unpublished", async () => {
  const base = source(), delivery = Promise.withResolvers<ConversationPage>();
  const runtime = new ConversationRuntime("s", {
    ...base,
    readPage: (query, signal) =>
      query.offset === 0 ? delivery.promise : base.readPage(query, signal),
  });
  await runtime.loadPage();
  const caller = new AbortController();
  let publications = 0;
  runtime.subscribe(() => publications++);
  const loading = runtime.loadPage(0, caller.signal);
  const rejected = assertRejects(() => loading, DOMException);
  delivery.resolve(await base.readPage({ offset: 0, limit: 10 }, runtime.signal));
  await delivery.promise;
  caller.abort();
  await rejected;
  assertEquals(publications, 0);
  assertEquals(runtime.projection.get(0), undefined);
  await runtime.dispose();
});

Deno.test("cancellation from publication observes a committed page as successful", async () => {
  const runtime = new ConversationRuntime("s", source());
  await runtime.loadPage();
  const caller = new AbortController();
  runtime.subscribe(() => caller.abort());
  try {
    await runtime.loadPage(0, caller.signal);
    assert(caller.signal.aborted, "test never cancelled from the page publication");
    assertEquals(runtime.projection.get(0)?.id, "e0");
  } finally {
    await runtime.dispose();
  }
});

Deno.test("cancellation and disposal release a page waiting for a future stream revision", async () => {
  for (const dispose of [false, true]) {
    const base = source();
    const delivery = Promise.withResolvers<ConversationPage>();
    const runtime = new ConversationRuntime("s", {
      ...base,
      readPage: (query, signal) =>
        query.offset === 0 ? delivery.promise : base.readPage(query, signal),
    }, { entries: 20, pageSize: 10 });
    await runtime.loadPage();
    const caller = new AbortController();
    const loading = runtime.loadPage(0, caller.signal);
    const rejected = assertRejects(() => loading, DOMException);
    delivery.resolve({
      ...await base.readPage({ offset: 0, limit: 10 }, caller.signal),
      revision: "2",
    });
    await delivery.promise;
    if (dispose) await runtime.dispose();
    else caller.abort();
    assertEquals((await rejected).name, "AbortError");
    await runtime.dispose();
    assertEquals(runtime.statistics.activeRequests + runtime.statistics.queuedRequests, 0);
  }
});

Deno.test("stream waits expose progress, expire without publication, and allow a new request", async () => {
  const base = source();
  let reads = 0;
  const runtime = new ConversationRuntime("s", {
    ...base,
    async readPage(query, signal) {
      const page = await base.readPage(query, signal);
      if (query.offset !== 0) return page;
      reads++;
      return { ...page, revision: "1" };
    },
  }, { pageWaitMilliseconds: 1 });
  const phases: string[] = [];
  try {
    await runtime.loadPage();
    await assertRejects(
      () => runtime.loadPage(0, undefined, (state) => phases.push(state.phase)),
      ConversationError,
      "stream_wait_timeout",
    );
    assertEquals(phases, ["reading", "waiting-for-stream"]);
    assertEquals(
      runtime.statistics.waitingPages,
      0,
      "expired waiter retained its progress listener",
    );
    assertEquals(runtime.projection.get(0), undefined, "expired waiter published its snapshot");
    runtime.apply({ epoch: "epoch", baseRevision: "0", revision: "1", total: 100, operations: [] });
    await runtime.loadPage(0);
    assertEquals(reads, 2);
    assertEquals(runtime.projection.get(0)?.id, "e0");
  } finally {
    await runtime.dispose();
  }
});

Deno.test("cancellation from waiting progress clears its deadline and prevents page publication", async () => {
  const base = source(), caller = new AbortController();
  const runtime = new ConversationRuntime("s", {
    ...base,
    async readPage(query, signal) {
      const page = await base.readPage(query, signal);
      return query.offset === 0 ? { ...page, revision: "1" } : page;
    },
  });
  try {
    await runtime.loadPage();
    await assertRejects(() =>
      runtime.loadPage(0, caller.signal, (state) => {
        if (state.phase === "waiting-for-stream") caller.abort();
      }), DOMException);
    assertEquals(runtime.statistics.waitingPages, 0);
    assertEquals(runtime.projection.get(0), undefined);
  } finally {
    await runtime.dispose();
  }
});

Deno.test("two pinned Chinese view windows render exact text beyond the shared cache budget", async () => {
  const base = source(68);
  const text = "中".repeat(200_000);
  const runtime = new ConversationRuntime("s", {
    ...base,
    readContent: (id, revision) => Promise.resolve({ entryId: id, revision, text }),
  });
  try {
    await runtime.loadPage();
    const first = runtime.attachView("first"), second = runtime.attachView("second");
    first.setVisible(Array.from({ length: 34 }, (_, i) => i));
    second.setVisible(Array.from({ length: 34 }, (_, i) => i + 34));
    for (let i = 0; i < 68; i++) assertEquals(await runtime.readText(`e${i}`, "0"), text);
    assert(runtime.statistics.contentBytes <= runtime.limits.contentBytes);
    assertEquals(runtime.cachedText("e67", "0"), undefined, "over-budget reads bypass retention");
  } finally {
    await runtime.dispose();
  }
});

Deno.test("invalid selection ordinals fail before any historical page read", async () => {
  const base = source();
  let reads = 0;
  const runtime = new ConversationRuntime("s", {
    ...base,
    readPage(query, signal) {
      reads++;
      return base.readPage(query, signal);
    },
  });
  await runtime.loadPage();
  const before = reads;
  try {
    for (const invalid of [NaN, Infinity, -Infinity, 0.5, Number.MAX_SAFE_INTEGER + 1]) {
      for (const endpoint of ["anchor", "focus"] as const) {
        const point = { id: "e0", ordinal: 0, revision: "0", offset: 0 };
        await assertRejects(
          () =>
            runtime.copySelection({
              epoch: "epoch",
              projectionRevision: "0",
              anchor: point,
              focus: point,
              [endpoint]: { ...point, ordinal: invalid },
            }, new Set()),
          ConversationError,
          "invalid_selection",
        );
      }
    }
    assertEquals(reads, before);
  } finally {
    await runtime.dispose();
  }
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

for (const concurrency of [1, 4]) {
  Deno.test(`uncached copying progresses with ${concurrency} occupied source request slots`, async () => {
    const base = source();
    const contentReads: string[] = [];
    const runtime = new ConversationRuntime("s", {
      ...base,
      readContent(id, revision, signal) {
        contentReads.push(id);
        return base.readContent(id, revision, signal);
      },
    }, { concurrentRequests: concurrency });
    // A deadline bounds a broken test's wait; success requires every exact copy and later source read.
    const budget = AbortSignal.timeout(10_000);
    try {
      await runtime.loadPage(undefined, budget);
      assertEquals(runtime.statistics.contentBytes, 0);
      const copies = Array.from({ length: concurrency }, (_, ordinal) => {
        const text = `record:${ordinal}`;
        return runtime.copySelection(
          {
            epoch: "epoch",
            projectionRevision: "0",
            anchor: { id: `e${ordinal}`, ordinal, revision: "0", offset: 0 },
            focus: { id: `e${ordinal}`, ordinal, revision: "0", offset: text.length },
          },
          new Set(),
          budget,
        );
      });
      assertEquals(
        await Promise.all(copies),
        Array.from({ length: concurrency }, (_, ordinal) => `record:${ordinal}`),
        "historical copying retained parent request slots while waiting for its text reads",
      );
      assertEquals(contentReads.sort(), Array.from({ length: concurrency }, (_, i) => `e${i}`));
      await runtime.loadPage(0, budget);
      assertEquals(
        await runtime.readText("e10", "0", budget),
        "record:10",
        "copying exhausted the shared source pool for later page/content requests",
      );
    } finally {
      await runtime.dispose();
    }
  });
}

Deno.test("projection clear restores an empty baseline before merging another epoch", () => {
  const projection = new ConversationProjection("s", defaultLimits);
  projection.merge(
    {
      sessionId: "s",
      epoch: "old",
      revision: "7",
      total: 3,
      entries: [entry(0, "7")],
    },
    [],
    new Set(),
    0,
  );
  projection.clear();
  projection.clear();
  assertEquals(projection.epoch, undefined, "clear retained the previous epoch");
  assertEquals(projection.revision, "0", "clear retained the previous stream baseline");
  assertEquals(projection.total, 0, "clear retained a total without any rows");
  assertEquals(projection.size, 0);
  assertEquals(projection.rows, []);
  assertEquals(projection.find("e0"), undefined);
  projection.merge(
    {
      sessionId: "s",
      epoch: "new",
      revision: "1",
      total: 1,
      entries: [entry(0, "1")],
    },
    [],
    new Set(),
    0,
  );
  assertEquals(projection.epoch, "new");
  assertEquals(projection.revision, "1");
  assertEquals(projection.total, 1);
  assertEquals(projection.get(0)?.contentRevision, "1");
});

Deno.test("overflowed reconciliation stops serializing later stream frames", async () => {
  const base = source();
  const entered = Promise.withResolvers<void>(), release = Promise.withResolvers<void>();
  let held = false, serializations = 0;
  const runtime = new ConversationRuntime("s", {
    ...base,
    async readPage(query, signal) {
      const page = await base.readPage(query, signal);
      if (held) {
        entered.resolve();
        await release.promise;
      }
      return page;
    },
  }, { reconciliationBytes: 16 });
  await runtime.loadPage();
  held = true;
  const pending = runtime.loadPage(0);
  const rejected = assertRejects(() => pending, ConversationError, "snapshot_gap");
  try {
    await entered.promise;
    for (const revision of [1, 2]) {
      const change = {
        epoch: "epoch",
        baseRevision: String(revision - 1),
        revision: String(revision),
        total: 100,
        operations: [],
        toJSON() {
          serializations++;
          return { revision: this.revision, payload: "larger than the reconciliation budget" };
        },
      };
      runtime.apply(change);
    }
    assertEquals(serializations, 1, "an already overflowed page kept serializing stream frames");
    assertEquals(
      runtime.projection.revision,
      "2",
      "journal overflow stopped ordered stream reduction",
    );
  } finally {
    release.resolve();
    await rejected;
    await runtime.dispose();
  }
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
