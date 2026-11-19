import { createApp, defineComponent, h, nextTick, ref, shallowRef } from "vue";
import {
  ConversationRuntime,
  type ConversationSubmission,
  ConversationView,
  type mountConversation,
  type mountConversationComposer,
  type mountConversationHistory,
} from "../mod.ts";
import { FixtureConversation } from "../fixtures/conversation.ts";

function assert(condition: unknown, mechanism: string): asserts condition {
  if (!condition) throw new Error(mechanism);
}
function equal(actual: unknown, expected: unknown, mechanism: string): void {
  assert(actual === expected, `${mechanism}: expected ${expected}, received ${actual}`);
}
async function guarded<T>(completion: Promise<T>, mechanism: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      completion,
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(mechanism)), 15_000);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
function geometryApplied(root: HTMLElement): boolean {
  if (root.querySelector(".mui-loading")) return false;
  const rows = [...root.querySelectorAll<HTMLElement>(".mui-row")];
  return rows.length > 0 && rows.every((row) => {
    const slot = row.parentElement;
    return slot &&
      Math.abs(slot.getBoundingClientRect().height - row.getBoundingClientRect().height) <= 1;
  });
}
function waitFor(predicate: () => boolean, mechanism: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      observer.disconnect();
      reject(new Error(mechanism));
    }, 15_000);
    const check = () => {
      if (predicate()) {
        clearTimeout(timer);
        observer.disconnect();
        resolve();
      }
    };
    const observer = new MutationObserver(check);
    observer.observe(document.body, {
      subtree: true,
      childList: true,
      attributes: true,
      characterData: true,
    });
    check();
  });
}

