import { scrollMutations, sourceHashes, verifyEvidence } from "./evidence.ts";

const root = new URL("../", import.meta.url);
const evidence = JSON.parse(
  await Deno.readTextFile(new URL("evidence/scroll-mutations.json", import.meta.url)),
);
verifyEvidence(evidence, await sourceHashes(root));
for (const { name, mutation } of scrollMutations) {
  const source = await Deno.readTextFile(new URL(mutation.file, root));
  if (source.split(mutation.before).length - 1 !== mutation.expected_matches) {
    throw new Error(`UI regression evidence: mutation no longer matches once: ${name}`);
  }
}
console.log("UI regression evidence matches the current source and test inputs.");
