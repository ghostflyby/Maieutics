import messages from "../locales/en.json" with { type: "json" };
import { type ConversationLocalizer, formatConversationMessage } from "../localization.ts";
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
  title: { type: String, default: "" },
  localize: {
    type: Function as PropType<ConversationLocalizer>,
    default: formatConversationMessage,
  },
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
          "aria-label": props.title || props.localize(messages.conversation),
        }, [
          h(ConversationHistory, {
            view,
            localize: props.localize,
            host: props.host,
            title: props.title,
            onReady: () => emit("ready"),
            onAnchorRestored: (anchor: ConversationAnchorRestoration) =>
              emit("anchorRestored", anchor),
          }),
          h(ConversationComposer, {
            view,
            localize: props.localize,
            onSubmit: (submission: ConversationSubmission) => emit("submit", submission),
          }),
        ]);
    });
  },
});
