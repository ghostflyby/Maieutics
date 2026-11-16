/** Fail closed if Deno's publish-plan format changes or includes workspace-only files. */
export function assertPublishPlan(diagnostics: string, root: URL): string[] {
  const files = [...diagnostics.matchAll(/^\s+(file:\/\/\S+) \([^\r\n]*\)\s*$/gm)].map((match) => {
    if (!match[1].startsWith(root.href)) {
      throw new Error(`Publish plan contains a foreign file: ${match[1]}`);
    }
    return match[1].slice(root.href.length);
  });
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
  return files;
}
