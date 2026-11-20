import { createApp, defineComponent, h, ref } from "vue";
import { ConversationRuntime, type ConversationSubmission, ConversationView } from "../mod.ts";
import { FixtureConversation } from "../fixtures/conversation.ts";

const fixture = new FixtureConversation();
const runtime = new ConversationRuntime(fixture.sessionId, fixture);
await runtime.loadPage();
const stop = fixture.subscribe((change) => runtime.apply(change));
const host = { writeText: (text: string) => navigator.clipboard.writeText(text) };
let lastReply: string | undefined;
const App = defineComponent({
  setup() {
    const split = ref(false);
    const submitted = ref(false);
    const submit = (submission: ConversationSubmission) => {
      lastReply = fixture.submit(submission.text);
      runtime.confirmSubmission(submission.viewId, submission.revision);
      submitted.value = true;
    };
    return () =>
      h("div", { class: "demo-shell" }, [
        h("aside", { class: "demo-sidebar" }, [
          h("a", { class: "demo-brand", href: "#" }, "maieutics"),
          h("div", { class: "demo-workspace" }, [
            h("span", "WORKSPACE"),
            h("strong", "探索与思考"),
          ]),
          h("nav", [
            h("span", { class: "demo-nav-active" }, "◌  对话"),
            h("span", "▤  笔记与材料"),
          ]),
          h("div", { class: "demo-sidebar-bottom" }, [
            h("span", { class: "demo-dot" }),
            "本地界面原型",
          ]),
        ]),
        h("main", { class: "demo-main" }, [
          h("header", { class: "demo-topbar" }, [
            h("span", "工作区 / 研究笔记"),
            h("button", {
              onClick: () => {
                split.value = !split.value;
              },
            }, split.value ? "单栏查看" : "并排查看"),
          ]),
          h("div", { class: "demo-intro" }, [
            h("span", { class: "demo-eyebrow" }, "A SPACE TO THINK"),
            h("h1", "让问题，继续展开。"),
            h("p", "10,000 轮示例对话 · 历史和内容按需载入。"),
          ]),
          h("div", { class: ["demo-conversations", split.value ? "demo-split" : ""] }, [
            h(ConversationView, {
              key: "primary",
              runtime,
              host,
              viewId: "primary",
              title: "研究笔记",
              onSubmit: submit,
            }),
            split.value
              ? h(ConversationView, {
                key: "secondary",
                runtime,
                host,
                viewId: "secondary",
                title: "独立阅读视图",
                onSubmit: submit,
              })
              : null,
          ]),
          h("footer", { class: "demo-footer" }, [
            h("span", "示例数据，不会调用模型或执行代码。"),
            submitted.value
              ? h("div", [
                h("button", {
                  onClick: () => {
                    if (lastReply) {
                      fixture.append(
                        lastReply,
                        "\n\n新的片段已经到达。这个输出块继续更新，已有块的身份与顺序保持稳定。",
                      );
                    }
                  },
                }, "追加输出片段"),
                h("button", {
                  onClick: () => {
                    if (lastReply) {
                      fixture.seal(lastReply);
                      lastReply = undefined;
                      submitted.value = false;
                    }
                  },
                }, "完成输出"),
              ])
              : null,
          ]),
        ]),
      ]);
  },
});
const root = document.getElementById("app");
if (!root) throw new Error("Missing application root");
const app = createApp(App);
app.mount(root);
const pageHidden = (event: PageTransitionEvent) => {
  if (event.persisted) return;
  globalThis.removeEventListener("pagehide", pageHidden);
  app.unmount();
  stop();
  runtime.dispose().catch((error) => console.error("Conversation shutdown failed", error));
};
globalThis.addEventListener("pagehide", pageHidden);
