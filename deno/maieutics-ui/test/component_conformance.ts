import { ConversationRuntime } from "../mod.ts";
import { FixtureConversation } from "../fixtures/conversation.ts";
import { assert, equal, geometryApplied, waitFor } from "./browser_assertions.ts";

type Artifact = Pick<
  typeof import("../mod.ts"),
  "mountConversation" | "mountConversationHistory" | "mountConversationComposer"
>;

/** Exercise the public DOM/CSS contract through the separately bundled mounts. */
export async function runComponentConformance(artifact: Artifact): Promise<string[]> {
  const passed: string[] = [];
  const source = new FixtureConversation("themes", 10);
  const runtime = new ConversationRuntime(source.sessionId, source);
  await runtime.loadPage();
  const themeRoot = document.createElement("div");
  themeRoot.className = "test-mui-theme";
  themeRoot.style.cssText = "display:grid;grid-template-columns:1fr 1fr;gap:16px";
  const outside = document.createElement("button");
  outside.textContent = "Host control";
  const isolated = document.createElement("div");
  const reference = document.createElement("button");
  reference.textContent = "Host control";
  isolated.attachShadow({ mode: "open" }).append(reference);
  document.body.append(outside, isolated, themeRoot);
  const sheet = document.createElement("style");
  sheet.textContent = `
    .test-mui-theme[data-theme="night"] {
      --mui-text: rgb(231, 233, 241);
      --mui-surface: rgb(21, 25, 34);
      --mui-editor-surface: rgb(32, 36, 48);
      --mui-accent: rgb(150, 185, 238);
      --mui-accent-text: rgb(20, 30, 45);
      --mui-font-family: monospace;
      --mui-body-font-size: 17px;
      --mui-body-line-height: 1.6;
      --mui-block-inset: 14px;
    }
    .test-mui-theme[data-theme="night"] .mui-submit { border-radius: 0; }
  `;
  document.head.append(sheet);
  const panels = [0, 1].map((index) => {
    const view = runtime.attachView(`theme-${index}`);
    const panel = document.createElement("div");
    const historyRoot = document.createElement("div");
    historyRoot.style.height = "500px";
    const inputRoot = document.createElement("div");
    panel.append(historyRoot, inputRoot);
    themeRoot.append(panel);
    return {
      view,
      historyRoot,
      inputRoot,
      history: artifact.mountConversationHistory(historyRoot, {
        view,
        host: { writeText: () => Promise.resolve() },
        title: `Theme ${index}`,
      }),
      input: artifact.mountConversationComposer(inputRoot, { view }),
    };
  });
  try {
    await waitFor(
      () =>
        panels.every(({ historyRoot }) =>
          !!historyRoot.querySelector('[data-entry-id="entry-99"] .mui-text') &&
          geometryApplied(historyRoot)
        ),
      "themed component fixtures did not finish natural row measurement",
    );
    for (
      const property of [
        "font",
        "padding",
        "borderRadius",
        "boxSizing",
        "backgroundColor",
        "color",
      ] as const
    ) {
      equal(
        getComputedStyle(outside)[property],
        getComputedStyle(reference)[property],
        `component CSS leaked ${property} into an unrelated host button`,
      );
    }
    passed.push("component CSS leaves unrelated host controls unchanged");
    for (const { historyRoot, inputRoot } of panels) {
      const list = historyRoot.querySelector("ol.mui-history-space");
      assert(list, "history has no semantic ordered list");
      const viewport = historyRoot.querySelector<HTMLElement>(".mui-viewport");
      assert(viewport?.tabIndex === 0, "history scroll region is not keyboard focusable");
      equal(viewport.getAttribute("role"), "region", "history has no named scroll region");
      equal(viewport.getAttribute("aria-live"), "off", "history replay became a live announcement");
      for (const slot of list.querySelectorAll<HTMLElement>(":scope > .mui-slot")) {
        equal(slot.tagName, "LI", "history entry lost list-item semantics");
        const article = slot.firstElementChild;
        assert(
          article && article.matches("article.mui-row.mui-block"),
          "entry has redundant row/article wrappers",
        );
        equal(slot.children.length, 1, "virtual slot has extra presentation wrappers");
        assert(article.querySelector("header > h3"), "message has no semantic heading");
        equal(
          Number(slot.getAttribute("aria-posinset")),
          Number(article.getAttribute("data-ordinal")) + 1,
          "virtual item accessibility position does not match its logical ordinal",
        );
        equal(
          Number(slot.getAttribute("aria-setsize")),
          runtime.projection.total,
          "virtual item accessibility count describes only mounted rows",
        );
      }
      const form = inputRoot.querySelector("form.mui-composer");
      assert(
        form && form.querySelector(":scope > textarea.mui-editor"),
        "input editor has an unnecessary wrapper",
      );
      assert(
        form.querySelector(':scope > button.mui-submit[type="submit"]'),
        "submit control has an unnecessary wrapper",
      );
      assert(!inputRoot.querySelector("style"), "input injected its own stylesheet");
      assert(!historyRoot.querySelector("style"), "history injected its own stylesheet");
      for (const element of historyRoot.querySelectorAll<HTMLElement>("[style]")) {
        for (const property of element.style) {
          assert(
            property.startsWith("--mui-virtual-"),
            `component embedded non-geometric inline style: ${property}`,
          );
        }
      }
    }
    passed.push(
      "semantic history entries and composer controls use a shallow DOM with only dynamic geometry inline",
    );
    const editors = panels.map(({ inputRoot }) => {
      const editor = inputRoot.querySelector<HTMLTextAreaElement>(".mui-editor");
      assert(editor, "theme fixture editor missing");
      editor.value = "主题切换期间继续编辑";
      editor.dispatchEvent(new Event("input", { bubbles: true }));
      return editor;
    });
    const editor = editors[0];
    editor.focus();
    editor.setSelectionRange(2, 4);
    const tail = panels[0].historyRoot.querySelector('[data-entry-id="entry-99"]');
    themeRoot.dataset.theme = "night";
    await waitFor(
      () =>
        panels.every(({ historyRoot, inputRoot }) => {
          const history = historyRoot.querySelector(".mui-history");
          const composer = inputRoot.querySelector(".mui-composer");
          return history && composer &&
            getComputedStyle(history).backgroundColor === "rgb(21, 25, 34)" &&
            getComputedStyle(composer).backgroundColor === "rgb(32, 36, 48)" &&
            geometryApplied(historyRoot);
        }),
      "one external theme did not update all history/editor roots and their measured geometry",
    );
    for (const [index, { historyRoot, inputRoot, view }] of panels.entries()) {
      const text = historyRoot.querySelector(".mui-text");
      const submit = inputRoot.querySelector(".mui-submit");
      assert(text && submit, "themed content or submit control missing");
      equal(
        getComputedStyle(text).fontSize,
        "17px",
        "external typography token did not reach message text",
      );
      equal(
        getComputedStyle(submit).backgroundColor,
        "rgb(150, 185, 238)",
        "external accent did not reach the editor",
      );
      equal(
        getComputedStyle(submit).borderRadius,
        "0px",
        "ordinary host CSS could not override the component layer",
      );
      assert(
        inputRoot.querySelector(".mui-editor") === editors[index],
        "theme change remounted the input editor",
      );
      equal(view.draft, "主题切换期间继续编辑", "theme change discarded the caller-owned draft");
    }
    equal(
      panels[0].historyRoot.querySelector('[data-entry-id="entry-99"]'),
      tail,
      "theme change remounted the visible output block",
    );
    assert(document.activeElement === editor, "theme change stole editor focus");
    equal(editor.selectionStart, 2, "theme change moved the editor selection start");
    equal(editor.selectionEnd, 4, "theme change moved the editor selection end");
    sheet.remove();
    await waitFor(
      () =>
        panels.every(({ historyRoot }) => {
          const text = historyRoot.querySelector(".mui-text");
          return text && getComputedStyle(text).fontSize === "13px" && geometryApplied(historyRoot);
        }),
      "removing the one injected theme did not restore default typography and row geometry",
    );
    passed.push(
      "one external stylesheet themes separate views and editors without remounting or losing input",
    );
    const replacementSource = new FixtureConversation("binding-replacement", 10);
    const replacement = new ConversationRuntime(replacementSource.sessionId, replacementSource);
    await replacement.loadPage();
    const reserved = replacement.attachView("old-id");
    reserved.setDraft("reserved sibling draft");
    const combinedRoot = document.createElement("div");
    combinedRoot.style.height = "500px";
    themeRoot.append(combinedRoot);
    let ready = 0;
    const common = {
      host: { writeText: () => Promise.resolve() },
      onReady: () => ready++,
    };
    const combined = artifact.mountConversation(combinedRoot, {
      ...common,
      runtime,
      viewId: "old-id",
    });
    try {
      await waitFor(() => ready === 1, "initial combined view did not initialize");
      combined.update({ ...common, runtime: replacement, viewId: "new-id" });
      await waitFor(
        () => ready === 2 && !!combinedRoot.querySelector('[data-view-id="new-id"]'),
        "runtime/viewId replacement created an intermediate conflicting attachment",
      );
      equal(runtime.statistics.views, 2, "combined replacement retained its previous view");
      equal(
        replacement.statistics.views,
        2,
        "combined replacement did not attach exactly its final view",
      );
      assert(!reserved.signal.aborted, "combined replacement closed a destination sibling");
      equal(
        reserved.draft,
        "reserved sibling draft",
        "combined replacement borrowed a destination sibling's state",
      );
      passed.push(
        "flattened components bind a complete runtime/viewId pair without an intermediate attachment",
      );
    } finally {
      combined.unmount();
      reserved.dispose();
      await replacement.dispose();
      combinedRoot.remove();
    }
  } finally {
    for (const { history, input, view } of panels) {
      input.unmount();
      history.unmount();
      view.dispose();
    }
    await runtime.dispose();
    sheet.remove();
    themeRoot.remove();
    outside.remove();
    isolated.remove();
  }
  const streamingSource = new FixtureConversation("surfaces", 100);
  let failContent = false;
  const streaming = new ConversationRuntime(streamingSource.sessionId, {
    readPage: streamingSource.readPage.bind(streamingSource),
    async readContent(id, revision, signal) {
      if (failContent) throw new Error("injected content failure");
      return {
        ...await streamingSource.readContent(id, revision, signal),
        presentation: { version: 1, kind: "markdown" },
      };
    },
  });
  const stop = streamingSource.subscribe((change) =>
    streaming.apply({
      ...change,
      operations: change.operations.map((operation) => {
        if (operation.type !== "append") return operation;
        const previous = streaming.projection.find(operation.id);
        assert(previous, "stream fixture lost its message");
        return {
          type: "upsert" as const,
          entry: { ...previous, contentRevision: operation.revision },
        };
      }),
    })
  );
  await streaming.loadPage();
  const attachment = streaming.attachView("surface-view");
  attachment.setDraft("keep draft");
  const container = document.createElement("div");
  container.style.height = "500px";
  document.body.append(container);
  const counts = new Map<
    string,
    { mounts: number; draws: number; disposed: number; lastSignal?: AbortSignal }
  >();
  const mounted = artifact.mountConversationHistory(container, {
    view: attachment,
    host: {
      writeText: () => Promise.resolve(),
      mountBlock(element) {
        let id: string | undefined;
        return {
          render(entry, content, signal) {
            if (!id) {
              id = entry.id;
              const count = counts.get(id) ?? { mounts: 0, draws: 0, disposed: 0 };
              count.mounts++;
              counts.set(id, count);
            }
            const count = counts.get(entry.id);
            assert(count, "surface lost its owning entry");
            count.draws++;
            count.lastSignal = signal;
            element.textContent = content.text;
            return Promise.resolve();
          },
          dispose() {
            if (id) {
              const count = counts.get(id);
              if (count) count.disposed++;
            }
          },
        };
      },
    },
  });
  try {
    const id = streamingSource.submit("stream into an existing surface");
    await waitFor(() => (counts.get(id)?.draws ?? 0) === 1, "new rich row did not mount");
    const first = container.querySelector(`[data-entry-id="${id}"] .mui-rendered`);
    const initialSignal = counts.get(id)?.lastSignal;
    streamingSource.append(id, "\nsecond revision");
    await waitFor(
      () => (counts.get(id)?.draws ?? 0) >= 2,
      "stream append lost its presentation descriptor",
    );
    equal(counts.get(id)?.mounts, 1, "a streamed revision remounted its existing surface");
    equal(
      container.querySelector(`[data-entry-id="${id}"] .mui-rendered`),
      first,
      "streaming replaced the surface root",
    );
    assert(initialSignal?.aborted, "a new surface revision did not cancel stale preparation");
    failContent = true;
    streamingSource.append(id, "\nretry revision");
    await waitFor(
      () => !!container.querySelector(`[data-entry-id="${id}"] .mui-error`),
      "failed rich revision hid its retry behind the previous surface",
    );
    equal(
      container.querySelector(`[data-entry-id="${id}"] .mui-rendered`),
      first,
      "a transient read failure discarded the last rendered surface",
    );
    failContent = false;
    const retry = container.querySelector<HTMLButtonElement>(`[data-entry-id="${id}"] .mui-retry`);
    assert(retry, "failed rich revision has no recovery action");
    retry.click();
    await waitFor(
      () =>
        (counts.get(id)?.draws ?? 0) >= 3 &&
        !container.querySelector(`[data-entry-id="${id}"] .mui-error`),
      "retry did not recover the current rich revision",
    );
    const viewport = container.querySelector<HTMLElement>(".mui-viewport");
    assert(viewport, "surface fixture has no viewport");
    viewport.scrollTop = 0;
    viewport.dispatchEvent(new Event("scroll"));
    await waitFor(
      () => (counts.get(id)?.disposed ?? 0) === 1,
      "virtual row recycling did not dispose its renderer",
    );
    assert(counts.get(id)?.lastSignal?.aborted, "recycled surface retained pending rendering work");
    equal(attachment.draft, "keep draft", "history recycling discarded independent input state");
    passed.push(
      "streamed blocks retain their host surface and virtual recycling disposes its exact rendering scope",
    );
  } finally {
    mounted.unmount();
    attachment.dispose();
    stop();
    await streaming.dispose();
    container.remove();
  }
  return passed;
}
