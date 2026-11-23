import { type ConversationLocalizer, formatConversationMessage } from "../../maieutics-ui/mod.ts";

const element = document.getElementById("localization");
if (!element) throw new Error("missing_localization_bundle");
const translations = JSON.parse(element.textContent ?? "{}") as Record<string, string>;
/** The extension supplies its VS Code localization bundle, with English fallback per message. */
export const localize: ConversationLocalizer = (message, ...args) => formatConversationMessage(translations[message] ?? message, ...args);
