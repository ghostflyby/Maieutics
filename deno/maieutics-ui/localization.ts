/** Host translation service. Message sources live in locales/en.json. */
export type ConversationLocalizer = (
  message: string,
  ...args: readonly (string | number)[]
) => string;

/** English fallback and positional formatting; locale and number formatting belong to the host. */
export const formatConversationMessage: ConversationLocalizer = (message, ...args) =>
  message.replace(
    /\{(\d+)\}/g,
    (placeholder, index) =>
      args[Number(index)] === undefined ? placeholder : String(args[Number(index)]),
  );

/** Resolve stable error codes even when a separately bundled runtime raised the error. */
export function historyErrorMessageKey(error: unknown) {
  const code = typeof error === "object" && error !== null && "code" in error
    ? error.code
    : undefined;
  switch (code) {
    case "selection_capacity":
      return "selectionCapacity";
    case "index_capacity":
      return "indexCapacity";
    case "range_capacity":
      return "rangeCapacity";
    case "stream_wait_timeout":
      return "streamTimeout";
    default:
      return "readFailure";
  }
}
