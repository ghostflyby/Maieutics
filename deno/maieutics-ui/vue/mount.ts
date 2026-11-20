import { createApp, h, shallowRef, type VNode } from "vue";
import { ConversationError } from "../core/types.ts";
import type { ConversationRuntime, ConversationViewState } from "../core/runtime.ts";
import { ConversationView } from "./conversation.ts";
import { ConversationHistory } from "./history.ts";
import { ConversationComposer } from "./composer.ts";
import type {
  ConversationAnchorRestoration,
  ConversationHost,
  ConversationSubmission,
} from "./contracts.ts";

/** Plain host binding for the self-contained ESM; no Vue objects cross the boundary. */
export interface ConversationBinding {
  readonly runtime: ConversationRuntime;
  readonly viewId: string;
  readonly host: ConversationHost;
  readonly title?: string;
  readonly onSubmit?: (submission: ConversationSubmission) => void;
  readonly onReady?: () => void;
  readonly onAnchorRestored?: (anchor: ConversationAnchorRestoration) => void;
}

/** History accepts an existing view attachment owned by the caller. */
export interface ConversationHistoryBinding {
  readonly view: ConversationViewState;
  readonly host: ConversationHost;
  readonly title?: string;
  readonly onReady?: () => void;
  readonly onAnchorRestored?: (anchor: ConversationAnchorRestoration) => void;
}

/** Input needs only draft/submission state, independently of history or clipboard capabilities. */
export interface ConversationComposerBinding {
  readonly view: ConversationViewState;
  readonly onSubmit?: (submission: ConversationSubmission) => void;
}

/** Mount with the bundle's own Vue runtime; the caller still owns the conversation runtime. */
export function mountConversation(element: HTMLElement, initial: ConversationBinding): {
  update(binding: ConversationBinding): void;
  unmount(): void;
} {
  return mountBinding(element, initial, (binding) => h(ConversationView, binding));
}

/** Mount history separately; unmount releases history work, never the supplied view attachment. */
export function mountConversationHistory(
  element: HTMLElement,
  initial: ConversationHistoryBinding,
): {
  update(binding: ConversationHistoryBinding): void;
  unmount(): void;
} {
  return mountBinding(element, initial, (binding) => h(ConversationHistory, binding));
}

/** Mount input separately; the caller retains its view and draft across unmount/remount. */
export function mountConversationComposer(
  element: HTMLElement,
  initial: ConversationComposerBinding,
): {
  update(binding: ConversationComposerBinding): void;
  unmount(): void;
} {
  return mountBinding(element, initial, (binding) => h(ConversationComposer, binding));
}

function mountBinding<Binding>(
  element: HTMLElement,
  initial: Binding,
  render: (binding: Binding) => VNode,
): {
  update(binding: Binding): void;
  unmount(): void;
} {
  const binding = shallowRef<Binding>(initial);
  let closed = false;
  const app = createApp({ render: () => render(binding.value) });
  app.mount(element);
  return {
    update(next) {
      if (closed) throw new ConversationError("view_closed");
      binding.value = next;
    },
    unmount() {
      if (!closed) {
        closed = true;
        app.unmount();
      }
    },
  };
}
