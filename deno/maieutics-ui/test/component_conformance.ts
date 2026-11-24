import { type ConversationEntry, ConversationRuntime } from "../mod.ts";
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
  const translated: Record<string, string> = {
    "Conversation history": "[history label]",
    "Send a message…": "[editor placeholder]",
  };
  const localize = (message: string) => translated[message] ?? message;
  const panels = [0, 1].map((index) => {
    const submissions: string[] = [];
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
      submissions,
      history: artifact.mountConversationHistory(historyRoot, {
        view,
        host: { writeText: () => Promise.resolve() },
        localize: index === 0 ? localize : undefined,
      }),
      input: artifact.mountConversationComposer(inputRoot, {
        view,
        localize: index === 0 ? localize : undefined,
        onSubmit: (submission) => submissions.push(submission.text),
      }),
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
    equal(
      panels[0].historyRoot.querySelector(".mui-viewport")?.getAttribute("aria-label"),
      "[history label]",
      "the history ignored the host's accessible label",
    );
    equal(
      panels[0].inputRoot.querySelector("textarea")?.placeholder,
      "[editor placeholder]",
      "the independent composer ignored the host's placeholder",
    );
    equal(
      panels[1].inputRoot.querySelector("textarea")?.placeholder,
      "Send a message…",
      "the host's translation leaked to another mount",
    );
    passed.push("independently mounted components accept scoped host translations");
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
    const shortcutEditor = panels[0].inputRoot.querySelector<HTMLTextAreaElement>("textarea");
    assert(shortcutEditor, "shortcut fixture has no editor");
    shortcutEditor.value = "快捷键发送";
    shortcutEditor.dispatchEvent(new Event("input", { bubbles: true }));
    shortcutEditor.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    shortcutEditor.dispatchEvent(
      new KeyboardEvent("keydown", {
        key: "Enter",
        ctrlKey: true,
        isComposing: true,
        bubbles: true,
      }),
    );
    equal(panels[0].submissions.length, 0, "newline or IME composition submitted a message");
    for (const modifier of ["ctrlKey", "metaKey"]) {
      shortcutEditor.dispatchEvent(
        new KeyboardEvent("keydown", {
          key: "Enter",
          [modifier]: true,
          bubbles: true,
        }),
      );
    }
    equal(
      panels[0].submissions.join(","),
      "快捷键发送,快捷键发送",
      "send shortcuts lost the captured draft",
    );
    equal(
      panels[0].view.draft,
      "快捷键发送",
      "shortcut cleared a draft before host acknowledgement",
    );
    passed.push(
      "composer send shortcuts preserve newline, IME composition and host-owned draft acknowledgement",
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
  await checkToolGroups(artifact);
  passed.push("adjacent tool calls share a heading and keep independent disclosure across appends");
  return passed;
}

async function checkToolGroups(artifact: Artifact): Promise<void> {
  const entries: ConversationEntry[] = (["tool", "tool", "message", "tool", "tool"] as const)
    .map((kind, ordinal) => ({
      kind,
      ordinal,
      id: `group-entry-${ordinal}`,
      interactionId: ordinal < 4 ? "first-turn" : "second-turn",
      contentRevision: "0",
      preview: `Entry ${ordinal}`,
      state: "sealed",
    }));
  const runtime = new ConversationRuntime("tool-groups", {
    readPage: () =>
      Promise.resolve({
        sessionId: "tool-groups",
        epoch: "groups",
        revision: "0",
        total: entries.length,
        entries,
      }),
    readContent(id, revision) {
      const entry = entries.find((entry) => entry.id === id);
      return Promise.resolve({
        entryId: id,
        revision,
        text: entry?.preview ?? "",
        presentation: entry?.kind === "tool" ? { version: 1, kind: "tool" } : undefined,
      });
    },
  });
  await runtime.loadPage();
  const view = runtime.attachView("tool-groups");
  const root = document.createElement("div");
  root.style.height = "620px";
  document.body.append(root);
  const history = artifact.mountConversationHistory(root, {
    view,
    host: {
      writeText: () => Promise.resolve(),
      mountBlock(element) {
        const details = document.createElement("details");
        const summary = details.appendChild(document.createElement("summary"));
        const body = details.appendChild(document.createElement("p"));
        body.textContent = "Tool result";
        element.append(details);
        return {
          render(entry) {
            summary.textContent = entry.preview;
            return Promise.resolve();
          },
          dispose: () => details.remove(),
        };
      },
    },
  });
  const row = (ordinal: number) => root.querySelector<HTMLElement>(`[data-ordinal="${ordinal}"]`);
  const headingOrdinals = () =>
    [...root.querySelectorAll(".mui-tool h3")].map((heading) =>
      heading.closest("[data-ordinal]")?.getAttribute("data-ordinal")
    ).join(",");
  try {
    await waitFor(
      () => root.querySelectorAll(".mui-tool details").length === 4 && geometryApplied(root),
      "the tool group fixture did not render and measure its calls",
    );
    equal(
      headingOrdinals(),
      "0,3,4",
      "tool headings must follow non-tool and interaction boundaries",
    );
    const first = row(0)?.querySelector("details");
    const firstSummary = first?.querySelector("summary");
    const second = row(1)?.querySelector("details");
    const secondSummary = second?.querySelector("summary");
    assert(
      first && firstSummary && second && secondSummary,
      "each tool call needs its own disclosure",
    );
    const gap = secondSummary.getBoundingClientRect().top -
      firstSummary.getBoundingClientRect().bottom;
    assert(gap >= -1 && gap <= 12, "consecutive tools have excess spacing or overlap");
    secondSummary.click();
    assert(second.open && !first.open, "expanding a tool changed another tool's disclosure");

    const priorTail = row(4);
    const next: ConversationEntry = {
      ...entries[4],
      id: "group-entry-5",
      ordinal: 5,
      state: "open",
    };
    entries.push(next);
    runtime.apply({
      epoch: "groups",
      baseRevision: "0",
      revision: "1",
      total: entries.length,
      operations: [{ type: "upsert", entry: next }],
    });
    await waitFor(
      () => !!row(5)?.querySelector("details") && geometryApplied(root),
      "the appended tool did not settle into its group",
    );
    equal(headingOrdinals(), "0,3,4", "an appended tool duplicated its group's heading");
    assert(row(5)?.querySelector(".mui-writing"), "grouping hid the open call's status");
    equal(row(4), priorTail, "adding a tool remounted the preceding row");
    assert(second.isConnected && second.open, "adding a tool reset the earlier expanded call");
  } finally {
    history.unmount();
    view.dispose();
    await runtime.dispose();
    root.remove();
  }
}
