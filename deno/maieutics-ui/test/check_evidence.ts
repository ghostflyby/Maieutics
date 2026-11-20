import {
  observeEvidence,
  scrollMutations,
  sourceHashes,
  verifyEvidence,
  verifyObservedEvidence,
} from "./evidence.ts";
import { runBrowserSuite } from "./browser_runner.ts";

const root = new URL("../", import.meta.url);
const path = new URL("evidence/scroll-mutations.json", import.meta.url);
const recording = Deno.args.length === 1 && Deno.args[0] === "--record";
if (Deno.args.length && !recording) throw new Error("Usage: check_evidence.ts [--record]");
const current = await sourceHashes(root);
const record: unknown = recording ? undefined : JSON.parse(await Deno.readTextFile(path));
if (!recording) verifyEvidence(record, current);
const owner = new AbortController();
const cancel = () =>
  owner.abort(new DOMException("Evidence verification interrupted", "AbortError"));
const signals: Deno.Signal[] = Deno.build.os === "windows" ? ["SIGINT"] : ["SIGINT", "SIGTERM"];
for (const signal of signals) Deno.addSignalListener(signal, cancel);
try {
  const observed = await observeEvidence(current, async (mutation) => {
    console.log(
      mutation
        ? `Running negative browser case: ${
          scrollMutations.find((entry) => entry.mutation === mutation)?.name
        }`
        : "Running restored browser suite",
    );
    return await runBrowserSuite({ mutation, signal: owner.signal });
  });
  // Also reject any source changes made while verification was running.
  verifyEvidence(observed, await sourceHashes(root));
  if (recording) await Deno.writeTextFile(path, JSON.stringify(observed, null, 2) + "\n");
  else verifyObservedEvidence(record, observed);
  console.log(
    `UI evidence verified by ${observed.cases.length} executed mutations and ${observed.restored.scenario_count} restored browser scenarios.`,
  );
} finally {
  for (const signal of signals) Deno.removeSignalListener(signal, cancel);
}
