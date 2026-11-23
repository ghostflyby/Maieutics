import messages from "../locales/en.json" with { type: "json" };
import { type ConversationLocalizer, formatConversationMessage } from "../localization.ts";
import { defineComponent, h, onScopeDispose, type PropType, ref } from "vue";
import type { ConversationViewState } from "../core/runtime.ts";
import type { ConversationSubmission } from "./contracts.ts";
import { useBinding } from "./binding.ts";

const composerProps = {
  view: { type: Object as PropType<ConversationViewState>, required: true },
  localize: {
    type: Function as PropType<ConversationLocalizer>,
    default: formatConversationMessage,
  },
} as const;

/** Independently mountable input. Unmounting preserves the caller-owned view and its draft. */
export const ConversationComposer = defineComponent({
  name: "ConversationComposer",
  props: composerProps,
  emits: { submit: (_submission: ConversationSubmission) => true },
  setup(props, { emit }) {
    return useBinding(() => props.view, (view) => {
      const key = Symbol("composer");
      const shortcut = /Mac|iPhone|iPad/.test(navigator.platform) ? "⌘Enter" : "Ctrl+Enter";
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
          "aria-label": props.localize(messages.composer),
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
            placeholder: props.localize(messages.placeholder),
            "aria-label": props.localize(messages.messageInput),
            onKeydown: (event: KeyboardEvent) => {
              if (
                event.key === "Enter" && (event.metaKey || event.ctrlKey) &&
                !event.isComposing && !view.signal.aborted &&
                event.currentTarget instanceof HTMLTextAreaElement
              ) {
                event.preventDefault();
                event.currentTarget.form?.requestSubmit();
              }
            },
            onInput: (event: Event) => {
              if (!view.signal.aborted && event.target instanceof HTMLTextAreaElement) {
                view.setDraft(event.target.value);
              }
            },
          }),
          h(
            "small",
            { class: "mui-composer-hint" },
            props.localize(messages.sendShortcut, shortcut),
          ),
          h("button", {
            class: "mui-button mui-submit",
            type: "submit",
            disabled: closed.value || !draft.value.trim(),
          }, props.localize(messages.send)),
        ]);
    });
  },
});
