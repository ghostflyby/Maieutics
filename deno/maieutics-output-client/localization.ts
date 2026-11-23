import type { OutputLocalizer } from "./types.d.ts";
/** English fallback. Hosts supply translation of message sources from locales/en.json. */
export const formatOutputMessage: OutputLocalizer = (message, ...args) =>
  message.replace(
    /\{(\d+)\}/g,
    (placeholder, index) =>
      args[Number(index)] === undefined ? placeholder : String(args[Number(index)]),
  );
