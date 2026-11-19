import { nextTick } from "vue";
import { type ConversationChange, type ConversationPage, ConversationRuntime } from "../mod.ts";
import { FixtureConversation } from "../fixtures/conversation.ts";
import { assert, equal, geometryApplied, guarded, waitFor } from "./browser_assertions.ts";

type Artifact = Pick<
  typeof import("../mod.ts"),
  "mountConversation" | "mountConversationHistory" | "mountConversationComposer"
>;

async function frame(): Promise<void> {
  await guarded(
    new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
    "the browser did not complete its queued layout frame",
  );
}

function historyTarget(): HTMLElement {
  const root = document.createElement("div");
  root.style.height = "600px";
  document.body.append(root);
  return root;
}

/** Controlled transport ordering and geometry successors cover the PR review regressions. */
export async function runReviewConformance(artifact: Artifact): Promise<string[]> {
  const passed: string[] = [];
  const fixture = new FixtureConversation("future", 10_000);
  const requested = Promise.withResolvers<number>();
  const response = Promise.withResolvers<ConversationPage>();
  let waiting = false;
  const runtime = new ConversationRuntime("future", {
    readPage(query, signal) {
      if (!waiting && query.offset !== undefined && query.offset < 90_000) {
        waiting = true;
        requested.resolve(query.offset);
        return response.promise;
      }
      return fixture.readPage(query, signal);
    },
    readContent: (id, revision, signal) => fixture.readContent(id, revision, signal),
  }, { entries: 160 });
  await runtime.loadPage();
  const changes: ConversationChange[] = [];
  const stop = fixture.subscribe((change) => changes.push(change));
  const futureRoot = historyTarget();
  const handle = artifact.mountConversation(futureRoot, {
    runtime,
    viewId: "future-view",
    host: { writeText: () => Promise.resolve() },
  });
  try {
    await waitFor(
      () => !!futureRoot.querySelector('[data-entry-id="entry-99999"] .mui-text'),
      "future-page fixture did not initialize",
    );
    fixture.submit("producer ahead of WebSocket delivery");
    const viewport = futureRoot.querySelector<HTMLElement>(".mui-viewport");
    assert(viewport, "future-page viewport missing");
    viewport.scrollTop = 25_000 * 120;
    viewport.dispatchEvent(new Event("scroll"));
    const offset = await guarded(requested.promise, "future HTTP page was not requested");
    response.resolve(
      await fixture.readPage({ offset, limit: runtime.limits.pageSize }, runtime.signal),
    );
    await response.promise;
    equal(runtime.projection.revision, "0", "HTTP advanced the global stream revision");
    for (const change of changes) runtime.apply(change);
    await waitFor(
      () =>
        [...futureRoot.querySelectorAll<HTMLElement>("[data-ordinal]")].some((node) =>
          Number(node.dataset.ordinal) >= offset &&
          Number(node.dataset.ordinal) < offset + runtime.limits.pageSize &&
          !!node.querySelector(".mui-text")
        ),
      "a future HTTP page remained a failed range after WebSocket catch-up",
    );
    equal(runtime.projection.revision, "1", "stream catch-up lost its own watermark");
    assert(!futureRoot.querySelector(".mui-error"), "caught-up page rendered a terminal failure");
    passed.push("future HTTP pages become readable after independently delivered stream catch-up");
  } finally {
    handle.unmount();
    stop();
    await runtime.dispose();
    futureRoot.remove();
  }

  const capacitySource = new FixtureConversation("capacity", 100);
  let capacityReads = 0;
  const capacity = new ConversationRuntime("capacity", {
    readPage(query, signal) {
      if (query.offset !== undefined && query.offset < 900) capacityReads++;
      return capacitySource.readPage(query, signal);
    },
    readContent: (id, revision, signal) => capacitySource.readContent(id, revision, signal),
  }, { entries: 80 });
  await capacity.loadPage();
  const blockerA = capacity.attachView("blocker-a"), blockerB = capacity.attachView("blocker-b");
  blockerA.setVisible(Array.from({ length: 68 }, (_, i) => 920 + i));
  blockerB.setVisible(Array.from({ length: 12 }, (_, i) => 988 + i));
  const capacityRoot = historyTarget();
  const capacityHandle = artifact.mountConversation(capacityRoot, {
    runtime: capacity,
    viewId: "capacity-reader",
    host: { writeText: () => Promise.resolve() },
  });
  try {
    await waitFor(
      () => !!capacityRoot.querySelector('[data-entry-id="entry-999"] .mui-text'),
      "capacity fixture did not initialize",
    );
    const viewport = capacityRoot.querySelector<HTMLElement>(".mui-viewport");
    assert(viewport, "capacity viewport missing");
    viewport.scrollTop = 0;
    viewport.dispatchEvent(new Event("scroll"));
    await waitFor(
      () => capacityRoot.textContent?.includes("历史缓存已被其他视图或选区占用") === true,
      "zero-progress page admission did not expose its typed capacity failure",
    );
    viewport.dispatchEvent(new Event("scroll"));
    await frame();
    equal(capacityReads, 1, "a zero-progress page was requested again on the next range scan");
    passed.push(
      "zero-progress page admission stops automatic range requests with a capacity result",
    );
  } finally {
    capacityHandle.unmount();
    await capacity.dispose();
    capacityRoot.remove();
  }

  const anchorSource = new FixtureConversation("anchor", 200);
  const anchorRuntime = new ConversationRuntime("anchor", {
    async readPage(query, signal) {
      const page = await anchorSource.readPage(query, signal);
      return {
        ...page,
        entries: page.entries.map((entry) => ({ ...entry, kind: "message" as const })),
      };
    },
    readContent: (id, revision) =>
      Promise.resolve({ entryId: id, revision, text: "measured line\n".repeat(12) }),
  }, { entries: 240 });
  await anchorRuntime.loadPage(0);
  const anchorView = anchorRuntime.attachView("anchor-reader");
  const anchorRoot = historyTarget();
  const anchorHandle = artifact.mountConversationHistory(anchorRoot, {
    view: anchorView,
    host: { writeText: () => Promise.resolve() },
  });
  try {
    await waitFor(
      () =>
        !!anchorRoot.querySelector('[data-entry-id="entry-1999"] .mui-text') &&
        geometryApplied(anchorRoot),
      "anchor fixture did not finish initial tail measurement",
    );
    await anchorRuntime.loadPage(800);
    const viewport = anchorRoot.querySelector<HTMLElement>(".mui-viewport");
    assert(viewport, "anchor viewport missing");
    viewport.scrollTop = 820 * 120 + 37;
    viewport.dispatchEvent(new Event("scroll"));
    await waitFor(
      () =>
        [...anchorRoot.querySelectorAll<HTMLElement>("[data-ordinal]")].some((node) =>
          Number(node.dataset.ordinal) >= 800 && Number(node.dataset.ordinal) < 880
        ) && !anchorRoot.querySelector(".mui-loading"),
      "anchor fixture did not enter its resident history page",
    );
    viewport.dispatchEvent(new Event("scrollend"));
    await waitFor(
      () => viewport.dataset.scrollGesture === "idle" && geometryApplied(anchorRoot),
      "history navigation did not explicitly finish its gesture",
    );
    assert(
      anchorRoot.querySelector(".mui-latest"),
      "anchor fixture unexpectedly follows live output",
    );
    anchorView.setSelection({
      epoch: anchorSource.epoch,
      projectionRevision: "0",
      anchor: { id: "entry-0", ordinal: 0, revision: "0", offset: 0 },
      focus: { id: "entry-1", ordinal: 1, revision: "0", offset: 0 },
    });
    await waitFor(
      () =>
        !!anchorRoot.querySelector('[data-entry-id="entry-0"] .mui-text') &&
        geometryApplied(anchorRoot),
      "offscreen pinned rows did not acquire their deliberately different heights",
    );
    anchorView.setSelection(undefined);
    await nextTick();
    await frame();
    equal(viewport.dataset.scrollGesture, "idle", "anchor test remained inside a frozen gesture");
    const bounds = viewport.getBoundingClientRect();
    const slot = [...anchorRoot.querySelectorAll<HTMLElement>(".mui-slot")].find((node) => {
      const rect = node.getBoundingClientRect();
      return rect.top <= bounds.top && rect.bottom > bounds.top;
    });
    const block = slot?.querySelector<HTMLElement>("[data-entry-id]");
    assert(block, "reading-history anchor was not an actual visible block");
    const anchorId = block.dataset.entryId;
    const before = block.getBoundingClientRect().top - bounds.top;
    assert(
      anchorRuntime.projection.get(0),
      "measured predecessor was evicted before the controlled transition",
    );
    await anchorRuntime.loadPage(1600);
    await waitFor(
      () => !anchorRuntime.projection.get(0) && geometryApplied(anchorRoot),
      "controlled page replacement did not evict measured predecessors",
    );
    const restored = anchorRoot.querySelector<HTMLElement>(`[data-entry-id="${anchorId}"]`);
    assert(restored, "page replacement lost the visible logical anchor");
    equal(viewport.dataset.scrollGesture, "idle", "anchor correction ran under frozen geometry");
    const after = restored.getBoundingClientRect().top - viewport.getBoundingClientRect().top;
    assert(
      Math.abs(after - before) <= 2,
      `non-frozen page replacement moved the history anchor by ${after - before} pixels`,
    );
    passed.push(
      "non-frozen history keeps its visible block offset when measured predecessors are evicted",
    );
  } finally {
    anchorHandle.unmount();
    await anchorRuntime.dispose();
    anchorRoot.remove();
  }

  const copySource = new FixtureConversation("foreign-copy", 2);
  const copyRuntime = new ConversationRuntime("foreign-copy", copySource, { selectionBytes: 1 });
  await copyRuntime.loadPage();
  const copyRoot = historyTarget();
  let clipboardWrites = 0;
  const copyHandle = artifact.mountConversation(copyRoot, {
    runtime: copyRuntime,
    viewId: "foreign-copy-view",
    host: {
      writeText: () => {
        clipboardWrites++;
        return Promise.resolve();
      },
    },
  });
  const selection = document.getSelection();
  try {
    await waitFor(
      () =>
        !!copyRoot.querySelector('[data-entry-id="entry-19"] .mui-text') &&
        geometryApplied(copyRoot),
      "cross-bundle copy fixture did not initialize",
    );
    const body = copyRoot.querySelector<HTMLElement>(".mui-message .mui-text");
    assert(body?.firstChild && selection, "cross-bundle selection text missing");
    const range = document.createRange();
    range.selectNodeContents(body);
    selection.removeAllRanges();
    selection.addRange(range);
    document.dispatchEvent(new Event("selectionchange"));
    body.dispatchEvent(new ClipboardEvent("copy", { bubbles: true, cancelable: true }));
    await waitFor(
      () => !!copyRoot.querySelector(".mui-status")?.textContent,
      "cross-bundle copy failure did not complete",
    );
    equal(
      copyRoot.querySelector(".mui-status")?.textContent,
      "选区过大，请缩小选区或导出内容。",
      "foreign ConversationError lost its stable capacity diagnostic",
    );
    equal(clipboardWrites, 0, "failed copy wrote partial text to the clipboard");
    passed.push(
      "cross-bundle copy capacity failures preserve their diagnostic and never write partial text",
    );
  } finally {
    selection?.removeAllRanges();
    document.dispatchEvent(new Event("selectionchange"));
    copyHandle.unmount();
    await copyRuntime.dispose();
    copyRoot.remove();
  }

  const demoFrame = document.createElement("iframe");
  demoFrame.style.cssText = "width:1000px;height:800px";
  const loaded = new Promise<void>((resolve) =>
    demoFrame.addEventListener("load", () => resolve(), { once: true })
  );
  demoFrame.src = "/demo";
  document.body.append(demoFrame);
  try {
    await guarded(loaded, "demo lifecycle frame did not load");
    const target = demoFrame.contentWindow, doc = demoFrame.contentDocument;
    assert(target && doc?.body, "demo lifecycle frame unavailable");
    await waitFor(
      () => !!doc.querySelector('[data-entry-id="entry-99999"] .mui-text'),
      "real demo did not initialize",
      doc.body,
    );
    const app = doc.querySelector(".demo-shell");
    assert(app, "demo application root missing");
    for (let i = 0; i < 2; i++) {
      target.dispatchEvent(new PageTransitionEvent("pagehide", { persisted: true }));
      target.dispatchEvent(new PageTransitionEvent("pageshow", { persisted: true }));
      assert(app.isConnected, "persisted pagehide destroyed the cached demo application");
      equal(
        doc.querySelector<HTMLTextAreaElement>("textarea")?.disabled,
        false,
        "bfcache restoration left the demo runtime closed",
      );
    }
    target.dispatchEvent(new PageTransitionEvent("pagehide", { persisted: false }));
    assert(!app.isConnected, "a persisted transition removed final demo disposal registration");
    passed.push(
      "demo survives repeated persisted page transitions and disposes on the final pagehide",
    );
  } finally {
    demoFrame.remove();
  }
  return passed;
}
