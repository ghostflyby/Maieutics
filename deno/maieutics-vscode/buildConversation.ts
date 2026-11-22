/** Browser-only dependency graph; no Vue code enters the Node extension-host bundle. */
import { fileURLToPath } from "node:url";
const root = new URL("./", import.meta.url);
const result = await new Deno.Command(Deno.execPath(), {
  args: ["bundle", "--config", fileURLToPath(new URL("../maieutics-ui/deno.json", root)), "--platform", "browser", "--minify", "-o", fileURLToPath(new URL("media/conversation.js", root)), new URL("webview/conversation.ts", root).href],
  stdout: "inherit",
  stderr: "inherit",
}).output();
if (!result.success) throw new Error("Conversation browser bundle failed");
await Deno.copyFile(new URL("../maieutics-ui/styles.css", root), new URL("media/conversation.css", root));
await Deno.copyFile(new URL("webview/theme.css", root), new URL("media/conversationTheme.css", root));
