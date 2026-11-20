import { defineComponent, h, onBeforeUnmount, type PropType, shallowRef, watch } from "vue";
import type { ConversationRuntime } from "../core/runtime.ts";
import { ConversationHistory } from "./history.ts";
import { ConversationComposer } from "./composer.ts";
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

const ConversationSession = defineComponent({
  name: "ConversationSession",
  props: conversationProps,
  emits: {
    submit: (_submission: ConversationSubmission) => true,
    ready: () => true,
    anchorRestored: (_anchor: ConversationAnchorRestoration) => true,
  },
  setup(props, { emit }) {
    const view = props.runtime.attachView(props.viewId);
    onBeforeUnmount(() => view.dispose());
    return () =>
      h("section", { class: "mui-conversation", "data-view-id": props.viewId }, [
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
  },
});

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
    const generation = shallowRef(Symbol("conversation-view"));
    watch([() => props.runtime, () => props.viewId], () => {
      generation.value = Symbol("conversation-view");
    }, { flush: "sync" });
    return () =>
      h(ConversationSession, {
        ...props,
        key: generation.value,
        onSubmit: (submission: ConversationSubmission) => emit("submit", submission),
        onReady: () => emit("ready"),
        onAnchorRestored: (anchor: ConversationAnchorRestoration) => emit("anchorRestored", anchor),
      });
  },
});
