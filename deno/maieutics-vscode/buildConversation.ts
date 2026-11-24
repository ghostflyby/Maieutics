/** Browser-only dependency graph; no Vue code enters the Node extension-host bundle. */
import { fileURLToPath } from "node:url";
const root = new URL("./", import.meta.url);
const result = await new Deno.Command(Deno.execPath(), {
  args: ["bundle", "--platform", "browser", "--minify", "-o", fileURLToPath(new URL("media/conversation.js", root)), new URL("webview/conversation.ts", root).href],
  stdout: "inherit",
  stderr: "inherit",
}).output();
if (!result.success) throw new Error("Conversation browser bundle failed");
await Deno.copyFile(new URL("../maieutics-ui/styles.css", root), new URL("media/conversation.css", root));
await Deno.copyFile(new URL("webview/theme.css", root), new URL("media/conversationTheme.css", root));
await Deno.copyFile(new URL("webview/output.css", root), new URL("media/conversationOutput.css", root));

// Convert the owning modules' message catalogs to VS Code's source-string bundles.
const bundles = new Map<string, Map<string, string>>();
const placeholders = (message: string) => [...message.matchAll(/\{\d+\}/g)].map(([token]) => token).sort().join(" ");
for (const path of ["../maieutics-ui/locales/", "../maieutics-output-client/locales/", "locales/"]) {
  const directory = new URL(path, root);
  const source = await readCatalog(new URL("en.json", directory));
  const files = [];
  for await (const entry of Deno.readDir(directory)) {
    if (entry.isFile && entry.name.endsWith(".json")) files.push(entry.name);
  }
  for (const file of files.sort()) {
    const locale = Intl.getCanonicalLocales(file.slice(0, -5))[0].toLowerCase();
    const catalog = file === "en.json" ? source : await readCatalog(new URL(file, directory));
    if (Object.keys(source).sort().join() !== Object.keys(catalog).sort().join()) {
      throw new Error(`Localization keys differ in ${path}${file}`);
    }
    let bundle = bundles.get(locale);
    if (!bundle) {
      bundle = new Map();
      bundles.set(locale, bundle);
    }
    for (const [key, message] of Object.entries(source)) {
      const translation = catalog[key];
      if (placeholders(translation) !== placeholders(message)) {
        throw new Error(`Localization placeholders differ for ${key} in ${path}${file}`);
      }
      const previous = bundle.get(message);
      if (previous !== undefined && previous !== translation) {
        throw new Error(`Conflicting ${locale} translation for ${message}`);
      }
      bundle.set(message, translation);
    }
  }
}
await Deno.mkdir(new URL("l10n/", root), { recursive: true });
for (const [locale, bundle] of bundles) {
  const name = locale === "en" ? "bundle.l10n.json" : `bundle.l10n.${locale}.json`;
  await Deno.writeTextFile(new URL(`l10n/${name}`, root), JSON.stringify(Object.fromEntries(bundle), null, 2) + "\n");
}

async function readCatalog(url: URL): Promise<Record<string, string>> {
  const catalog: unknown = JSON.parse(await Deno.readTextFile(url));
  if (
    !catalog || typeof catalog !== "object" || Array.isArray(catalog) ||
    Object.values(catalog).some((value) => typeof value !== "string" || !value.trim())
  ) throw new Error(`Invalid localization catalog: ${url.pathname}`);
  return catalog as Record<string, string>;
}
