import { assert, assertEquals } from "@std/assert";
import { parseStyles, readStyles } from "./build_styles.ts";

Deno.test("shipped default-theme CSS is fresh, output-scoped, self-contained and within its budget", async () => {
  const css = await readStyles();
  assertEquals(await Deno.readTextFile(new URL("./styles.css", import.meta.url)), css);
  assert(css.length <= 48_000);
  const root = parseStyles(css);
  const declared = new Set<string>();
  const referenced = new Set<string>();
  root.walkRules((rule) => {
    for (const selector of rule.selectors) {
      assert(
        selector.includes(":where(.maieutics-output)"),
        `Styles escaped their output scope: ${selector}`,
      );
    }
  });
  root.walkDecls((decl) => {
    declared.add(decl.prop);
    for (const match of decl.value.matchAll(/var\((--vp-[\w-]+)/g)) referenced.add(match[1]);
  });
  for (const name of referenced) {
    assert(declared.has(name), `Missing upstream content variable: ${name}`);
  }
  assert(!css.includes("@font-face") && !css.includes("@import"));
});

Deno.test("vendored default-theme CSS matches the recorded upstream hashes", async () => {
  const directory = new URL("./vendor/vitepress/", import.meta.url);
  const manifest = JSON.parse(await Deno.readTextFile(new URL("upstream.json", directory))) as {
    files: { path: string; sha256: string }[];
  };
  const files = manifest.files.filter((file) => file.path.endsWith(".css"));
  assertEquals(files.length, 4);
  for (const file of files) {
    const data = await Deno.readFile(new URL(`styles/${file.path.split("/").at(-1)}`, directory));
    const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", data));
    assertEquals(
      Array.from(hash, (byte) => byte.toString(16).padStart(2, "0")).join(""),
      file.sha256,
    );
  }
});
