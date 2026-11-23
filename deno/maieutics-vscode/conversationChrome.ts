/** Host chrome shared by the webview document and its browser layout fixture. */
export function conversationChrome(labels: {
  conversation: string;
  connecting: string;
  actions: string;
  reload: string;
  stop: string;
}): string {
  return `<main id="app" aria-label="${
    escapeHtml(labels.conversation)
  }"><header class="conversation-toolbar"><span id="status" role="status">${
    escapeHtml(labels.connecting)
  }</span><div class="conversation-actions" role="group" aria-label="${
    escapeHtml(labels.actions)
  }"><button id="reload" type="button">${
    escapeHtml(labels.reload)
  }</button><button id="cancel" type="button" hidden disabled>${
    escapeHtml(labels.stop)
  }</button></div></header><div id="history"></div><div id="composer"></div><p id="notice" role="alert"></p></main>`;
}

/** Escape text in HTML and quoted attributes. */
export function escapeHtml(value: string): string {
  return value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");
}
