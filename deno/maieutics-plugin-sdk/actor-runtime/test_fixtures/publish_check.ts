/** Assert the actual Deno publish plan, rather than duplicating its glob rules. */
const root = new URL("../../", import.meta.url);
const output = await new Deno.Command(Deno.execPath(), {
  args: ["publish", "--dry-run", "--allow-dirty", "--no-provenance"],
  cwd: root,
  stdout: "piped",
  stderr: "piped",
  env: { NO_COLOR: "1" },
}).output();
const diagnostics = new TextDecoder().decode(output.stdout) +
  new TextDecoder().decode(output.stderr);
if (!output.success) throw new Error(diagnostics);
const files = [...diagnostics.matchAll(/^\s+(file:\/\/\S+) \(/gm)].map((match) =>
  match[1].slice(root.href.length)
);
for (
  const required of [
    "actor-runtime/browser.ts",
    "actor-runtime/spawn_process.ts",
    "actor-runtime/LICENSE",
    "actor-runtime/NOTICE",
  ]
) {
  if (!files.includes(required)) {
    throw new Error(`Publish plan omitted ${required}\n${diagnostics}`);
  }
}
for (const file of files) {
  if (file.endsWith("_test.ts") || file.startsWith("actor-runtime/test_fixtures/")) {
    throw new Error(`Publish plan leaked a workspace-only file: ${file}`);
  }
}
console.log(
  `SDK publish dry-run verified ${files.length} files; no Actor fixtures or tests included.`,
);
