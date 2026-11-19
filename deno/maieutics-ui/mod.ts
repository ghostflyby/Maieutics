/** Standalone conversation component and framework-neutral runtime. Importing opens no I/O. */
export { ConversationView } from "./vue/conversation.ts";
export { ConversationHistory } from "./vue/history.ts";
export { ConversationComposer } from "./vue/composer.ts";
export type { ConversationHost, ConversationSubmission } from "./vue/contracts.ts";
export {
  mountConversation,
  mountConversationComposer,
  mountConversationHistory,
} from "./vue/mount.ts";
export type {
  ConversationBinding,
  ConversationComposerBinding,
  ConversationHistoryBinding,
} from "./vue/mount.ts";
export { ConversationRuntime, ConversationViewState } from "./core/runtime.ts";
export { ConversationError, defaultLimits } from "./core/types.ts";
export type {
  ConversationChange,
  ConversationEntry,
  ConversationLimits,
  ConversationOperation,
  ConversationPage,
  ConversationProjectionView,
  ConversationRow,
  ConversationSelection,
  ConversationSource,
  TextContent,
  TextPoint,
} from "./core/types.ts";
