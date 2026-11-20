import { defineComponent, h, onScopeDispose, type PropType, ref } from "vue";
import type { ConversationViewState } from "../core/runtime.ts";
import type { ConversationSubmission } from "./contracts.ts";
import { useBinding } from "./binding.ts";

const composerProps = {
  view: { type: Object as PropType<ConversationViewState>, required: true },
} as const;

/** Independently mountable input. Unmounting preserves the caller-owned view and its draft. */
export const ConversationComposer = defineComponent({
  name: "ConversationComposer",
  props: composerProps,
  emits: { submit: (_submission: ConversationSubmission) => true },
  setup(props, { emit }) {
    return useBinding(() => props.view, (view) => {
      const key = Symbol("composer");
      const draft = ref(view.draft);
      const closed = ref(view.signal.aborted);
      const stop = closed.value ? () => {} : view.subscribe(() => draft.value = view.draft);
      const close = () => {
        closed.value = true;
        draft.value = "";
      };
      view.signal.addEventListener("abort", close, { once: true });
      onScopeDispose(() => {
        view.signal.removeEventListener("abort", close);
        stop();
      });
      return () =>
        h("form", {
          key,
          class: "mui-composer",
          "data-composer-view-id": view.id,
          "aria-label": "当前输入",
          onSubmit: (event: SubmitEvent) => {
            event.preventDefault();
            if (view.signal.aborted) return;
            const submission = view.captureSubmission();
            if (submission.text.trim()) emit("submit", submission);
          },
        }, [
          h("textarea", {
            class: "mui-editor",
            value: draft.value,
            disabled: closed.value,
            maxlength: 64_000,
            placeholder: "写下你的问题…",
            "aria-label": "当前输入",
            onInput: (event: Event) => {
              if (!view.signal.aborted && event.target instanceof HTMLTextAreaElement) {
                view.setDraft(event.target.value);
              }
            },
          }),
          h("small", { class: "mui-composer-hint" }, "Enter 换行"),
          h("button", {
            class: "mui-button mui-submit",
            type: "submit",
            disabled: closed.value || !draft.value.trim(),
          }, "发送 ↑"),
        ]);
    });
  },
});
