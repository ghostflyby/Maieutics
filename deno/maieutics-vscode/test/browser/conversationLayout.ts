/// <reference lib="dom" />
/// <reference lib="es2024.promise" />
import { assert, assertEquals } from "@std/assert";
import {
  ConversationRuntime,
  mountConversationComposer,
  mountConversationHistory,
} from "../../../maieutics-ui/mod.ts";
import { FixtureConversation } from "../../../maieutics-ui/fixtures/conversation.ts";

function requireElement<T extends Element>(root: ParentNode, selector: string): T {
  const element = root.querySelector<T>(selector);
  assert(element, `Missing ${selector} in the conversation shell`);
  return element;
}

/** The iframe is a real viewport, including viewport units and responsive rules. */
export async function mountLayoutFixture(): Promise<void> {
  const source = new FixtureConversation("layout", 2);
  const runtime = new ConversationRuntime(source.sessionId, source);
  await runtime.loadPage();
  const view = runtime.attachView("layout");
  view.setDraft("A draft that keeps the send control available");
  const history = mountConversationHistory(requireElement(document, "#history"), {
    view,
    host: { writeText: () => Promise.resolve() },
  });
  const composer = mountConversationComposer(requireElement(document, "#composer"), { view });
  globalThis.addEventListener("pagehide", () => {
    history.unmount();
    composer.unmount();
    view.dispose();
    void runtime.dispose();
  }, { once: true });
}

async function frame(): Promise<void> {
  await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
}

function assertContained(outer: Element, element: Element, name: string): void {
  const viewport = outer.getBoundingClientRect();
  const box = element.getBoundingClientRect();
  assert(box.width > 0 && box.height > 0, `${name} is hidden`);
  assert(
    box.left >= viewport.left - 1 && box.right <= viewport.right + 1 &&
      box.top >= viewport.top - 1 && box.bottom <= viewport.bottom + 1,
    `${name} escapes its visible container`,
  );
}

async function assertOutputEdgeSpacing(): Promise<void> {
  const host = document.createElement("div");
  const root = host.attachShadow({ mode: "open" });
  // The Markdown surface attaches the host stylesheet after authored content.
  root.innerHTML = "<h2>Heading</h2><p>First paragraph</p><p>Last paragraph</p>";
  const sheet = document.createElement("link");
  sheet.rel = "stylesheet";
  sheet.href = "/output.css";
  const loaded = new Promise<void>((resolve, reject) => {
    sheet.onload = () => resolve();
    sheet.onerror = () => reject(new Error("Could not load the conversation output stylesheet"));
  });
  root.append(sheet);
  document.body.append(host);
  try {
    await loaded;
    assertEquals(getComputedStyle(requireElement(root, "h2")).marginBlockStart, "0px");
    assertEquals(getComputedStyle(requireElement(root, "p")).marginBlockEnd, "12px");
    assertEquals(
      getComputedStyle(requireElement(root, "p + p")).marginBlockEnd,
      "0px",
      "The final content paragraph must lose its trailing margin even with a stylesheet sibling",
    );
  } finally {
    host.remove();
  }
}

export async function runBrowserConformance(): Promise<string[]> {
  const iframe = document.createElement("iframe");
  iframe.style.cssText = "width:320px;height:520px;border:0;display:block";
  const owner = new AbortController();
  try {
    await new Promise<void>((resolve, reject) => {
      const deadline = setTimeout(
        () => reject(new Error("Conversation layout fixture did not mount")),
        5_000,
      );
      owner.signal.addEventListener("abort", () => clearTimeout(deadline), { once: true });
      globalThis.addEventListener("message", (event) => {
        if (event.source !== iframe.contentWindow || event.origin !== location.origin) return;
        if (event.data?.layoutReady === true) {
          clearTimeout(deadline);
          resolve();
        } else if (typeof event.data?.layoutError === "string") {
          reject(new Error(event.data.layoutError));
        }
      }, { signal: owner.signal });
      iframe.src = "/layout";
      document.body.append(iframe);
    });
    const doc = iframe.contentDocument;
    const viewport = iframe.contentWindow;
    assert(doc && viewport);
    const app = requireElement(doc, "#app");
    const editor = requireElement<HTMLTextAreaElement>(doc, ".mui-editor");
    const composer = requireElement(doc, "#composer");
    const send = requireElement<HTMLButtonElement>(doc, ".mui-submit");
    const history = requireElement(doc, ".mui-viewport");
    const toolbar = requireElement(doc, ".conversation-toolbar");
    for (const width of [320, 1400]) {
      iframe.style.width = `${width}px`;
      await frame();
      assert(viewport.innerWidth === width, "Fixture did not resize the actual browsing viewport");
      assertContained(app, composer, "Composer");
      assertContained(composer, editor, "Message editor");
      assertContained(composer, send, "Send button");
      const submit = send.getBoundingClientRect();
      assert(
        doc.elementFromPoint(submit.x + submit.width / 2, submit.y + submit.height / 2) === send &&
          !send.disabled,
        "The send button is clipped, covered, or unusable",
      );
      const editorColumn = composer.getBoundingClientRect();
      const controls = toolbar.getBoundingClientRect();
      assert(Math.abs(editorColumn.left - controls.left) < 1, "Toolbar and composer do not align");
      assert(editorColumn.width <= 900, "Wide view has an unbounded message column");
      const historyBox = history.getBoundingClientRect();
      assert(
        historyBox.height > 0 && historyBox.bottom <= editorColumn.top + 1,
        "History overlaps input",
      );
      assert(
        viewport.getComputedStyle(editor).resize === "none",
        "Editor exposes a manual resize grip",
      );
    }
    editor.focus();
    const form = requireElement(doc, ".mui-composer");
    assert(form.matches(":focus-within"), "Focusing input does not focus its containing form");
    assert(
      viewport.getComputedStyle(editor).outlineStyle === "none",
      "Input focus draws through the footer",
    );
    await assertOutputEdgeSpacing();
    return [
      "the conversation output stylesheet removes shadow content edge margins",
      "the conversation shell keeps actual editor and submit control visible at narrow and wide viewport sizes",
    ];
  } finally {
    owner.abort();
    iframe.remove();
  }
}
