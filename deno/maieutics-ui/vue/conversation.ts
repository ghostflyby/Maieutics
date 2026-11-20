import { defineComponent, h, onScopeDispose, type PropType } from "vue";
import type { ConversationRuntime } from "../core/runtime.ts";
import { ConversationHistory } from "./history.ts";
import { ConversationComposer } from "./composer.ts";
import { useBinding } from "./binding.ts";
import type {
  ConversationAnchorRestoration,
  ConversationHost,
  ConversationSubmission,
} from "./contracts.ts";

const conversationProps = {
  runtime: { type: Object as PropType<ConversationRuntime>, required: true },
  viewId: { type: String, required: true },
  host: { type: Object as PropType<ConversationHost>, required: true },
  title: { type: String, default: "对话" },
} as const;

/** Compose history and input under one owned view attachment. */
export const ConversationView = defineComponent({
  name: "ConversationView",
  props: conversationProps,
  emits: {
    submit: (_submission: ConversationSubmission) => true,
    ready: () => true,
    anchorRestored: (_anchor: ConversationAnchorRestoration) => true,
  },
  setup(props, { emit }) {
    return useBinding(() => [props.runtime, props.viewId] as const, ([runtime, id]) => {
      const view = runtime.attachView(id);
      const key = Symbol("conversation");
      onScopeDispose(() => view.dispose());
      return () =>
        h("section", {
          key,
          class: "mui-conversation",
          "data-view-id": id,
          "aria-label": props.title,
        }, [
          h(ConversationHistory, {
            view,
            host: props.host,
            title: props.title,
            onReady: () => emit("ready"),
            onAnchorRestored: (anchor: ConversationAnchorRestoration) =>
              emit("anchorRestored", anchor),
          }),
          h(ConversationComposer, {
            view,
            onSubmit: (submission: ConversationSubmission) => emit("submit", submission),
          }),
        ]);
    });
  },
});
