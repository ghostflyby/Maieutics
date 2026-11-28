import { formatOutputMessage } from "./outputClient.js";
import translations from "./notebookTranslations.js";

/** Notebook renderers receive the display language in transient output metadata. */
export function createNotebookLocalizer() {
  let catalog;
  return {
    select(metadata) {
      if (typeof metadata?.maieuticsLocale !== "string") return;
      const locale = metadata.maieuticsLocale.toLowerCase();
      catalog = Object.hasOwn(translations, locale) ? translations[locale] : undefined;
    },
    localize(message, ...args) {
      const text = catalog && Object.hasOwn(catalog, message) ? catalog[message] : message;
      return formatOutputMessage(text, ...args);
    },
  };
}