/** Signal-driven scenarios run against actual Vue, geometry and browser Selection. */
export async function runBrowserConformance(): Promise<string[]> {
  const passed: string[] = [];
  const fixture = new FixtureConversation("browser", 10_000);
  const runtime = new ConversationRuntime(fixture.sessionId, fixture, { entries: 240 });
  await runtime.loadPage();
  const release = fixture.subscribe((change) => runtime.apply(change));
  const root = document.createElement("div");
  root.style.cssText = "height:800px;display:grid;grid-template-columns:1fr 1fr;gap:16px";
  document.body.append(root);
  const showFirst = ref(true);
  const secondRuntime = shallowRef(runtime);
  let replacement: ConversationRuntime | undefined;
  const copied = Promise.withResolvers<string>();
  let submittedId: string | undefined;
  const submit = (submission: ConversationSubmission) => {
    submittedId = fixture.submit(submission.text);
    runtime.confirmSubmission(submission.viewId, submission.revision);
  };
  const app = createApp(defineComponent({
    setup() {
      return () => [
        showFirst.value
          ? h(ConversationView, {
            runtime,
            viewId: "first",
            host: {
              writeText(text: string) {
                copied.resolve(text);
                return Promise.resolve();
              },
            },
            onSubmit: submit,
          })
          : null,
        h(ConversationView, {
          runtime: secondRuntime.value,
          viewId: "second",
          host: { writeText: () => Promise.resolve() },
          onSubmit: submit,
        }),
      ];
    },
  }));
  app.mount(root);
  try {
    const first = (): HTMLElement => {
      const element = root.querySelector<HTMLElement>('[data-view-id="first"]');
      assert(element, "first conversation did not mount");
      return element;
    };
    await waitFor(
      () => !!first().querySelector('[data-entry-id="entry-99999"] .mui-text'),
      "latest content never became readable",
    );
    assert(runtime.projection.total === 100_000, "fixture did not expose complete history");
    assert(
      first().querySelectorAll(".mui-row").length <= 68,
      "virtualization mounted more than its hard budget",
    );
    assert(runtime.statistics.entries <= 240, "initial history materialized the complete index");
    assert(fixture.contentReads < 100, "initial history eagerly loaded offscreen output bodies");
    passed.push("100,000 entries mount a bounded tail and load bodies lazily");
    await waitFor(
      () => geometryApplied(first()),
      "variable-height output never acquired its natural row geometry",
    );
    passed.push(
      "measured slots contain variable-height output without overlapping adjacent blocks",
    );

    const editors = root.querySelectorAll<HTMLTextAreaElement>("textarea");
    assert(editors.length === 2, "two independent composers were not mounted");
    editors[0].value = "first draft";
    editors[0].dispatchEvent(new Event("input", { bubbles: true }));
    editors[1].value = "second draft";
    editors[1].dispatchEvent(new Event("input", { bubbles: true }));
    await nextTick();
    assert(
      editors[0].value === "first draft" && editors[1].value === "second draft",
      "drafts leaked between views",
    );
    passed.push("shared conversation runtime keeps independent view drafts");

    first().querySelector<HTMLFormElement>("form")?.dispatchEvent(
      new Event("submit", { bubbles: true, cancelable: true }),
    );
    assert(submittedId, "submission was not delivered to the explicit host");
    const id = submittedId;
    await waitFor(
      () => !!first().querySelector(`[data-entry-id="${id}"] .mui-text`),
      "new output did not follow the live end",
    );
    fixture.append(id, "\nstrict successor fragment");
    fixture.seal(id);
    await waitFor(
      () =>
        first().querySelector(`[data-entry-id="${id}"] .mui-text`)?.textContent?.includes(
          "strict successor fragment",
        ) === true,
      "streaming append was not applied to its original block",
    );
    equal(editors[0].value, "", "accepted submission did not clear its matching draft");
    assert(editors[1].value === "second draft", "acceptance cleared another view's draft");
    passed.push("accepted input and streaming updates retain block identity and draft ownership");

    const viewport = first().querySelector<HTMLElement>(".mui-viewport");
    assert(viewport, "history viewport missing");
    viewport.scrollTop = 50_000 * 120;
    viewport.dispatchEvent(new Event("scroll"));
    await waitFor(
      () =>
        [...first().querySelectorAll<HTMLElement>("[data-ordinal]")].some((node) =>
          Math.abs(Number(node.dataset.ordinal) - 50_000) < 60
        ) && !!first().querySelector(".mui-message .mui-text"),
      "scrollbar-only navigation did not request the unloaded middle range",
    );
    assert(
      runtime.statistics.entries <= 240,
      "reading another history range exceeded resident index budget",
    );
    assert(
      first().querySelectorAll(".mui-row").length <= 68,
      "history navigation exceeded mounted row budget",
    );
    assert(
      first().querySelector(".mui-latest"),
      "reading history unexpectedly resumed following live output",
    );
    passed.push("scrollbar-only navigation loads history without a wheel or keyboard event");

    const bodies = [...first().querySelectorAll<HTMLElement>(".mui-text")];
    assert(bodies.length >= 2, "history does not contain a selectable text range");
    const firstBody = bodies[0], lastBody = bodies[bodies.length - 1];
    const firstText = firstBody.firstChild, lastText = lastBody.firstChild;
    assert(firstText && lastText, "text projection has no text nodes");
    const selection = document.getSelection();
    assert(selection, "browser Selection unavailable");
    const range = document.createRange();
    range.setStart(firstText, 0);
    range.setEnd(lastText, lastText.textContent?.length ?? 0);
    selection.removeAllRanges();
    selection.addRange(range);
    document.dispatchEvent(new Event("selectionchange"));
    firstBody.dispatchEvent(new ClipboardEvent("copy", { bubbles: true, cancelable: true }));
    const result = await copied.promise;
    assert(
      result.startsWith(firstBody.textContent ?? "") && result.endsWith(lastBody.textContent ?? ""),
      "ordinary copy lost selected endpoints",
    );
    assert(
      !result.includes("Maieutics\n"),
      "copy used surrounding controls instead of the text projection",
    );
    selection.removeAllRanges();
    document.dispatchEvent(new Event("selectionchange"));
    passed.push(
      "ordinary cross-block copy uses revisioned text and excludes presentation controls",
    );

    const activity = first().querySelector<HTMLElement>(".mui-tool");
    if (activity) {
      const before = fixture.contentReads;
      activity.querySelector<HTMLButtonElement>("button")?.click();
      await waitFor(
        () => activity.textContent?.includes("工作区检索已完成") === true,
        "expanding a tool did not load its content",
      );
      assert(fixture.contentReads > before, "collapsed tool detail had already loaded eagerly");
      passed.push("tool detail loads only after expansion");
    } else throw new Error("fixture page did not expose a tool activity for lazy-load conformance");

    showFirst.value = false;
    await nextTick();
    assert(
      runtime.statistics.views === 1,
      "closing one view did not release exactly its own attachment",
    );
    assert(
      root.querySelector<HTMLTextAreaElement>("textarea")?.value === "second draft",
      "closing a sibling discarded the remaining draft",
    );
    passed.push("closing one view preserves its sibling attachment and draft");

    const newSource = new FixtureConversation("replacement", 3);
    replacement = new ConversationRuntime("replacement", newSource);
    await replacement.loadPage();
    secondRuntime.value = replacement;
    await waitFor(
      () => !!root.querySelector('[data-entry-id="entry-29"] .mui-text'),
      "session replacement did not mount the new addressed view",
    );
    equal(runtime.statistics.views, 0, "session replacement retained the previous view attachment");
    equal(replacement.statistics.views, 1, "replacement view attached to the wrong runtime");
    equal(
      root.querySelector<HTMLTextAreaElement>("textarea")?.value,
      "",
      "session replacement borrowed an old session draft",
    );
    passed.push("session replacement retires old view state before attaching the new runtime");
  } finally {
    app.unmount();
    release();
    await runtime.dispose();
    await replacement?.dispose();
    root.remove();
  }
  equal(
    runtime.statistics.entries + runtime.statistics.contentBytes + runtime.statistics.views,
    0,
    "terminal disposal retained conversation resources",
  );
  passed.push("terminal disposal releases source requests, cached content and view attachments");

  const artifact = await import(new URL("/conversation.js", location.href).href) as {
    mountConversation: typeof mountConversation;
    mountConversationHistory: typeof mountConversationHistory;
    mountConversationComposer: typeof mountConversationComposer;
  };
  const isolatedSource = new FixtureConversation("artifact", 2);
  const isolated = new ConversationRuntime("artifact", isolatedSource);
  await isolated.loadPage();
  const target = document.createElement("div");
  target.style.height = "600px";
  document.body.append(target);
  const handle = artifact.mountConversation(target, {
    runtime: isolated,
    viewId: "artifact",
    host: { writeText: () => Promise.resolve() },
  });
  try {
    await waitFor(
      () => !!target.querySelector('[data-entry-id="entry-19"] .mui-text'),
      "standalone ESM did not mount with its own Vue runtime",
    );
    equal(isolated.statistics.views, 1, "standalone mount lost its view lease");
    passed.push("self-contained ESM mounts without sharing Vue implementation objects");
    await isolated.dispose();
    await waitFor(
      () => target.querySelector<HTMLTextAreaElement>("textarea")?.disabled === true,
      "closing the runtime left a mounted view interactive",
    );
    passed.push("runtime closure disables a still-mounted view without leaving live effects");
  } finally {
    handle.unmount();
    handle.unmount();
    await isolated.dispose();
    target.remove();
  }
  const dragSource = new FixtureConversation("drag", 10_000);
  const entered = Promise.withResolvers<void>(),
    aborted = Promise.withResolvers<void>(),
    releasePage = Promise.withResolvers<void>();
  let delayed = false;
  const dragRuntime = new ConversationRuntime("drag", {
    async readPage(query, signal) {
      const snapshot = await dragSource.readPage(query, signal);
      if (
        !delayed && query.offset !== undefined && query.offset >= 20_000 && query.offset < 30_000
      ) {
        delayed = true;
        await new Promise<void>((resolve, reject) => {
          const cancel = () => {
            aborted.resolve();
            reject(signal.reason);
          };
          signal.addEventListener("abort", cancel, { once: true });
          releasePage.promise.then(() => {
            signal.removeEventListener("abort", cancel);
            resolve();
          });
          entered.resolve();
        });
      }
      return snapshot;
    },
    readContent: (id, revision, signal) => dragSource.readContent(id, revision, signal),
  }, { entries: 160 });
  await dragRuntime.loadPage();
  const dragRoot = document.createElement("div");
  dragRoot.style.height = "600px";
  document.body.append(dragRoot);
  const dragView = artifact.mountConversation(dragRoot, {
    runtime: dragRuntime,
    viewId: "drag",
    host: { writeText: () => Promise.resolve() },
  });
  try {
    await waitFor(
      () => !!dragRoot.querySelector('[data-entry-id="entry-99999"] .mui-text'),
      "drag fixture did not initialize at the live end",
    );
    const viewport = dragRoot.querySelector<HTMLElement>(".mui-viewport");
    assert(viewport, "drag viewport missing");
    viewport.scrollTop = 25_000 * 120;
    viewport.dispatchEvent(new Event("scroll"));
    await guarded(
      entered.promise,
      "first scrollbar target did not start its controlled page request",
    );
    viewport.scrollTop = 75_000 * 120;
    viewport.dispatchEvent(new Event("scroll"));
    await guarded(
      aborted.promise,
      "moving within the same mounted unloaded range did not cancel the obsolete request",
    );
    await waitFor(
      () =>
        [...dragRoot.querySelectorAll<HTMLElement>("[data-ordinal]")].some((node) =>
          Math.abs(Number(node.dataset.ordinal) - 75_000) < 80
        ) && !!dragRoot.querySelector(".mui-message .mui-text"),
      "latest scrollbar target did not become readable",
    );
    assert(
      dragRuntime.projection.get(25_000) === undefined,
      "cancelled scrollbar target published its stale page",
    );
    assert(
      dragRuntime.statistics.entries <= 160 && dragRoot.querySelectorAll(".mui-row").length <= 68,
      "scrollbar navigation exceeded its cache or mount budgets",
    );
    passed.push("rapid scrollbar jumps within one unloaded region cancel the obsolete target");
  } finally {
    releasePage.resolve();
    dragView.unmount();
    await dragRuntime.dispose();
    dragRoot.remove();
  }
  const heldSource = new FixtureConversation("held", 10_000);
  const pageEntered = Promise.withResolvers<void>(), pageRelease = Promise.withResolvers<void>();
  let holdPage = true;
  const heldRuntime = new ConversationRuntime("held", {
    async readPage(query, signal) {
      if (holdPage && query.offset !== undefined && query.offset < 99_000) {
        holdPage = false;
        pageEntered.resolve();
        await pageRelease.promise;
      }
      return heldSource.readPage(query, signal);
    },
    readContent: (id, revision, signal) => heldSource.readContent(id, revision, signal),
  }, { entries: 160 });
  await heldRuntime.loadPage();
  const stopHeld = heldSource.subscribe((change) => heldRuntime.apply(change));
  const heldRoot = document.createElement("div");
  heldRoot.style.height = "600px";
  document.body.append(heldRoot);
  const heldView = artifact.mountConversation(heldRoot, {
    runtime: heldRuntime,
    viewId: "held",
    host: { writeText: () => Promise.resolve() },
  });
  try {
    await waitFor(
      () =>
        !!heldRoot.querySelector('[data-entry-id="entry-99999"] .mui-text') &&
        geometryApplied(heldRoot),
      "held-scroll fixture did not acquire its initial row geometry",
    );
    const viewport = heldRoot.querySelector<HTMLElement>(".mui-viewport");
    assert(viewport, "held-scroll viewport missing");
    const bounds = viewport.getBoundingClientRect();
    const extent = viewport.scrollHeight;
    const tailOffset = viewport.scrollTop;
    let writes = 0;
    const scrollTo = viewport.scrollTo.bind(viewport);
    viewport.scrollTo = (options?: ScrollToOptions | number, y?: number) => {
      writes++;
      if (typeof options === "number") scrollTo(options, y ?? 0);
      else scrollTo(options);
    };
    viewport.dispatchEvent(
      new PointerEvent("pointerdown", {
        bubbles: true,
        pointerId: 17,
        pointerType: "mouse",
        button: 0,
        buttons: 1,
        clientX: bounds.right - 2,
        clientY: (bounds.top + bounds.bottom) / 2,
      }),
    );
    await nextTick();
    const firstOffset = 25_000 * 120 + 73;
    const liveId = heldSource.submit("dragged live end");
    heldSource.append(liveId, "\nstreaming height change".repeat(60));
    await waitFor(
      () =>
        heldRoot.querySelector(`[data-entry-id="${liveId}"] .mui-text`)?.textContent?.includes(
          "streaming height change",
        ) === true,
      "live output did not continue while the scrollbar was held",
    );
    assert(
      Math.abs(viewport.scrollTop - tailOffset) <= 1,
      "live output moved the held scrollbar away from its initial position",
    );
    equal(viewport.scrollHeight, extent, "live output changed the held scrollbar extent");
    equal(writes, 0, "live following wrote a scroll correction during a drag");
    viewport.scrollTop = firstOffset;
    viewport.dispatchEvent(new Event("scroll"));
    await guarded(pageEntered.promise, "held scrollbar did not request its first page");
    pageRelease.resolve();
    await waitFor(
      () =>
        !!heldRoot.querySelector('[data-entry-id="entry-25001"] .mui-text') &&
        !heldRoot.querySelector(".mui-loading"),
      "first held-scroll target did not finish its lazy body load",
    );
    assert(
      Math.abs(viewport.scrollTop - firstOffset) <= 1,
      "page/body completion moved the held scrollbar beyond native pixel rounding",
    );
    equal(viewport.scrollHeight, extent, "page/body completion changed the held scrollbar extent");
    equal(writes, 0, "virtualizer wrote a scroll correction while the scrollbar was held");

    const secondOffset = 75_000 * 120 + 41;
    viewport.scrollTop = secondOffset;
    viewport.dispatchEvent(new Event("scroll"));
    await waitFor(
      () =>
        !!heldRoot.querySelector('[data-entry-id="entry-75001"] .mui-text') &&
        !heldRoot.querySelector(".mui-loading"),
      "second held-scroll target did not finish its lazy body load",
    );
    assert(
      Math.abs(viewport.scrollTop - secondOffset) <= 1,
      "successive page replacement moved the held scrollbar beyond native pixel rounding",
    );
    equal(viewport.scrollHeight, extent, "cache eviction changed the held scrollbar extent");
    equal(writes, 0, "page replacement wrote a scroll correction during the same drag");
    equal(viewport.dataset.scrollGesture, "active", "native scrollend ended a still-held gesture");
    globalThis.dispatchEvent(new PointerEvent("pointerup", { pointerId: 17 }));
    await waitFor(
      () => viewport.dataset.scrollGesture === "idle" && geometryApplied(heldRoot),
      "releasing the scrollbar did not restore measured row heights",
    );
    assert(
      heldRoot.querySelector('[data-entry-id="entry-75000"]'),
      "release lost the logical block at the dragged position",
    );
    passed.push(
      "held scrollbar keeps position/extent through live output, lazy loads and page eviction",
    );
  } finally {
    pageRelease.resolve();
    globalThis.dispatchEvent(new PointerEvent("pointercancel", { pointerId: 17 }));
    heldView.unmount();
    stopHeld();
    await heldRuntime.dispose();
    heldRoot.remove();
  }
  const splitSource = new FixtureConversation("split", 10_000);
  const splitEntered = Promise.withResolvers<void>(),
    splitAborted = Promise.withResolvers<void>(),
    splitRelease = Promise.withResolvers<void>();
  let splitDelayed = false;
  const splitRuntime = new ConversationRuntime("split", {
    async readPage(query, signal) {
      if (!splitDelayed && query.offset !== undefined && query.offset < 99_000) {
        splitDelayed = true;
        await new Promise<void>((resolve, reject) => {
          const cancel = () => {
            splitAborted.resolve();
            reject(signal.reason);
          };
          signal.addEventListener("abort", cancel, { once: true });
          splitRelease.promise.then(() => {
            signal.removeEventListener("abort", cancel);
            resolve();
          });
          splitEntered.resolve();
        });
      }
      return splitSource.readPage(query, signal);
    },
    readContent: (id, revision, signal) => splitSource.readContent(id, revision, signal),
  }, { entries: 160 });
  await splitRuntime.loadPage();
  const sharedView = splitRuntime.attachView("split-view");
  const historyRoot = document.createElement("div"), composerRoot = document.createElement("div");
  historyRoot.style.height = "600px";
  document.body.append(historyRoot, composerRoot);
  let submittedDraft: ConversationSubmission | undefined;
  const receiveDraft = (value: ConversationSubmission) => submittedDraft = value;
  const host = { writeText: () => Promise.resolve() };
  let historyHandle = artifact.mountConversationHistory(historyRoot, { view: sharedView, host });
  let composerHandle = artifact.mountConversationComposer(composerRoot, {
    view: sharedView,
    onSubmit: receiveDraft,
  });
  let changedRuntime: ConversationRuntime | undefined;
  let stopChanged = () => {};
  try {
    await waitFor(
      () => !!historyRoot.querySelector('[data-entry-id="entry-99999"] .mui-text'),
      "independently mounted history did not load its visible content",
    );
    assert(!historyRoot.querySelector("textarea"), "standalone history includes an input editor");
    assert(
      !composerRoot.querySelector(".mui-viewport"),
      "standalone input includes a history viewport",
    );
    equal(
      splitRuntime.statistics.views,
      1,
      "separate components created duplicate view attachments",
    );
    const editor = composerRoot.querySelector<HTMLTextAreaElement>("textarea");
    assert(editor, "independently mounted input editor missing");
    editor.value = "separate submission";
    editor.dispatchEvent(new Event("input", { bubbles: true }));
    composerRoot.querySelector("form")?.dispatchEvent(
      new Event("submit", { bubbles: true, cancelable: true }),
    );
    assert(submittedDraft, "standalone input did not emit its submission");
    equal(submittedDraft.viewId, sharedView.id, "standalone input submitted the wrong view");
    equal(submittedDraft.text, "separate submission", "standalone input lost its draft snapshot");
    editor.value = "newer draft";
    editor.dispatchEvent(new Event("input", { bubbles: true }));
    splitRuntime.confirmSubmission(sharedView.id, submittedDraft.revision);
    await nextTick();
    equal(
      editor.value,
      "newer draft",
      "accepting an older separate submission cleared newer input",
    );
    passed.push("history and input mount in separate DOM roots under one caller-owned view");

    const viewport = historyRoot.querySelector<HTMLElement>(".mui-viewport");
    assert(viewport, "standalone history viewport missing");
    viewport.scrollTop = 25_000 * 120;
    viewport.dispatchEvent(new Event("scroll"));
    await guarded(splitEntered.promise, "standalone history did not request its controlled page");
    historyHandle.unmount();
    await guarded(splitAborted.promise, "history unmount did not cancel its owned page request");
    assert(!sharedView.signal.aborted, "history unmount closed the input's shared view");
    equal(splitRuntime.statistics.views, 1, "history unmount released the caller-owned view");
    equal(sharedView.pins.size, 0, "history unmount retained viewport/selection pins");
    equal(editor.value, "newer draft", "history unmount discarded the independent input draft");
    editor.value = "preserved draft";
    editor.dispatchEvent(new Event("input", { bubbles: true }));
    historyHandle = artifact.mountConversationHistory(historyRoot, { view: sharedView, host });
    await waitFor(
      () => !!historyRoot.querySelector('[data-entry-id="entry-99999"] .mui-text'),
      "standalone history could not remount beside the existing input",
    );
    composerHandle.unmount();
    equal(sharedView.draft, "preserved draft", "input unmount discarded the caller-owned draft");
    assert(!sharedView.signal.aborted, "input unmount closed the history's shared view");
    assert(historyRoot.querySelector(".mui-text"), "input unmount removed independent history");
    composerHandle = artifact.mountConversationComposer(composerRoot, {
      view: sharedView,
      onSubmit: receiveDraft,
    });
    equal(
      composerRoot.querySelector<HTMLTextAreaElement>("textarea")?.value,
      "preserved draft",
      "remounting standalone input did not restore the shared draft",
    );
    passed.push("independent unmount cancels history work while preserving the sibling and draft");

    const changedSource = new FixtureConversation("changed-split", 2);
    const nextRuntime = new ConversationRuntime("changed-split", changedSource);
    changedRuntime = nextRuntime;
    await nextRuntime.loadPage();
    stopChanged = changedSource.subscribe((change) => nextRuntime.apply(change));
    const changedView = nextRuntime.attachView("changed-view");
    historyHandle.update({ view: changedView, host });
    composerHandle.update({ view: changedView, onSubmit: receiveDraft });
    await waitFor(
      () => !!historyRoot.querySelector('[data-entry-id="entry-19"] .mui-text'),
      "standalone components did not rebind to the replacement view",
    );
    equal(
      composerRoot.querySelector<HTMLTextAreaElement>("textarea")?.value,
      "",
      "replacement input borrowed the previous view's draft",
    );
    equal(sharedView.pins.size, 0, "history replacement retained the previous view's pins");
    equal(
      sharedView.draft,
      "preserved draft",
      "replacing input destroyed the previous view's draft",
    );
    sharedView.dispose();
    equal(splitRuntime.statistics.views, 0, "caller disposal did not release the previous view");
    equal(
      changedRuntime.statistics.views,
      1,
      "replacement components lost their shared attachment",
    );
    changedView.dispose();
    changedSource.submit("update after view closure");
    // The runtime still accepts changes after a view closes; retired history must stay empty.
    await waitFor(
      () =>
        composerRoot.querySelector<HTMLTextAreaElement>("textarea")?.disabled === true &&
        historyRoot.querySelector(".mui-status")?.textContent === "会话已关闭" &&
        !historyRoot.querySelector("[data-ordinal]"),
      "closing the shared view left an independent component interactive or repopulated history",
    );
    passed.push("separate components rebind and close together without owning either runtime");
  } finally {
    splitRelease.resolve();
    historyHandle.unmount();
    composerHandle.unmount();
    sharedView.dispose();
    stopChanged();
    await splitRuntime.dispose();
    await changedRuntime?.dispose();
    historyRoot.remove();
    composerRoot.remove();
  }
  return passed;
}
